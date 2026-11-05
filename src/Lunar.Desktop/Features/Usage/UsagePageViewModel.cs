using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Settings;
using Lunar.Application.Abstractions.Usage;
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

    /// <summary>The page is on but no token is stored: it asks for one.</summary>
    NeedsToken,

    /// <summary>The page is on and has a token: it shows the limits or says why it cannot.</summary>
    Active
}

/// <summary>One limit as the page draws it. <see cref="Percent"/> is 0 to 100 for the bar and 0 when the service gave no figure.</summary>
public sealed record UsageLimitView(string Title, double Percent, bool HasFigure, string PercentText, string ResetText, string AutomationName);

/// <summary>
/// What the Usage page shows and can do (FE-27, ADR-0014). It reads the Application layer only: <see cref="UsageMonitor"/> for the limits,
/// <see cref="IUsageTokenStore"/> for the pasted token and <see cref="ISettingsRepository"/> for the opt-in switch; never a Windows or network API.
///
/// Opt-in: nothing is requested until the user turns the page on. Turning it on saves <c>Usage.Enabled</c> and, when a token is stored, starts
/// the monitor; turning it off saves the choice and stops it. The token the user pastes goes straight to the store: this class never keeps it,
/// never puts it in a property, a message or a log line, and the page clears the box whatever the result. Messages shown come from the monitor
/// and the store, which promise fixed text without a token, address or response. A problem keeps the last reading on screen, marked by its time,
/// and never invents a figure. Port callbacks arrive on other threads and are marshalled through <c>post</c>. Use on the UI thread.
/// </summary>
public sealed class UsagePageViewModel : INotifyPropertyChanged, IDisposable
{
    private const string Component = "UsagePage";

    private readonly UsageMonitor _monitor;
    private readonly IUsageTokenStore _tokens;
    private readonly ISettingsRepository _settings;
    private readonly Action<Action> _post;
    private readonly IDiagnosticLog? _log;
    private readonly TimeProvider _time;
    private readonly CultureInfo _culture;
    private readonly CancellationTokenSource _lifetime = new();
    private IDisposable? _subscription;

    private bool _initialized;
    private bool _enabled;
    private bool _hasToken;
    private bool _islandOpen;
    private bool _tokenEntryOpen;
    private bool _disposed;
    private UsageState _state = UsageState.Off;
    private string? _notice;

    public UsagePageViewModel(
        UsageMonitor monitor,
        IUsageTokenStore tokens,
        ISettingsRepository settings,
        Action<Action> post,
        IDiagnosticLog? log = null)
        : this(monitor, tokens, settings, post, log, TimeProvider.System, CultureInfo.CurrentCulture)
    {
    }

    internal UsagePageViewModel(
        UsageMonitor monitor,
        IUsageTokenStore tokens,
        ISettingsRepository settings,
        Action<Action> post,
        IDiagnosticLog? log,
        TimeProvider time,
        CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(culture);
        _monitor = monitor;
        _tokens = tokens;
        _settings = settings;
        _post = post;
        _log = log;
        _time = time;
        _culture = culture;

        var lifetime = _lifetime.Token;
        ToggleEnabledCommand = new AsyncRelayCommand(ToggleEnabledAsync, () => _initialized, OnFault, lifetime);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => Mode == UsagePageMode.Active, OnFault, lifetime);
        RemoveTokenCommand = new AsyncRelayCommand(RemoveTokenAsync, () => _initialized && _hasToken, OnFault, lifetime);
        ChangeTokenCommand = new RelayCommand(() => SetTokenEntry(true), () => Mode == UsagePageMode.Active && !ShowTokenEntry);
        CancelTokenEntryCommand = new RelayCommand(() => SetTokenEntry(false), () => Mode == UsagePageMode.Active && ShowTokenEntry);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ---- what the page shows ----------------------------------------------------------------------------------------

    public UsagePageMode Mode =>
        !_initialized ? UsagePageMode.Loading
        : !_enabled ? UsagePageMode.Off
        : !_hasToken ? UsagePageMode.NeedsToken
        : UsagePageMode.Active;

    /// <summary>The state of the opt-in switch (what was saved, not what was clicked).</summary>
    public bool IsEnabled => _enabled;

    public bool HasToken => _hasToken;

    public bool IsOff => Mode == UsagePageMode.Off;

    public bool IsLoading => Mode == UsagePageMode.Loading;

    public bool IsActive => Mode == UsagePageMode.Active;

    /// <summary>The token field is shown when a token is needed, when the user asked to change it, and when the service refused the stored one.</summary>
    public bool ShowTokenEntry =>
        Mode == UsagePageMode.NeedsToken || (Mode == UsagePageMode.Active && (_tokenEntryOpen || _state.Status == UsageStatus.SignedOut));

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
                UsageStatus.SignedOut => _state.Message ?? "Claude did not accept the token. Paste it again.",
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

