using Lunar.Application.Abstractions.Usage;
using Lunar.Application.Reactive;
using Lunar.Application.Usage.Models;
using Lunar.Domain.Shared;

namespace Lunar.Application.Usage;

/// <summary>
/// Looks at the account's usage limits on a schedule and tells a screen when one resets (ADR-0014, BE-32). It does nothing until
/// <see cref="Start"/> and nothing after <see cref="Stop"/>: the caller starts it only when the user turned the Usage page on, so no request is
/// made before that.
///
/// Schedule: one request in flight at any time; every <see cref="UsageMonitorOptions.OpenInterval"/> while the island is open and every
/// <see cref="UsageMonitorOptions.ClosedInterval"/> while it is closed (<see cref="SetIslandOpen"/>); never closer than
/// <see cref="UsageMonitorOptions.MinDelay"/>; an extra look just after a known reset time so the announcement is timely. After a failure that
/// may pass (no network, service down, unreadable reply) the wait doubles each time up to <see cref="UsageMonitorOptions.MaxBackoff"/>, and a
/// good reading starts the normal pace again. A refusal (no token, or the service said no) pauses polling altogether; a new token and
/// <see cref="RefreshAsync"/> resume it.
///
/// Reset detection compares each good reading with the previous one, per limit: a limit that was in use has reset when its reset time moved
/// later by more than the tolerance, or its used share went down, or its reset time had passed and the service now reports nothing used. Each
/// reset is announced once. The previous reading lives in memory only, so a reset that happened while Lunar was not running is not announced.
/// Nothing a failure carries beyond the typed, token-free message reaches the state. Observable semantics are those of
/// <see cref="StateBroadcaster{T}"/> (ADR-0006). Thread-safe.
/// </summary>
public sealed class UsageMonitor : IDisposable
{
    private const string UnexpectedMessage = "Lunar could not read the answer from Claude.";

    private readonly IUsageSource _source;
    private readonly TimeProvider _time;
    private readonly UsageMonitorOptions _options;
    private readonly StateBroadcaster<UsageState> _changes = new();
    private readonly StateBroadcaster<UsageLimitReset> _resets = new();
    private readonly SemaphoreSlim _fetchGate = new(1, 1);
    private readonly object _gate = new();

    private UsageState _current = UsageState.Off;
    private CancellationTokenSource? _run;
    private Task? _loop;
    private CancellationTokenSource? _wake;
    private bool _wakePending;
    private bool _islandOpen;
    private bool _paused;
    private int _failures;
    private int _started;
    private UsageSnapshot? _baseline;
    private DateTimeOffset? _lastAttempt;
    private bool _disposed;

    public UsageMonitor(IUsageSource source, UsageMonitorOptions? options = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _options = options ?? new UsageMonitorOptions();
        _time = time ?? TimeProvider.System;
    }

