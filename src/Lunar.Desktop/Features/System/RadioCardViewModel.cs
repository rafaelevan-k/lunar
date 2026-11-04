using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Lunar.Application.System.Models;
using Lunar.Desktop.Controls;
using Lunar.Desktop.Shell;
using Lunar.Domain.Shared;

namespace Lunar.Desktop.Features.SystemControls;

/// <summary>
/// One radio card of the System page (Wi-Fi or Bluetooth): what to show for the current state and the two things a user
/// can do (switch it, open its Settings page). It holds no Windows API: the actions are delegates the page view model
/// supplies, and the state is pushed in by <see cref="Show"/>. Use it on the UI thread.
/// </summary>
public sealed class RadioCardViewModel : INotifyPropertyChanged
{
    private readonly string _subject;
    private readonly Func<bool, CancellationToken, Task<Result>> _setEnabled;
    private readonly Func<CancellationToken, Task<Result>> _openSettings;
    private bool _isLoaded;
    private string _statusText = "Checking…";
    private StatusKind _statusKind = StatusKind.Neutral;
    private bool _isOn;
    private bool _canToggle;
    private bool _showSettingsLink;
    private string? _error;

    internal RadioCardViewModel(
        string title,
        string subject,
        Func<bool, CancellationToken, Task<Result>> setEnabled,
        Func<CancellationToken, Task<Result>> openSettings,
        Action<Exception> onFault,
        CancellationToken lifetime)
    {
        Title = title;
        _subject = subject;
        _setEnabled = setEnabled;
        _openSettings = openSettings;
        ToggleCommand = new AsyncRelayCommand(ToggleAsync, () => _canToggle && _isLoaded, onFault, lifetime);
        OpenSettingsCommand = new AsyncRelayCommand(OpenSettingsAsync, null, onFault, lifetime);
        DismissErrorCommand = new RelayCommand(() => Error = null);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title { get; }

    /// <summary>False until the first reading arrives; the page shows a placeholder meanwhile.</summary>
    public bool IsLoaded
    {
        get => _isLoaded;
        private set => Set(ref _isLoaded, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public StatusKind StatusKind
    {
        get => _statusKind;
        private set => Set(ref _statusKind, value);
    }

    /// <summary>True when the radio is on.</summary>
    public bool IsOn
    {
        get => _isOn;
        private set
        {
            if (Set(ref _isOn, value))
                OnChanged(nameof(ActionText));
        }
    }

    /// <summary>True when Windows lets this app switch the radio right now.</summary>
    public bool CanToggle
    {
        get => _canToggle;
        private set
        {
            if (Set(ref _canToggle, value))
                ((AsyncRelayCommand)ToggleCommand).NotifyCanExecuteChanged();
        }
    }

    /// <summary>The label of the switch action: what pressing it would do.</summary>
    public string ActionText => _isOn ? "Turn off" : "Turn on";

    /// <summary>True when the card should offer the Settings page instead of (or next to) a switch.</summary>
    public bool ShowSettingsLink
    {
        get => _showSettingsLink;
        private set => Set(ref _showSettingsLink, value);
    }

    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    public ICommand ToggleCommand { get; }

    public ICommand OpenSettingsCommand { get; }

    public ICommand DismissErrorCommand { get; }

    /// <summary>Shows a state (or the answer to "could not be read").</summary>
    internal void Show(string text, StatusKind kind, bool isOn, bool canToggle, bool showSettings)
    {
        StatusText = text;
        StatusKind = kind;
        IsOn = isOn;
        CanToggle = canToggle;
        ShowSettingsLink = showSettings;
        IsLoaded = true;
        ((AsyncRelayCommand)ToggleCommand).NotifyCanExecuteChanged();
    }

    internal void ShowFault(string message)
    {
        Error = message;
        if (!_isLoaded)
            Show("Unknown", StatusKind.Neutral, isOn: false, canToggle: false, showSettings: true);
    }

    private async Task ToggleAsync(CancellationToken ct)
    {
        Error = null;
        var result = await _setEnabled(!_isOn, ct);
        if (result.IsFailure && !result.IsCancelled)
            Error = SystemPageText.Failure(result.Error!, _subject);
    }

    private async Task OpenSettingsAsync(CancellationToken ct)
    {
        var result = await _openSettings(ct);
        if (result.IsFailure && !result.IsCancelled)
            Error = "Couldn't open Settings.";
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnChanged(name);
        return true;
    }

    private void OnChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