    /// <summary>The result of the last token or switch action, in words. Null when there is none.</summary>
    public string? Notice => _notice;

    public string Disclosure =>
        "Turning this on makes Lunar contact Anthropic over HTTPS to read your Claude usage limits, using a token you paste here. "
        + "The token is kept encrypted on this PC and is sent only to Anthropic. Nothing else is sent.";

    public string TokenHelp =>
        "In a terminal run \"claude setup-token\", copy the token it prints and paste it below. Lunar never asks for your password.";

    public ICommand ToggleEnabledCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand RemoveTokenCommand { get; }

    public ICommand ChangeTokenCommand { get; }

    public ICommand CancelTokenEntryCommand { get; }

    // ---- lifetime ---------------------------------------------------------------------------------------------------

    /// <summary>Reads the opt-in choice and whether a token is stored, then starts the monitor when both say so. Call once.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized || _disposed)
            return;

        // Listen first so a change during the reads below is not lost.
        _subscription ??= _monitor.Changes.Subscribe(new Listener(state => _post(() => Apply(state))));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var loaded = await _settings.LoadAsync(linked.Token).ConfigureAwait(false);
        var has = await _tokens.HasTokenAsync(linked.Token).ConfigureAwait(false);
        if (loaded.IsFailure)
            Log(DiagnosticLevel.Warning, "usage.settings.read.failed", "Initialize", loaded.Error!.Code);

        _post(() =>
        {
            if (_disposed)
                return;
            _enabled = loaded.IsSuccess && loaded.Value.Settings.Usage.Enabled;
            _hasToken = has.IsSuccess && has.Value;
            _initialized = true;
            if (_enabled && _hasToken)
                StartMonitor();
            _state = _monitor.Current;
            Changed();
        });
    }

    /// <summary>The island opened or closed; this sets the monitor's pace and refreshes the shown times when it opens.</summary>
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

    /// <summary>
    /// Saves the pasted token. Returns whether it was saved. The text is passed straight to the store and not kept; the caller clears its box.
    /// A rejected text says why (the store's fixed message) and changes nothing else.
    /// </summary>
    public async Task<bool> SaveTokenAsync(string pastedToken, CancellationToken ct = default)
    {
        if (_disposed || !_initialized || !_enabled)
            return false;

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            var saved = await _tokens.SaveAsync(pastedToken ?? string.Empty, linked.Token);
            if (_disposed)
                return false;
            if (saved.IsFailure)
            {
                Log(DiagnosticLevel.Warning, "usage.token.save.failed", "SaveToken", saved.Error!.Code);
                SetNotice(saved.Error!.SafeMessage);
                return false;
            }

            _hasToken = true;
            _tokenEntryOpen = false;
            _notice = "Token saved.";
            StartMonitor();
            Changed();
            // A running monitor that was waiting for a token looks again now.
            var state = await _monitor.RefreshAsync(linked.Token);
            Apply(state);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            OnFault(ex);
            SetNotice("Lunar could not save the token.");
            return false;
        }
    }

    private async Task ToggleEnabledAsync(CancellationToken ct)
    {
        var turnOn = !_enabled;
        var saved = await SaveEnabledAsync(turnOn, ct);
        if (saved.IsFailure)
        {
            Log(DiagnosticLevel.Warning, "usage.enable.failed", "Toggle", saved.Error!.Code);
            SetNotice("Lunar could not save this choice. Nothing changed.");
            return;
        }

        _enabled = turnOn;
        _tokenEntryOpen = false;
        _notice = null;
        if (turnOn)
        {
            if (_hasToken)
                StartMonitor();
        }
        else
        {
            _monitor.Stop();
        }

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

    private async Task RemoveTokenAsync(CancellationToken ct)
    {
        var removed = await _tokens.RemoveAsync(ct);
        if (removed.IsFailure)
        {
            Log(DiagnosticLevel.Warning, "usage.token.remove.failed", "RemoveToken", removed.Error!.Code);
            SetNotice(removed.Error!.SafeMessage);
            return;
        }

        _hasToken = false;
        _tokenEntryOpen = false;
        _monitor.Stop();
        _state = _monitor.Current;
        _notice = "Token removed.";
        Changed();
    }

    private void StartMonitor()
    {
        _monitor.Start();
        _monitor.SetIslandOpen(_islandOpen);
    }

    private void SetTokenEntry(bool open)
    {
        _tokenEntryOpen = open;
        Changed();
    }

    private void SetNotice(string? text)
    {
        _notice = text;
        Changed();
    }

    // ---- state ------------------------------------------------------------------------------------------------------

    // A refusal means the stored token is not usable: the status says so and ShowTokenEntry follows it.
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
        ((AsyncRelayCommand)RemoveTokenCommand).NotifyCanExecuteChanged();
        ((RelayCommand)ChangeTokenCommand).NotifyCanExecuteChanged();
        ((RelayCommand)CancelTokenEntryCommand).NotifyCanExecuteChanged();
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
