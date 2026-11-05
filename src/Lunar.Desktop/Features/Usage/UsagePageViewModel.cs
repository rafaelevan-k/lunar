using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Settings;
using Lunar.Application.Settings.Models;
using Lunar.Application.Usage;
using Lunar.Application.Usage.Models;
using Lunar.Desktop.Shell;
using Lunar.Domain.Shared;

namespace Lunar.Desktop.Features.Usage;

/// <summary>Which face the Usage page shows.</summary>
public enum UsagePageMode
{
    /// <summary>The settings have not been read yet.</summary>
    Loading,

    /// <summary>The page is off: it says what turning it on does and offers the switch. Nothing is requested.</summary>
    Off,

    /// <summary>The page is on: it shows the limits, or says why it cannot (no Claude Code sign-in, expired, refused, no network).</summary>
    Active
}

/// <summary>One limit as the page draws it. <see cref="Percent"/> is 0 to 100 for the bar and 0 when the service gave no figure.</summary>
public sealed record UsageLimitView(string Title, double Percent, bool HasFigure, string PercentText, string ResetText, string AutomationName);

/// <summary>
/// What the Usage page shows and can do (FE-27, FE-29, ADR-0014 and ADR-0015). It reads the Application layer only: <see cref="UsageMonitor"/>
/// for the limits and <see cref="ISettingsRepository"/> for the opt-in switch; never a Windows or network API.
///
/// Opt-in: nothing is requested until the user turns the page on. Turning it on saves <c>Usage.Enabled</c> and starts the monitor; turning it
/// off saves the choice and stops it. The login is the user's own Claude Code sign-in, which the credential source reads (read-only) for each
/// request; this class never sees a token. When that login is missing, expired or refused the monitor says so in a fixed sentence and the
/// page shows it in its own card, with the Refresh button: the monitor also tries again by itself when the island opens. A problem keeps the
/// last reading on screen, marked by its time, and never invents a figure. Port callbacks arrive on other threads and are marshalled through
/// <c>post</c>. Use on the UI thread.
/// </summary>
public sealed class UsagePageViewModel : INotifyPropertyChanged, IDisposable
{
    private const string Component = "UsagePage";

    private readonly UsageMonitor _monitor;
    private readonly ISettingsRepository _settings;
    private readonly Action<Action> _post;
    private readonly IDiagnosticLog? _log;
    private readonly TimeProvider _time;
    private readonly CultureInfo _culture;
    private readonly CancellationTokenSource _lifetime = new();
    private IDisposable? _subscription;

    private bool _initialized;
    private bool _enabled;
    private bool _islandOpen;
    private bool _disposed;
    private UsageState _state = UsageState.Off;
    private string? _notice;

    public UsagePageViewModel(UsageMonitor monitor, ISettingsRepository settings, Action<Action> post, IDiagnosticLog? log = null)
        : this(monitor, settings, post, log, TimeProvider.System, CultureInfo.CurrentCulture)
    {
    }

    internal UsagePageViewModel(
        UsageMonitor monitor,
        ISettingsRepository settings,
        Action<Action> post,
        IDiagnosticLog? log,
        TimeProvider time,
        CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(culture);
        _monitor = monitor;
        _settings = settings;
        _post = post;
        _log = log;
        _time = time;
        _culture = culture;

        var lifetime = _lifetime.Token;
        ToggleEnabledCommand = new AsyncRelayCommand(ToggleEnabledAsync, () => _initialized, OnFault, lifetime);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => Mode == UsagePageMode.Active, OnFault, lifetime);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ---- what the page shows ----------------------------------------------------------------------------------------

    public UsagePageMode Mode =>
        !_initialized ? UsagePageMode.Loading
        : !_enabled ? UsagePageMode.Off
        : UsagePageMode.Active;

    /// <summary>The state of the opt-in switch (what was saved, not what was clicked).</summary>
    public bool IsEnabled => _enabled;

    public bool IsOff => Mode == UsagePageMode.Off;

    public bool IsLoading => Mode == UsagePageMode.Loading;

    public bool IsActive => Mode == UsagePageMode.Active;

    public bool ShowLimits => Mode == UsagePageMode.Active && _state.Snapshot is not null;

    public UsageLimitView FiveHour => Limit("5-hour limit", _state.Snapshot?.FiveHour);

    public UsageLimitView Weekly => Limit("Weekly limit", _state.Snapshot?.Weekly);

    /// <summary>One line under the limits: when the reading was taken, or what is wrong. Null when there is nothing to say.</summary>
    public string? StatusText
    {
        get
        {
            if (Mode != UsagePageMode.Active)
                return null;
            var snapshot = _state.Snapshot;
            return _state.Status switch
            {
                UsageStatus.Ready when snapshot is not null => UsageText.Updated(snapshot.FetchedAt, _culture),
                UsageStatus.SignedOut => _state.Message ?? "Claude Code is not signed in on this PC.",
                UsageStatus.Unavailable or UsageStatus.Unreadable =>
                    (_state.Message ?? "The limits cannot be read right now.")
                    + (snapshot is null ? string.Empty : " Showing the " + UsageText.Updated(snapshot.FetchedAt, _culture).ToLowerInvariant() + "."),
                _ => "Reading your limits…"
            };
        }
    }

