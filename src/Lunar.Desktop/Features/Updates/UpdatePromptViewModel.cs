using System.ComponentModel;
using System.Windows.Input;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Updates;
using Lunar.Desktop.Resources;
using Lunar.Desktop.Shell;
using Lunar.Domain.Shared;

namespace Lunar.Desktop.Features.Updates;

/// <summary>
/// The new-version prompt and the update line in Settings (FE-38, ADR-0025). It only talks to <see cref="UpdateService"/>.
/// The prompt is held back while <c>busy</c> says a focus session or a recording is on, and comes when that ends; a download already under way
/// stays visible. After Install now has handed over to Windows Installer, <c>exit</c> closes Lunar so its files can be replaced.
/// Create and use it on the UI thread.
/// </summary>
public sealed class UpdatePromptViewModel : INotifyPropertyChanged, IObserver<UpdateSnapshot>, IDisposable
{
    private const string Component = "UpdatePrompt";

    private readonly UpdateService _updates;
    private readonly Action<Action> _post;
    private readonly Func<bool> _busy;
    private readonly Action _exit;
    private readonly IDiagnosticLog? _log;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IDisposable _subscription;
    private UpdateSnapshot _snapshot;
    private CancellationTokenSource? _install;
    private bool _heldBack;
    private bool _disposed;

    public UpdatePromptViewModel(UpdateService updates, Action<Action> post, Func<bool> busy, Action exit, IDiagnosticLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(busy);
        ArgumentNullException.ThrowIfNull(exit);
        _updates = updates;
        _post = post;
        _busy = busy;
        _exit = exit;
        _log = log;
        _snapshot = updates.Current;
        _heldBack = busy();

        InstallCommand = new AsyncRelayCommand(InstallAsync, () => CanInstall, OnFault, _lifetime.Token);
        LaterCommand = new RelayCommand(_updates.Postpone, () => ShowLater);
        CancelCommand = new RelayCommand(() => _install?.Cancel(), () => IsDownloading);

        _subscription = updates.Changes.Subscribe(this);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand InstallCommand { get; }

    public ICommand LaterCommand { get; }

    public ICommand CancelCommand { get; }

    public UpdatePhase Phase => _snapshot.Phase;

    /// <summary>The bottom-right prompt should be on screen.</summary>
    public bool IsPromptVisible => _snapshot.ShowPrompt && (!_heldBack || _snapshot.Phase is UpdatePhase.Downloading or UpdatePhase.Installing);

    public bool IsDownloading => _snapshot.Phase == UpdatePhase.Downloading;

    public bool IsInstalling => _snapshot.Phase == UpdatePhase.Installing;

    /// <summary>Install now (or Try again) can be pressed: a new version is known, nothing is under way, and no focus session or recording would be cut short.</summary>
    public bool CanInstall => _snapshot.Release is not null && _snapshot.Phase is UpdatePhase.Available or UpdatePhase.Failed && !_heldBack;

    public bool ShowLater => _snapshot.Release is not null && _snapshot.Phase is UpdatePhase.Available or UpdatePhase.Failed;

    public bool HasFailed => _snapshot.Phase == UpdatePhase.Failed;

    /// <summary>0 to 100.</summary>
    public double ProgressPercent => Math.Round(_snapshot.Progress * 100);

    public string Title => Strings.Get("Update.Title");

    public string InstallText => HasFailed ? Strings.Get("Update.TryAgain") : Strings.Get("Update.InstallNow");

    /// <summary>The one line the prompt says.</summary>
    public string Message => _snapshot.Phase switch
    {
        UpdatePhase.Available or UpdatePhase.Failed when _snapshot.Error is null => Strings.Format("Update.Available", Version, _updates.RunningVersion),
        UpdatePhase.Failed => Failure(_snapshot.Error!),
        UpdatePhase.Downloading => Strings.Format("Update.Downloading", ProgressPercent),
        UpdatePhase.Installing => Strings.Get("Update.Installing"),
        _ => Strings.Format("Update.UpToDate", _updates.RunningVersion)
    };

    /// <summary>The line in Settings: the same as the prompt, or the running version when nothing is offered or the check is off.</summary>
    public string StatusText => !_updates.IsEnabled && _snapshot.Phase is UpdatePhase.Idle
        ? Strings.Format("Update.Off", _updates.RunningVersion)
        : _snapshot.Phase == UpdatePhase.Available && _heldBack
            ? Strings.Format("Update.AvailableBusy", Version)
            : Message;

    /// <summary>Said once when the prompt appears or its state changes; never the running percent.</summary>
    public string AnnouncementText => _snapshot.Phase switch
    {
        UpdatePhase.Downloading => Strings.Get("Update.AnnounceDownloading"),
        _ => Message
    };

    private string Version => _snapshot.Release?.Version.ToString() ?? string.Empty;

    /// <summary>A focus session or a recording started or ended, or the switch changed. UI thread only.</summary>
    public void Refresh()
    {
        if (_disposed)
            return;
        _heldBack = _busy();
        RaiseAll();
    }

    public void OnNext(UpdateSnapshot value) => _post(() => Apply(value));

    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _subscription.Dispose();
        _install?.Cancel();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    internal static string Failure(OperationError error) => error.Code switch
    {
        ErrorCode.InvalidInput => Strings.Get("Update.FailedMismatch"),
        ErrorCode.IoFailure => Strings.Get("Update.FailedSave"),
        ErrorCode.DependencyMissing => Strings.Get("Update.FailedStart"),
        _ => Strings.Get("Update.FailedNetwork")
    };

    private async Task InstallAsync(CancellationToken lifetime)
    {
        using var install = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _install = install;
        RaiseAll();
        try
        {
            var result = await _updates.InstallAsync(install.Token);
            if (result.IsSuccess && !_disposed)
            {
                _log?.Write(new DiagnosticEvent(DiagnosticLevel.Information, "update.exit_for_install", Component, "Install"));
                _exit(); // Windows Installer needs Lunar's files free; the MSI starts Lunar again when it is done
            }
        }
        finally
        {
            _install = null;
        }
    }

    private void Apply(UpdateSnapshot snapshot)
    {
        if (_disposed)
            return;
        _snapshot = snapshot;
        _heldBack = _busy();
        RaiseAll();
    }

    private void RaiseAll()
    {
        foreach (var name in new[]
        {
            nameof(Phase), nameof(IsPromptVisible), nameof(IsDownloading), nameof(IsInstalling), nameof(CanInstall), nameof(ShowLater), nameof(HasFailed),
            nameof(ProgressPercent), nameof(InstallText), nameof(Message), nameof(StatusText), nameof(AnnouncementText)
        })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        ((AsyncRelayCommand)InstallCommand).NotifyCanExecuteChanged();
        ((RelayCommand)LaterCommand).NotifyCanExecuteChanged();
        ((RelayCommand)CancelCommand).NotifyCanExecuteChanged();
    }

    private void OnFault(Exception exception) =>
        _log?.Write(new DiagnosticEvent(DiagnosticLevel.Error, "update.command.failed", Component, "Command", ErrorCode.Unexpected, ExceptionType: exception.GetType()));
}
