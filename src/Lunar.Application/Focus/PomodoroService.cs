using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.System;
using Lunar.Application.Reactive;
using Lunar.Application.Settings;
using Lunar.Domain.Focus;
using Lunar.Domain.Shared;

namespace Lunar.Application.Focus;

/// <summary>
/// The one focus timer (ADR-0023). It counts toward a stored end time, so a late tick or a sleep corrects itself and time asleep counts as elapsed. It turns
/// Do Not Disturb on while the timer runs (not while paused) and puts back what was there: a user who already had it on keeps it, and a change made by hand during the session is
/// respected. A marker on disk repairs an unexpected stop at the next start. Calls are serialized; <see cref="Changes"/> follows ADR-0006.
/// </summary>
public sealed class PomodoroService : IDisposable
{
    internal static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private const string Component = "Pomodoro";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _pendingLock = new();
    private readonly StateBroadcaster<PomodoroSnapshot> _changes = new();
    private readonly IFocusModeControl _focus;
    private readonly IFocusSessionMarker _marker;
    private readonly SettingsService _settings;
    private readonly IClock _clock;
    private readonly IDiagnosticLog? _log;
    private readonly TimeProvider _time;
    private PomodoroSnapshot _current = PomodoroSnapshot.Initial;
    private ITimer? _ticker;
    private Task _pendingFinish = Task.CompletedTask;
    private DateTimeOffset _endsAt;
    private TimeSpan _pausedRemaining;
    private string? _restoreToken;
    private string? _turnedOnToken;
    private int _finishRequested;
    private bool _disposed;