    /// <summary>The status is a problem (so the page marks it with a symbol as well as words).</summary>
    public bool StatusIsProblem =>
        Mode == UsagePageMode.Active && _state.Status is UsageStatus.SignedOut or UsageStatus.Unavailable or UsageStatus.Unreadable;

    /// <summary>The sign-in is missing, expired or refused: the page shows the reason in a card of its own.</summary>
    public bool ShowLoginProblem => Mode == UsagePageMode.Active && _state.Status == UsageStatus.SignedOut;

    /// <summary>The words of that card.</summary>
    public string? LoginProblemText => ShowLoginProblem ? StatusText : null;

    /// <summary>The slim line under the cards; hidden while the login card says the same thing.</summary>
    public bool ShowStatusLine => StatusText is not null && !ShowLoginProblem;

    /// <summary>The result of the last switch action, in words, when it did not work. Null when there is none.</summary>
    public string? Notice => _notice;

    public string Disclosure =>
        "Lunar reads the Claude Code sign-in on this PC (read-only) and asks Anthropic over HTTPS for your usage limits. Nothing else is sent.";

    public ICommand ToggleEnabledCommand { get; }

    public ICommand RefreshCommand { get; }

    // ---- lifetime ---------------------------------------------------------------------------------------------------

    /// <summary>Reads the opt-in choice and starts the monitor when it is on. Call once.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized || _disposed)
            return;

        // Listen first so a change during the read below is not lost.
        _subscription ??= _monitor.Changes.Subscribe(new Listener(state => _post(() => Apply(state))));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var loaded = await _settings.LoadAsync(linked.Token).ConfigureAwait(false);
        if (loaded.IsFailure)
            Log(DiagnosticLevel.Warning, "usage.settings.read.failed", "Initialize", loaded.Error!.Code);

        _post(() =>
        {
            if (_disposed)
                return;
            _enabled = loaded.IsSuccess && loaded.Value.Settings.Usage.Enabled;
            _initialized = true;
            if (_enabled)
                StartMonitor();
            _state = _monitor.Current;
            Changed();
        });
    }

    /// <summary>The island opened or closed; this sets the monitor's pace (and lets it try again after a sign-in problem) and refreshes the shown times.</summary>
    public void SetIslandOpen(bool open)
    {
        if (_disposed)
            return;
        _islandOpen = open;
        _monitor.SetIslandOpen(open);
        if (open)
            Changed(); // the "resets in" text is recomputed from the clock
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lifetime.Cancel();
        _subscription?.Dispose();
        _subscription = null;
        _lifetime.Dispose();
    }

    // ---- actions ----------------------------------------------------------------------------------------------------

    private async Task ToggleEnabledAsync(CancellationToken ct)
    {
        var turnOn = !_enabled;
        var saved = await SaveEnabledAsync(turnOn, ct);
        if (saved.IsFailure)
        {
            Log(DiagnosticLevel.Warning, "usage.enable.failed", "Toggle", saved.Error!.Code);
            _notice = "Lunar could not save this choice. Nothing changed.";
            Changed();
            return;
        }

        _enabled = turnOn;
        _notice = null;
        if (turnOn)
            StartMonitor();
        else
            _monitor.Stop();

        _state = _monitor.Current;
        Changed();
    }

    private async Task<Result> SaveEnabledAsync(bool enabled, CancellationToken ct)
    {
        var loaded = await _settings.LoadAsync(ct);
        if (loaded.IsFailure)
            return Result.Failure(loaded.Error!);
        var next = loaded.Value.Settings with { Usage = new UsageSettings { Enabled = enabled } };
        return await _settings.SaveAsync(next, ct);
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var state = await _monitor.RefreshAsync(ct);
        Apply(state);
    }

    private void StartMonitor()
    {
        _monitor.Start();
        _monitor.SetIslandOpen(_islandOpen);
    }

    // ---- state ------------------------------------------------------------------------------------------------------

    private void Apply(UsageState state)
    {
        if (_disposed)
            return;
        _state = state;
        Changed();
    }

    private UsageLimitView Limit(string title, UsageWindow? window)
    {
        var now = _time.GetUtcNow();
        return new UsageLimitView(
            title,
            window?.UsedPercent ?? 0,
            window?.UsedPercent is not null,
            UsageText.Percent(window),
            UsageText.Reset(window, now, _culture),
            UsageText.Spoken(title, window, now, _culture));
    }

    private void Changed()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty)); // every property: the page is small
        ((AsyncRelayCommand)ToggleEnabledCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)RefreshCommand).NotifyCanExecuteChanged();
    }

    private void OnFault(Exception ex) => Log(DiagnosticLevel.Error, "usagepage.fault", "Command", ErrorCode.Unexpected, ex.GetType());

    private void Log(DiagnosticLevel level, string eventId, string operation, ErrorCode? code = null, Type? exception = null) =>
        _log?.Write(new DiagnosticEvent(level, eventId, Component, operation, code, ExceptionType: exception));

    private sealed class Listener(Action<UsageState> onNext) : IObserver<UsageState>
    {
        public void OnNext(UsageState value) => onNext(value);

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }
}
