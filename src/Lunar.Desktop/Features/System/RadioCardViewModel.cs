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
///
/// The switch never claims a result before Windows has confirmed it (FE-15): <see cref="IsOn"/> only ever comes from a
/// state the service reported, pressing the switch only asks, and while the request is out the card says "Turning on…"
/// (an in-progress status, not a success) and the switch is disabled. When the request ends, whatever the outcome, the
/// card reads the real state again, so a refused or timed-out request leaves the switch where the radio really is and shows
/// why. There is no scan, network list or SSID anywhere on the card.
/// </summary>
public sealed class RadioCardViewModel : INotifyPropertyChanged
{
    private readonly string _subject;
    private readonly Func<bool, CancellationToken, Task<Result>> _setEnabled;
    private readonly Func<CancellationToken, Task<Result>> _openSettings;
    private readonly Func<CancellationToken, Task> _refresh;
    private bool _isLoaded;
    private string _statusText = "Checking…";
    private StatusKind _statusKind = StatusKind.Neutral;
    private bool _isOn;
    private bool _canToggle;
    private bool _showSettingsLink;
    private string? _hint;
    private string? _error;
    private bool _isSwitching;
    private bool _pendingEnable;

    internal RadioCardViewModel(
        string title,
        string subject,
        Func<bool, CancellationToken, Task<Result>> setEnabled,
        Func<CancellationToken, Task<Result>> openSettings,
        Func<CancellationToken, Task> refresh,
        Action<Exception> onFault,
        CancellationToken lifetime)
    {
        Title = title;
        _subject = subject;
        _setEnabled = setEnabled;
        _openSettings = openSettings;
        _refresh = refresh;
        ToggleCommand = new AsyncRelayCommand(ToggleAsync, () => _canToggle && _isLoaded, onFault, lifetime);
        OpenSettingsCommand = new AsyncRelayCommand(OpenSettingsAsync, null, onFault, lifetime);
        DismissErrorCommand = new RelayCommand(() => Error = null);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title { get; }

    /// <summary>The icon in the card's colored tile: the Bluetooth symbol for Bluetooth, the Wi-Fi symbol otherwise.</summary>
    public string Glyph => Title == "Bluetooth" ? "\uE702" : "\uE701";

    /// <summary>False until the first reading arrives; the page shows a placeholder meanwhile.</summary>
    public bool IsLoaded
    {
        get => _isLoaded;
        private set => Set(ref _isLoaded, value);
    }

    /// <summary>The words for the state Windows reported ("Connected", "Off", "Access denied", …).</summary>
    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (Set(ref _statusText, value))
                OnChanged(nameof(ShownStatusText));
        }
    }

    public StatusKind StatusKind
    {
        get => _statusKind;
        private set
        {
            if (Set(ref _statusKind, value))
                OnChanged(nameof(ShownStatusKind));
        }
    }

    /// <summary>What the status chip shows: the confirmed state, or "Turning on…/off…" while a request is out.</summary>
    public string ShownStatusText => _isSwitching ? SystemPageText.Switching(_pendingEnable) : _statusText;

    /// <summary>The chip's kind: an in-progress request is informational, never a success.</summary>
    public StatusKind ShownStatusKind => _isSwitching ? StatusKind.Info : _statusKind;

    /// <summary>True while a switch request is out and Windows has not answered.</summary>
    public bool IsSwitching
    {
        get => _isSwitching;
        private set
        {
            if (!Set(ref _isSwitching, value))
                return;
            OnChanged(nameof(ShownStatusText));
            OnChanged(nameof(ShownStatusKind));
            OnChanged(nameof(IsSwitchEnabled));
        }
    }

    /// <summary>True when the radio is on, as last reported by Windows. Never set by pressing the switch.</summary>
    public bool IsOn
    {
        get => _isOn;
        private set
        {
            if (!Set(ref _isOn, value))
                return;
            OnChanged(nameof(ActionText));
            OnChanged(nameof(SwitchLabel));
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

    /// <summary>The switch can be pressed: offered and no request is already out.</summary>
    public bool IsSwitchEnabled => _canToggle && _isLoaded && !_isSwitching;

    /// <summary>The label of the switch action: what pressing it would do.</summary>
    public string ActionText => _isOn ? "Turn off" : "Turn on";

    /// <summary>The accessible name of the switch: the action and what it acts on.</summary>
    public string SwitchLabel => $"{ActionText} {_subject}";

    /// <summary>True when the card should offer the Settings page instead of (or next to) a switch.</summary>
    public bool ShowSettingsLink
    {
        get => _showSettingsLink;
        private set => Set(ref _showSettingsLink, value);
    }

    /// <summary>One short sentence saying why there is no switch (not supported, denied, held by the system), or null.</summary>
    public string? Hint
    {
        get => _hint;
        private set => Set(ref _hint, value);
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
    internal void Show(string text, StatusKind kind, bool isOn, bool canToggle, bool showSettings, string? hint = null)
    {
        StatusText = text;
        StatusKind = kind;
        IsOn = isOn;
        CanToggle = canToggle;
        ShowSettingsLink = showSettings;
        Hint = hint;
        IsLoaded = true;
        OnChanged(nameof(IsSwitchEnabled));
        ((AsyncRelayCommand)ToggleCommand).NotifyCanExecuteChanged();
    }

    internal void ShowFault(string message)
    {
        Error = message;
        if (!_isLoaded)
            Show("Unknown", StatusKind.Neutral, isOn: false, canToggle: false, showSettings: true, hint: SystemPageText.UnknownHint);
    }

    private async Task ToggleAsync(CancellationToken ct)
    {
        Error = null;
        _pendingEnable = !_isOn;
        IsSwitching = true;
        try
        {
            var result = await _setEnabled(_pendingEnable, ct);
            if (result.IsFailure && !result.IsCancelled)
                Error = SystemPageText.Failure(result.Error!, _subject);

            // Whatever the answer, the switch ends where the radio really is, never where it was asked to go.
            if (!result.IsCancelled)
                await _refresh(ct);
        }
        finally
        {
            IsSwitching = false;
            ((AsyncRelayCommand)ToggleCommand).NotifyCanExecuteChanged();
        }
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
        if (name is nameof(IsLoaded) or nameof(CanToggle))
            OnChanged(nameof(IsSwitchEnabled));
        return true;
    }

    private void OnChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
