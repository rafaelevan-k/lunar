using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.System;
using Lunar.Application.System.Models;
using Lunar.Application.System.UseCases;
using Lunar.Desktop.Resources;
using Lunar.Desktop.Shell;
using Lunar.Domain.Shared;

namespace Lunar.Desktop.Features.SystemControls;

/// <summary>
/// The brightness card on the System page (FE-32, ADR-0021), for the display the island is on. It talks only to the Application port
/// (<see cref="IBrightnessService"/>, writes through <see cref="BrightnessWriter"/>) and the Settings launcher.
/// <list type="bullet">
/// <item>Dragging is optimistic: the slider shows the hand's value at once and the writer sends it, throttled. A refused write rolls the
/// slider back to the value read from the display and says why.</item>
/// <item>Outside changes (keys, Windows Settings) are followed: a built-in panel announces them, an external monitor is read again every few
/// seconds, but only while the card is visible.</item>
/// <item>For a moment after its own write the card does not re-read (Windows reports the new value with a delay), so the slider never
/// jumps back and forth; while the hand is on the slider nothing from outside moves it.</item>
/// <item>A display that cannot be changed shows a short reason and "Display settings" instead of a slider.</item>
/// </list>
/// Create and use it on the UI thread; results from the port are marshaled with <c>post</c>.
/// </summary>
public sealed class BrightnessCardViewModel : INotifyPropertyChanged, IDisposable
{
    private const string Component = "BrightnessCard";
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan QuietAfterWrite = TimeSpan.FromSeconds(1.5);

    private readonly IBrightnessService _service;
    private readonly BrightnessWriter _writer;
    private readonly Func<string?> _display;
    private readonly Action<Action> _post;
    private readonly IOneShotTimer _poll;
    private readonly TimeProvider _time;
    private readonly IDiagnosticLog? _log;
    private readonly CancellationTokenSource _lifetime = new();

    private BrightnessState? _state;
    private string? _readNote;
    private bool _readDenied;
    private double _percent;
    private bool _adjusting;
    private bool _visible;
    private string? _error;
    private long _quietUntil;
    private int _readVersion;
    private bool _disposed;

