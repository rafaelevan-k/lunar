using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Media;
using Lunar.Application.Media.Models;
using Lunar.Domain.Shared;
using Lunar.Windows.Shared;

namespace Lunar.Windows.Media;

/// <summary>
/// Media session discovery through Windows' Global System Media Transport Controls (BE-15, ADR-0002, ADR-0006, ADR-0013).
///
/// What this task delivers (reading only):
/// - <see cref="GetSessionsAsync"/> lists every session with its app, title, artist, playback state, position, duration,
///   capability flags, whether Windows treats it as the current one, and its artwork (at most 2 MiB, empty when absent).
///   No session is an empty list, a success; several sessions are all listed, each with its own id, exactly one marked current.
/// - Position is where the media is now: the position Windows stamped, moved forward by the time since the stamp while it
///   plays. A live stream or an app without a timeline has no duration and no position, and can never be seeked.
/// - <see cref="Changes"/> carries the whole session list whenever something about any session really changed (a session
///   opened or closed, the current session changed, new media, a state change, a seek), one list at a time and never the same
///   list twice in a row. Bursts of OS events become one read. A session that disappears is simply missing from the next
///   list.
/// - A failed read is typed: no media API on this Windows is <see cref="ErrorCode.Unsupported"/>, a refusal is
///   <see cref="ErrorCode.AccessDenied"/>, a read that does not finish in time is <see cref="ErrorCode.Timeout"/>, anything
///   else is <see cref="ErrorCode.Unexpected"/> and retryable. Titles, artists and app names reach no log.
/// - Subscriptions to Windows are removed when a session leaves the list and on disposal; disposal completes the observers
///   and is idempotent.
///
/// Transport commands (BE-16): <see cref="PlayPauseAsync"/>, <see cref="NextAsync"/> and <see cref="PreviousAsync"/>
/// look the session up in a fresh read, check the command against what that session advertises right now
/// (<see cref="MediaCommandPlanner"/>), and only then ask the player. An unknown or closed session is
/// <see cref="ErrorCode.InvalidInput"/>; a command the session does not offer is <see cref="ErrorCode.Unsupported"/> and is
/// never sent; a command the player does not accept is <see cref="ErrorCode.Unexpected"/> (retryable); success means the
/// player accepted it, and the new state arrives through <see cref="Changes"/>. Commands go one at a time with the reads.
///
/// Lifecycle (BE-18): notifications from Windows are throttled (a burst is one refresh per interval); a refresh that fails is
/// tried again a few times so the list recovers by itself, and the backend reconnects on its next read; a session that
/// leaves the list loses its subscriptions in that same read; disposal stops the timers, completes the observers and
/// releases every subscription. Which session is "the" active one is not decided here (the list says which one Windows
/// treats as current); that is the policy of <c>MediaSessionTracker</c> in the Application layer.
///
/// Seeking (BE-17): <see cref="SeekAsync"/> follows the same path (fresh read, check against what the session advertises now,
/// then ask the player), keeps the target inside the media and converts it to the timeline of the player.
/// Thread-safe.
/// </summary>
public sealed class GsmtcMediaService : IMediaService, IDisposable
{
    private const string Component = "Media";
    private static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(10);

    private readonly IGsmtcBackend _backend;
    private readonly IDiagnosticLog? _log;
    private readonly TimeProvider _time;
    private readonly TimeSpan _readTimeout;
    private readonly ServiceCore<IReadOnlyList<MediaSessionSnapshot>> _core;

    private readonly MediaServiceTimings _timings;
    private readonly CancellationTokenSource _stop = new();

    private List<MediaSessionReading>? _lastReadings;
    private int _retries;
    private int _throttling;

    public GsmtcMediaService(IDiagnosticLog? log = null)
        : this(new WinRtGsmtcBackend(), log, TimeProvider.System, DefaultReadTimeout)
    {
    }

    internal GsmtcMediaService(IGsmtcBackend backend, IDiagnosticLog? log, TimeProvider time, TimeSpan readTimeout, MediaServiceTimings? timings = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        if (readTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(readTimeout), readTimeout, "The read timeout must be positive.");
        _log = log;
        _readTimeout = readTimeout;
        _timings = timings ?? MediaServiceTimings.Default;

        _core = new ServiceCore<IReadOnlyList<MediaSessionSnapshot>>(
            refresh: RefreshAsync,
            detach: () => _backend.Changed -= OnBackendChanged,
            release: _backend.Dispose);
        _backend.Changed += OnBackendChanged;
    }

    public IObservable<IReadOnlyList<MediaSessionSnapshot>> Changes => _core.Changes;

    public async Task<Result<IReadOnlyList<MediaSessionSnapshot>>> GetSessionsAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Result.Cancelled<IReadOnlyList<MediaSessionSnapshot>>();
        if (_core.IsDisposed)
            return Result.Failure<IReadOnlyList<MediaSessionSnapshot>>(Closed);

        if (!await _core.EnterAsync(ct).ConfigureAwait(false))
        {
            return ct.IsCancellationRequested
                ? Result.Cancelled<IReadOnlyList<MediaSessionSnapshot>>()
                : Result.Failure<IReadOnlyList<MediaSessionSnapshot>>(Closed);
        }