    public UsageState Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _run is not null;
        }
    }

    /// <summary>Every change of the state; no replay (read <see cref="Current"/> first).</summary>
    public IObservable<UsageState> Changes => _changes;

    /// <summary>A limit that was in use has reset; one value per reset.</summary>
    public IObservable<UsageLimitReset> Resets => _resets;

    /// <summary>Starts looking. The first request goes out at once. Does nothing when already running.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _run is not null)
                return;

            var run = new CancellationTokenSource();
            _run = run;
            _paused = false;
            _failures = 0;
            _lastAttempt = null;
            _baseline = null;
            Publish(new UsageState(UsageStatus.Loading));
            _loop = Task.Run(() => LoopAsync(run.Token));
        }
    }

    /// <summary>Stops looking and forgets the last reading and the previous one. An answer that arrives later is dropped.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_run is null)
                return;
            StopCore();
            Publish(UsageState.Off);
        }
    }

    /// <summary>Tells the monitor whether the island is open, which sets the pace. Opening it looks at once when the last look is old enough.</summary>
    public void SetIslandOpen(bool isOpen)
    {
        lock (_gate)
        {
            if (_islandOpen == isOpen)
                return;
            _islandOpen = isOpen;
            Wake();
        }
    }

    /// <summary>
    /// Looks now, for the user's refresh button or a newly pasted token. While the last reading is good and younger than
    /// <see cref="UsageMonitorOptions.RefreshGap"/> the answer is that reading and no request is made. If a request is already running this waits
    /// for it instead of sending another. Returns the state afterwards; does nothing when the monitor is not running.
    /// </summary>
    public async Task<UsageState> RefreshAsync(CancellationToken ct)
    {
        CancellationToken run;
        lock (_gate)
        {
            if (_disposed || _run is null)
                return _current;
            // A reading younger than the gap is the answer, unless a request is running right now: then wait for that one.
            if (_current.Status == UsageStatus.Ready && _fetchGate.CurrentCount > 0 && _lastAttempt is { } last && _time.GetUtcNow() - last < _options.RefreshGap)
                return _current;
            run = _run.Token;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(run, ct);
        await FetchAsync(run, linked.Token).ConfigureAwait(false);
        lock (_gate)
        {
            Wake();
            return _current;
        }
    }

    public void Dispose()
    {
        Task? loop;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            loop = _loop;
            StopCore();
        }

        _changes.Complete();
        _resets.Complete();
        try
        {
            loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The loop catches its own failures; a cancelled wait is the normal way out.
        }
    }

    // Callers hold the gate.
    private void StopCore()
    {
        var run = _run;
        _run = null;
        _paused = false;
        _failures = 0;
        _baseline = null;
        _lastAttempt = null;
        try
        {
            run?.Cancel();
        }
        finally
        {
            run?.Dispose();
        }

        _wake?.Cancel();
    }

    // Callers hold the gate.
    private void Publish(UsageState state)
    {
        _current = state;
        _changes.Publish(state);
    }

    // Callers hold the gate.
    private void Wake()
    {
        _wakePending = true;
        _wake?.Cancel();
    }

    private async Task LoopAsync(CancellationToken run)
    {
        try
        {
            while (!run.IsCancellationRequested)
            {
                var wait = NextWait();
                if (wait is null || wait.Value > TimeSpan.Zero)
                {
                    await WaitAsync(wait ?? Timeout.InfiniteTimeSpan, run).ConfigureAwait(false);
                    continue;
                }

                await FetchAsync(run, run).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stop or dispose: the normal way out.
        }
    }

    // How long until the next look; null means wait to be woken (paused); zero or less means look now.
    private TimeSpan? NextWait()
    {
        lock (_gate)
        {
            if (_paused)
                return null;
            if (_lastAttempt is not { } last)
                return TimeSpan.Zero;

            var now = _time.GetUtcNow();
            var interval = _islandOpen ? _options.OpenInterval : _options.ClosedInterval;
            for (var i = 0; i < _failures && interval < _options.MaxBackoff; i++)
                interval += interval;
            interval = interval > _options.MaxBackoff ? _options.MaxBackoff : interval;

            var due = last + interval;
            if (_failures == 0 && _baseline is not null)
            {
                foreach (var window in new[] { _baseline.FiveHour, _baseline.Weekly })
                {
                    // Only a reset time that is still ahead of the last look: one that has passed was looked at already.
                    if (window?.ResetsAt is { } resetsAt && resetsAt > last && resetsAt + _options.ResetGrace < due)
                        due = resetsAt + _options.ResetGrace;
                }
            }

            var earliest = last + _options.MinDelay;
            return (due > earliest ? due : earliest) - now;
        }
    }

    // Waits for the time, or until something wakes the loop (island opened or closed, refresh, stop).
    private async Task WaitAsync(TimeSpan time, CancellationToken run)
    {
        using var wake = CancellationTokenSource.CreateLinkedTokenSource(run);
        lock (_gate)
        {
            if (_wakePending)
            {
                _wakePending = false;
                return;
            }

            _wake = wake;
        }

        try
        {
            await _options.Delay(time, wake.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!run.IsCancellationRequested)
        {
            // Woken: look at the schedule again.
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_wake, wake))
                    _wake = null;
                _wakePending = false;
            }
        }
    }

    // One request at a time. 'run' is the monitor's own token (an answer after Stop is dropped); 'ct' also covers a caller's cancel.
    private async Task FetchAsync(CancellationToken run, CancellationToken ct)
    {
        int asked;
        bool busy;
        lock (_gate)
        {
            asked = _started;
            busy = _fetchGate.CurrentCount == 0;
        }

        try
        {
            await _fetchGate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // the caller gave up waiting for its turn
        }

        try
        {
            lock (_gate)
            {
                // A request that was running when this one asked, or started while it waited, already answers it: one request in
                // flight, never two in a row.
                if (run.IsCancellationRequested || busy || _started != asked)
                    return;
                _started++;
                _lastAttempt = _time.GetUtcNow();
            }

            Result<UsageSnapshot> result;
            try
            {
                result = await _source.FetchAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // A source that throws instead of returning a typed failure: shown as unreadable, with no exception text.
                result = Result.Failure<UsageSnapshot>(new OperationError(ErrorCode.Unexpected, UnexpectedMessage, retryable: true));
            }

            Apply(result, run);
        }
        finally
        {
            _fetchGate.Release();
        }
    }

    private void Apply(Result<UsageSnapshot> result, CancellationToken run)
    {
        lock (_gate)
        {
            if (run.IsCancellationRequested || result.IsCancelled)
                return;

            var now = _time.GetUtcNow();
            if (result.IsSuccess)
            {
                var snapshot = result.Value;
                var found = Detect(_baseline, snapshot, now);
                _baseline = snapshot;
                _failures = 0;
                _paused = false;
                Publish(new UsageState(UsageStatus.Ready, snapshot));
                foreach (var reset in found)
                    _resets.Publish(reset);
                return;
            }

            var error = result.Error!;
            if (error.Code == ErrorCode.AccessDenied)
            {
                _failures = 0;
                _paused = true;
                _baseline = null;
                Publish(new UsageState(UsageStatus.SignedOut, null, error.SafeMessage));
                return;
            }

            _failures++;
            var status = error.Code == ErrorCode.Unexpected ? UsageStatus.Unreadable : UsageStatus.Unavailable;
            Publish(new UsageState(status, _current.Snapshot, error.SafeMessage));
        }
    }

    // The limits of 'next' that have reset since 'previous'. Only a limit that was in use, with a known reset time, can have reset.
    private IReadOnlyList<UsageLimitReset> Detect(UsageSnapshot? previous, UsageSnapshot next, DateTimeOffset now)
    {
        if (previous is null)
            return [];

        var found = new List<UsageLimitReset>(2);
        Check(previous.FiveHour, next.FiveHour, UsageWindowKind.FiveHour);
        Check(previous.Weekly, next.Weekly, UsageWindowKind.Weekly);
        return found;

        void Check(UsageWindow? before, UsageWindow? after, UsageWindowKind kind)
        {
            if (before is not { UsedFraction: > 0, ResetsAt: { } expected })
                return;

            var passed = now >= expected;
            var movedOn = after?.ResetsAt is { } newTime && newTime - expected > _options.ResetTolerance;
            var usedLess = after?.UsedFraction is { } used && used < before.UsedFraction.Value;
            var emptied = passed && after?.UsedFraction is null && after?.ResetsAt is null;
            if (!movedOn && !usedLess && !emptied)
                return;

            found.Add(new UsageLimitReset(kind, passed ? expected : now, now, before.UsedPercent));
        }
    }
}