    internal BrightnessCardViewModel(
        IBrightnessService service,
        BrightnessWriter writer,
        Func<string?> display,
        ISystemSettingsLauncher settings,
        Action<Action> post,
        IOneShotTimer poll,
        TimeProvider? time = null,
        IDiagnosticLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(poll);
        _service = service;
        _writer = writer;
        _display = display;
        _post = post;
        _poll = poll;
        _time = time ?? TimeProvider.System;
        _log = log;
        OpenSettingsCommand = new AsyncRelayCommand(async ct =>
        {
            var opened = await settings.OpenAsync(SettingsPage.Display, ct);
            if (opened.IsFailure && !opened.IsCancelled)
                Error = Strings.Get("System.OpenSettingsFailed");
        }, null, OnFault, _lifetime.Token);
        DismissErrorCommand = new RelayCommand(() => Error = null);
        _service.Changed += OnOutsideChange;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ---- what the card shows ------------------------------------------------------------------------------------------

    /// <summary>False until the first read finished: the card shows a placeholder.</summary>
    public bool IsLoaded => _state is not null || _readNote is not null;

    /// <summary>The slider is shown: the display's brightness can be changed from here.</summary>
    public bool IsAvailable => _state?.Capability == CapabilityStatus.Supported && _readNote is null;

    /// <summary>Why there is no slider; null when there is one (or before the first read).</summary>
    public string? Note => !IsLoaded || IsAvailable ? null : _readNote ?? NoteFor(_state!.Capability);

    /// <summary>The reason in a few words, for the one-line row; <see cref="Note"/> is the full sentence (tooltip, screen reader).</summary>
    public string? ShortNote => Note is null
        ? null
        : _readNote is not null
            ? Strings.Get(_readDenied ? "System.BrightnessShortDenied" : "System.BrightnessShortReadFailed")
            : _state!.Capability switch
            {
                CapabilityStatus.Unavailable => Strings.Get("System.BrightnessShortNoDisplay"),
                CapabilityStatus.PermissionDenied => Strings.Get("System.BrightnessShortDenied"),
                _ => Strings.Get("System.BrightnessShortUnsupported")
            };

    /// <summary>Settings &gt; Display is offered in place of the slider.</summary>
    public bool ShowSettingsLink => IsLoaded && !IsAvailable;

    /// <summary>The slider value, 0–100. Setting it (the hand on the slider, keys, wheel) writes it, throttled.</summary>
    public double Percent
    {
        get => _percent;
        set
        {
            var clamped = Math.Clamp(Math.Round(value), 0, 100);
            if (!IsAvailable || clamped == _percent)
                return;
            _percent = clamped;
            Raise(nameof(Percent));
            Raise(nameof(PercentText));
            Write((int)clamped);
        }
    }

    public string PercentText => _percent.ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <summary>Set by the slider while the hand holds it; nothing from outside moves the slider meanwhile.</summary>
    public bool IsAdjusting
    {
        get => _adjusting;
        set
        {
            if (_adjusting == value)
                return;
            _adjusting = value;
            Raise(nameof(IsAdjusting));
        }
    }

    /// <summary>What went wrong with the last change, in words; null when nothing did.</summary>
    public string? Error
    {
        get => _error;
        private set
        {
            if (_error == value)
                return;
            _error = value;
            Raise(nameof(Error));
        }
    }

    public ICommand OpenSettingsCommand { get; }

    public ICommand DismissErrorCommand { get; }

    // ---- lifetime -----------------------------------------------------------------------------------------------------

    /// <summary>Reads the display once. Call when the page starts.</summary>
    public Task InitializeAsync(CancellationToken ct = default) => ReadAsync(force: true, ct);

    /// <summary>
    /// The card became visible (island open on the System page) or hidden. Visible re-reads (the island may have moved to another display)
    /// and, for an external monitor, reads again every few seconds; hidden stops that.
    /// </summary>
    internal void SetVisible(bool visible)
    {
        if (_disposed || _visible == visible)
            return;
        _visible = visible;
        if (visible)
            _ = ReadAsync(force: false, _lifetime.Token);
        else
            _poll.Cancel();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _service.Changed -= OnOutsideChange;
        _poll.Cancel();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    // ---- reading and writing ----------------------------------------------------------------------------------------------

    private async Task ReadAsync(bool force, CancellationToken ct)
    {
        if (_disposed || (!force && (_adjusting || _time.GetTimestamp() < Interlocked.Read(ref _quietUntil))))
        {
            SchedulePoll();
            return;
        }

        var version = Interlocked.Increment(ref _readVersion);
        Result<BrightnessState> read;
        try
        {
            read = await _service.GetAsync(_display(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _post(() =>
        {
            if (_disposed || version != Volatile.Read(ref _readVersion) || read.IsCancelled)
                return;
            if (read.IsSuccess)
            {
                _state = read.Value;
                _readNote = null;
                if (!_adjusting || force)
                    _percent = read.Value.Percent;
            }
            else
            {
                _readDenied = read.Error!.Code == ErrorCode.AccessDenied;
                _readNote = _readDenied
                    ? Strings.Get("System.BrightnessDenied")
                    : Strings.Get("System.BrightnessReadFailed");
                Log(DiagnosticLevel.Warning, "brightness.card.read.failed", "Read", read.Error.Code);
            }

            Changed();
            SchedulePoll();
        });
    }

    private void Write(int percent)
    {
        Interlocked.Exchange(ref _quietUntil, _time.GetTimestamp() + (long)(QuietAfterWrite.TotalSeconds * _time.TimestampFrequency));
        Error = null;
        var display = _display();
        _ = WriteAsync(display, percent);
    }

    // Never faults: every outcome is shown, and a refused write is rolled back to what the display really has.
    private async Task WriteAsync(string? display, int percent)
    {
        Result result;
        try
        {
            result = await _writer.RequestAsync(display, percent, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            result = Result.Failure(new OperationError(ErrorCode.Unexpected, "The brightness could not be changed."));
            Log(DiagnosticLevel.Error, "brightness.card.write.fault", "Write", ErrorCode.Unexpected, ex.GetType());
        }

        if (result.IsSuccess || result.IsCancelled)
            return;
        Log(DiagnosticLevel.Warning, "brightness.card.write.failed", "Write", result.Error!.Code);
        _post(() =>
        {
            if (_disposed)
                return;
            Error = result.Error!.Code == ErrorCode.AccessDenied
                ? Strings.Get("System.BrightnessChangeDenied")
                : Strings.Get("System.BrightnessChangeFailed");
            Interlocked.Exchange(ref _quietUntil, 0);
            _ = ReadAsync(force: true, _lifetime.Token); // roll back to the authoritative value
        });
    }

    private void SchedulePoll()
    {
        if (_disposed || !_visible || _state?.Source != BrightnessSource.External)
            return;
        _poll.Schedule(PollInterval, () => _ = ReadAsync(force: false, _lifetime.Token));
    }

    private void OnOutsideChange(string displayId) => _post(() =>
    {
        var shown = _display();
        if (_disposed || (shown is not null && !string.Equals(shown, displayId, StringComparison.OrdinalIgnoreCase)))
            return;
        _ = ReadAsync(force: false, _lifetime.Token);
    });

    internal static string NoteFor(CapabilityStatus capability) => capability switch
    {
        CapabilityStatus.Unavailable => Strings.Get("System.BrightnessNoDisplay"),
        CapabilityStatus.PermissionDenied => Strings.Get("System.BrightnessDenied"),
        _ => Strings.Get("System.BrightnessUnsupported")
    };

    private void Changed()
    {
        foreach (var name in new[] { nameof(IsLoaded), nameof(IsAvailable), nameof(Note), nameof(ShortNote), nameof(ShowSettingsLink), nameof(Percent), nameof(PercentText) })
            Raise(name);
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void OnFault(Exception ex) => Log(DiagnosticLevel.Error, "brightness.card.fault", "Command", ErrorCode.Unexpected, ex.GetType());

    private void Log(DiagnosticLevel level, string eventId, string operation, ErrorCode? code = null, Type? exception = null) =>
        _log?.Write(new DiagnosticEvent(level, eventId, Component, operation, code, null, exception));
}