    public PomodoroService(IFocusModeControl focus, IFocusSessionMarker marker, SettingsService settings, IClock clock, IDiagnosticLog? log = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(focus);
        ArgumentNullException.ThrowIfNull(marker);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        _focus = focus;
        _marker = marker;
        _settings = settings;
        _clock = clock;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    public PomodoroSnapshot Current => Volatile.Read(ref _current);

    public IObservable<PomodoroSnapshot> Changes => _changes;

    /// <summary>A session is running or paused. Safe from any thread.</summary>
    public bool IsSessionActive => FocusTimerRules.IsActive(Current.Phase);

    /// <summary>The minutes to offer next: the last ones used.</summary>
    public int LastMinutes => _settings.Current.Focus.LastMinutes;

    /// <summary>Time left in the session, from the clock; zero when there is none. Safe from the UI thread.</summary>
    public TimeSpan Remaining => Current.Phase switch
    {
        PomodoroPhase.Running => Max(_endsAt - _clock.LocalNow),
        PomodoroPhase.Paused => _pausedRemaining,
        _ => TimeSpan.Zero
    };

    /// <summary>Puts back a Do Not Disturb that an unexpected stop left on. Call once at start-up.</summary>
    public async Task<Result> RecoverAsync(CancellationToken ct)
    {
        if (!await EnterAsync(ct).ConfigureAwait(false))
            return Result.Cancelled();
        try
        {
            var marker = await _marker.ReadAsync(ct).ConfigureAwait(false);
            if (marker.IsFailure || marker.Value is null)
                return marker.IsFailure ? Result.Failure(marker.Error!) : Result.Success();

            var restored = await _focus.RestoreAsync(marker.Value, ct).ConfigureAwait(false);
            if (restored.IsFailure && restored.Error!.Code != ErrorCode.InvalidInput)
            {
                Log(DiagnosticLevel.Warning, "pomodoro.recover.failed", "Recover", restored.Error.Code);
                return restored;
            }

            Log(DiagnosticLevel.Information, "pomodoro.recovered", "Recover");
            return await _marker.ClearAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Starts a session. While one is under way nothing happens. A Do Not Disturb that cannot be turned on leaves the timer running and is told.</summary>
    public async Task<Result> StartAsync(int minutes, CancellationToken ct)
    {
        if (!FocusTimerRules.IsValid(minutes))
            return Result.Failure(new OperationError(ErrorCode.InvalidInput, "Choose between 1 and 180 minutes."));
        if (!await EnterAsync(ct).ConfigureAwait(false))
            return Result.Cancelled();
        try
        {
            if (IsSessionActive || _disposed)
                return Result.Success();

            _endsAt = _clock.LocalNow + TimeSpan.FromMinutes(minutes);
            StartTicking();
            Publish(new PomodoroSnapshot(PomodoroPhase.Running, FocusModeStatus.TurningOn));
            Log(DiagnosticLevel.Information, "pomodoro.started", "Start");
            await _settings.UpdateAsync(s => s with { Focus = s.Focus with { LastMinutes = minutes } }, ct).ConfigureAwait(false);
            await TurnFocusOnAsync(ct).ConfigureAwait(false);
            return Result.Success();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Holds the time left and puts Do Not Disturb back as it was until <see cref="ResumeAsync"/> (the owner's decision, 2026-11-11). One that was
    /// already on before the session is left alone.
    /// </summary>
    public async Task<Result> PauseAsync(CancellationToken ct)
    {
        if (!await EnterAsync(ct).ConfigureAwait(false))
            return Result.Cancelled();
        try
        {
            if (Current.Phase != PomodoroPhase.Running)
                return Result.Success();
            _pausedRemaining = Max(_endsAt - _clock.LocalNow);
            StopTicking();
            var alreadyOn = Current.FocusStatus == FocusModeStatus.AlreadyOn;
            await RestoreFocusAsync().ConfigureAwait(false);
            Publish(new PomodoroSnapshot(PomodoroPhase.Paused, alreadyOn ? FocusModeStatus.AlreadyOn : FocusModeStatus.NotRequested));
            return Result.Success();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Result> ResumeAsync(CancellationToken ct)
    {
        if (!await EnterAsync(ct).ConfigureAwait(false))
            return Result.Cancelled();
        try
        {
            if (Current.Phase != PomodoroPhase.Paused)
                return Result.Success();
            _endsAt = _clock.LocalNow + _pausedRemaining;
            StartTicking();
            Publish(new PomodoroSnapshot(PomodoroPhase.Running, FocusModeStatus.TurningOn));
            await TurnFocusOnAsync(ct).ConfigureAwait(false);
            return Result.Success();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Ends the session early and puts Do Not Disturb back. Not cancellable.</summary>
    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsSessionActive)
                await EndAsync(PomodoroPhase.Idle).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Lunar is closing: Do Not Disturb is put back.</summary>
    public async Task ShutdownAsync()
    {
        Task pending;
        lock (_pendingLock)
            pending = _pendingFinish;
        await pending.ConfigureAwait(false);
        await StopAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _ticker?.Dispose();
        _changes.Complete();
    }

    private async Task TurnFocusOnAsync(CancellationToken ct)
    {
        var earlier = await _focus.ReadAsync(ct).ConfigureAwait(false);
        if (earlier.IsFailure)
        {
            ReportUnavailable(earlier.Error!);
            return;
        }

        if (earlier.Value.IsOn)
        {
            Publish(Current with { FocusStatus = FocusModeStatus.AlreadyOn });
            return;
        }

        // The marker goes first: if Lunar stops right after turning it on, the next start can still put it back.
        var marked = await _marker.SaveAsync(earlier.Value.RestoreToken, ct).ConfigureAwait(false);
        if (marked.IsFailure)
        {
            ReportUnavailable(marked.Error!);
            return;
        }

        var on = await _focus.TurnOnAsync(ct).ConfigureAwait(false);
        if (on.IsFailure)
        {
            await _marker.ClearAsync(CancellationToken.None).ConfigureAwait(false);
            ReportUnavailable(on.Error!);
            return;
        }

        var now = await _focus.ReadAsync(ct).ConfigureAwait(false);
        _restoreToken = earlier.Value.RestoreToken;
        _turnedOnToken = now.IsSuccess ? now.Value.RestoreToken : null;
        Publish(Current with { FocusStatus = FocusModeStatus.On });
    }

    private void ReportUnavailable(OperationError error)
    {
        Log(DiagnosticLevel.Warning, "pomodoro.focus.unavailable", "Start", error.Code);
        if (error.Code == ErrorCode.Cancelled)
            Publish(Current with { FocusStatus = FocusModeStatus.NotRequested });
        else
            Publish(Current with { FocusStatus = FocusModeStatus.Unavailable, FocusError = error });
    }

    // Under the gate.
    private async Task EndAsync(PomodoroPhase next)
    {
        StopTicking();
        await RestoreFocusAsync().ConfigureAwait(false);
        Log(DiagnosticLevel.Information, next == PomodoroPhase.Finished ? "pomodoro.finished" : "pomodoro.stopped", "End");
        Publish(new PomodoroSnapshot(next));
    }

    private async Task RestoreFocusAsync()
    {
        if (_restoreToken is not { } original)
            return;
        _restoreToken = null;

        // If the user changed it by hand during the session, that choice stands.
        var now = await _focus.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        if (now.IsSuccess && _turnedOnToken is not null && now.Value.RestoreToken != _turnedOnToken)
        {
            await _marker.ClearAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var restored = await _focus.RestoreAsync(original, CancellationToken.None).ConfigureAwait(false);
        if (restored.IsFailure)
        {
            // The marker stays, so the next start tries again.
            Log(DiagnosticLevel.Warning, "pomodoro.restore.failed", "End", restored.Error!.Code);
            return;
        }

        await _marker.ClearAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private void StartTicking()
    {
        Volatile.Write(ref _finishRequested, 0);
        _ticker?.Dispose();
        _ticker = _time.CreateTimer(_ => OnTick(), null, TickInterval, TickInterval);
    }

    private void StopTicking()
    {
        _ticker?.Dispose();
        _ticker = null;
    }

    private void OnTick()
    {
        if (Current.Phase != PomodoroPhase.Running || _clock.LocalNow < _endsAt || Interlocked.Exchange(ref _finishRequested, 1) != 0)
            return;
        lock (_pendingLock)
            _pendingFinish = _pendingFinish.ContinueWith(_ => FinishAsync(), TaskScheduler.Default).Unwrap();
    }

    private async Task FinishAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Current.Phase == PomodoroPhase.Running)
                await EndAsync(PomodoroPhase.Finished).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log(DiagnosticLevel.Error, "pomodoro.finish.crashed", "End", ErrorCode.Unexpected, ex.GetType());
            Publish(new PomodoroSnapshot(PomodoroPhase.Finished));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> EnterAsync(CancellationToken ct)
    {
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void Publish(PomodoroSnapshot snapshot)
    {
        Volatile.Write(ref _current, snapshot);
        _changes.Publish(snapshot);
    }

    private void Log(DiagnosticLevel level, string eventId, string operation, ErrorCode? error = null, Type? exception = null) =>
        _log?.Write(new DiagnosticEvent(level, eventId, Component, operation, error, ExceptionType: exception));

    private static TimeSpan Max(TimeSpan time) => time < TimeSpan.Zero ? TimeSpan.Zero : time;
}