        try
        {
            if (_core.IsDisposed)
                return Result.Failure<IReadOnlyList<MediaSessionSnapshot>>(Closed);

            return await ReadAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _core.Exit();
        }
    }

    public Task<Result> PlayPauseAsync(string sessionId, CancellationToken ct) => CommandAsync(sessionId, MediaCommand.PlayPause, ct);

    public Task<Result> NextAsync(string sessionId, CancellationToken ct) => CommandAsync(sessionId, MediaCommand.Next, ct);

    public Task<Result> PreviousAsync(string sessionId, CancellationToken ct) => CommandAsync(sessionId, MediaCommand.Previous, ct);

    /// <summary>
    /// Moves the session to <paramref name="position"/>, counted from the start of its media (the same scale as the snapshot
    /// position). The session is looked up in a fresh read; a session without a finite timeline or without seek support is
    /// <see cref="ErrorCode.Unsupported"/> and nothing is sent. The target is kept inside the media (not before its start, not
    /// past its end) and converted to the timeline the player reports (its start plus the position). Success means the player
    /// accepted it; the new position arrives through <see cref="Changes"/>.
    /// </summary>
    public Task<Result> SeekAsync(string sessionId, TimeSpan position, CancellationToken ct) =>
        WithSessionAsync(sessionId, ct, async reading =>
        {
            if (!reading.CanSeek || MediaSnapshotMapper.DurationOf(reading) is not { } duration)
                return Result.Failure(new OperationError(ErrorCode.Unsupported, "This player cannot be seeked right now."));

            var inside = position < TimeSpan.Zero ? TimeSpan.Zero : position > duration ? duration : position;
            return await SendAsync(sessionId, GsmtcAction.Seek, reading.Start + inside, ct).ConfigureAwait(false);
        });

    public ValueTask DisposeAsync()
    {
        StopTimers();
        return _core.DisposeAsync();
    }

    /// <summary>Synchronous disposal for containers that dispose singletons with <c>Dispose()</c> at shutdown.</summary>
    public void Dispose()
    {
        StopTimers();
        _core.Dispose();
    }

    private void StopTimers()
    {
        try
        {
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already stopped.
        }
    }

    private static OperationError Closed => new(ErrorCode.DeviceUnavailable, "The media service is closed.");

    private Task<Result> CommandAsync(string sessionId, MediaCommand command, CancellationToken ct) =>
        WithSessionAsync(sessionId, ct, reading =>
            MediaCommandPlanner.Plan(reading, command) is { } action
                ? SendAsync(sessionId, action, TimeSpan.Zero, ct)
                : Task.FromResult(Result.Failure(new OperationError(ErrorCode.Unsupported, "This player does not offer that command right now."))));

    // One command to one session: the session is looked up in a fresh read, the command is checked against what that session
    // advertises right now by the callback, and only then is it sent. A command succeeds only when the player accepted it.
    private async Task<Result> WithSessionAsync(string sessionId, CancellationToken ct, Func<MediaSessionReading, Task<Result>> act)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return Result.Failure(new OperationError(ErrorCode.InvalidInput, "A media session id is required."));
        if (ct.IsCancellationRequested)
            return Result.Cancelled();
        if (_core.IsDisposed)
            return Result.Failure(Closed);

        if (!await _core.EnterAsync(ct).ConfigureAwait(false))
            return ct.IsCancellationRequested ? Result.Cancelled() : Result.Failure(Closed);

        try
        {
            if (_core.IsDisposed)
                return Result.Failure(Closed);

            var read = await ReadAsync(ct).ConfigureAwait(false);
            if (read.IsCancelled)
                return Result.Cancelled();
            if (read.IsFailure)
                return Result.Failure(read.Error!);

            var reading = _lastReadings?.Find(r => r.Id == sessionId);
            if (reading is null)
                return Result.Failure(new OperationError(ErrorCode.InvalidInput, "That media session is not open any more."));

            return await act(reading).ConfigureAwait(false);
        }
        finally
        {
            _core.Exit();
        }
    }

    private async Task<Result> SendAsync(string sessionId, GsmtcAction action, TimeSpan target, CancellationToken ct)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(_readTimeout);
            var outcome = await _backend.SendAsync(sessionId, action, target, linked.Token).ConfigureAwait(false);
            return outcome switch
            {
                GsmtcCommandResult.Accepted => Result.Success(),
                GsmtcCommandResult.SessionGone => Result.Failure(new OperationError(ErrorCode.InvalidInput, "That media session closed before the command was sent.")),
                _ => Result.Failure(new OperationError(ErrorCode.Unexpected, "The player did not accept the command.", retryable: true))
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Result.Cancelled(); // a command already sent is not undone
        }
        catch (OperationCanceledException)
        {
            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "media.command.timeout", Component, "Command", ErrorCode.Timeout));
            return Result.Failure(new OperationError(ErrorCode.Timeout, "The player did not answer in time.", retryable: true));
        }
        catch (MediaException ex)
        {
            LogFailure("media.command." + ex.Failure, ex);
            return Result.Failure(ex.Failure switch
            {
                MediaFailure.NotSupported => new OperationError(ErrorCode.Unsupported, "Media controls are not available on this PC."),
                MediaFailure.AccessDenied => new OperationError(ErrorCode.AccessDenied, "Windows did not allow the command."),
                _ => new OperationError(ErrorCode.Unexpected, "The command could not be sent.", retryable: true)
            });
        }
    }

    // Reads every session, publishes the list when it really changed, and returns it. Callers hold the gate.
    private async Task<Result<IReadOnlyList<MediaSessionSnapshot>>> ReadAsync(CancellationToken ct)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(_readTimeout);
            var readings = await _backend.ReadAsync(linked.Token).ConfigureAwait(false);

            var now = _time.GetUtcNow();
            IReadOnlyList<MediaSessionSnapshot> snapshots = [.. readings.Select(reading => MediaSnapshotMapper.ToSnapshot(reading, now))];
            PublishIfChanged(readings, snapshots);
            return Result.Success(snapshots);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Result.Cancelled<IReadOnlyList<MediaSessionSnapshot>>();
        }
        catch (OperationCanceledException)
        {
            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "media.read.timeout", Component, "Read", ErrorCode.Timeout));
            return Result.Failure<IReadOnlyList<MediaSessionSnapshot>>(new OperationError(ErrorCode.Timeout, "Windows did not answer in time.", retryable: true));
        }
        catch (MediaException ex)
        {
            LogFailure("media.read." + ex.Failure, ex);
            return Result.Failure<IReadOnlyList<MediaSessionSnapshot>>(ex.Failure switch
            {
                MediaFailure.NotSupported => new OperationError(ErrorCode.Unsupported, "Media controls are not available on this PC."),
                MediaFailure.AccessDenied => new OperationError(ErrorCode.AccessDenied, "Windows did not allow access to the media sessions."),
                _ => new OperationError(ErrorCode.Unexpected, "The media sessions could not be read.", retryable: true)
            });
        }
    }

    // A list goes to the observers only when something differs from the one they last got. Position drift while playing is not
    // a difference: the raw position and its stamp are compared, not the moved-forward one.
    private void PublishIfChanged(IReadOnlyList<MediaSessionReading> readings, IReadOnlyList<MediaSessionSnapshot> snapshots)
    {
        var previous = _lastReadings;
        if (previous is not null && previous.Count == readings.Count && previous.Zip(readings).All(pair => pair.First.SameAs(pair.Second)))
            return;

        _lastReadings = [.. readings];
        _core.Publish(snapshots);
    }

    // From a Windows thread: only asks for a refresh. Never calls back into Windows. A burst of notifications (some players report
    // their timeline many times a second) becomes one refresh per throttle interval: the first notification starts the interval,
    // and the refresh at its end reads everything that happened meanwhile.
    private void OnBackendChanged()
    {
        if (_timings.EventThrottle <= TimeSpan.Zero)
        {
            _core.RequestRefresh();
            return;
        }

        if (Interlocked.Exchange(ref _throttling, 1) == 0)
            _ = ThrottledRefreshAsync();
    }

    private async Task ThrottledRefreshAsync()
    {
        try
        {
            await Task.Delay(_timings.EventThrottle, _time, _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // disposed
        }
        finally
        {
            Volatile.Write(ref _throttling, 0);
        }

        _core.RequestRefresh();
    }

    // A refresh that Windows notified about. When it fails (the media service restarted, a read timed out) the list the observers
    // hold may now be wrong and no further notification is guaranteed, so it is tried again a few times, a moment apart; the backend
    // reconnects on the next read. A read made by a caller is not retried: the caller has the failure.
    private async Task RefreshAsync()
    {
        var result = await ReadAsync(CancellationToken.None).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            Volatile.Write(ref _retries, 0);
            return;
        }

        if (result.IsCancelled || Interlocked.Increment(ref _retries) > _timings.MaxRetries)
            return;

        _ = RetryLaterAsync();
    }

    private async Task RetryLaterAsync()
    {
        try
        {
            await Task.Delay(_timings.RetryDelay, _time, _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // disposed
        }

        _core.RequestRefresh();
    }

    private void LogFailure(string eventId, MediaException ex) =>
        _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, eventId, Component, "Call", ErrorCode.DeviceUnavailable, ExceptionType: ex.GetType()));
}

/// <summary>How the media service paces notifications and recovery. Tests shorten these; the defaults suit the real machine.</summary>
/// <param name="EventThrottle">The shortest time between two refreshes caused by notifications (zero: every notification refreshes at once).</param>
/// <param name="RetryDelay">The wait before a failed refresh is tried again.</param>
/// <param name="MaxRetries">How many times in a row a failed refresh is retried (zero: never).</param>
internal sealed record MediaServiceTimings(TimeSpan EventThrottle, TimeSpan RetryDelay, int MaxRetries)
{
    public static MediaServiceTimings Default { get; } = new(TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(1), 3);
}
