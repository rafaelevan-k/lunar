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
/// What it does not do yet: the four commands (<see cref="PlayPauseAsync"/>, <see cref="NextAsync"/>,
/// <see cref="PreviousAsync"/>, <see cref="SeekAsync"/>) belong to BE-16 and BE-17 and fail with
/// <see cref="ErrorCode.Unsupported"/> until then, so nothing can claim a command worked that was never sent. The service is
/// not registered in the composition root for the same reason: a half-finished port should not be handed to the UI.
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

    private List<MediaSessionReading>? _lastReadings;

    public GsmtcMediaService(IDiagnosticLog? log = null)
        : this(new WinRtGsmtcBackend(), log, TimeProvider.System, DefaultReadTimeout)
    {
    }

    internal GsmtcMediaService(IGsmtcBackend backend, IDiagnosticLog? log, TimeProvider time, TimeSpan readTimeout)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        if (readTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(readTimeout), readTimeout, "The read timeout must be positive.");
        _log = log;
        _readTimeout = readTimeout;

        _core = new ServiceCore<IReadOnlyList<MediaSessionSnapshot>>(
            refresh: async () => _ = await ReadAsync(CancellationToken.None).ConfigureAwait(false),
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

    public Task<Result> PlayPauseAsync(string sessionId, CancellationToken ct) => NotYetAsync(ct);

    public Task<Result> NextAsync(string sessionId, CancellationToken ct) => NotYetAsync(ct);

    public Task<Result> PreviousAsync(string sessionId, CancellationToken ct) => NotYetAsync(ct);

    public Task<Result> SeekAsync(string sessionId, TimeSpan position, CancellationToken ct) => NotYetAsync(ct);

    public ValueTask DisposeAsync() => _core.DisposeAsync();

    /// <summary>Synchronous disposal for containers that dispose singletons with <c>Dispose()</c> at shutdown.</summary>
    public void Dispose() => _core.Dispose();

    private static OperationError Closed => new(ErrorCode.DeviceUnavailable, "The media service is closed.");

    // Commands arrive with BE-16 and BE-17. Until then they say so, instead of pretending.
    private Task<Result> NotYetAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Task.FromResult(Result.Cancelled());
        if (_core.IsDisposed)
            return Task.FromResult(Result.Failure(Closed));
        return Task.FromResult(Result.Failure(new OperationError(ErrorCode.Unsupported, "Media commands are not available yet.")));
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

    // From a Windows thread: only asks for a refresh. Never calls back into Windows.
    private void OnBackendChanged() => _core.RequestRefresh();

    private void LogFailure(string eventId, MediaException ex) =>
        _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, eventId, Component, "Call", ErrorCode.DeviceUnavailable, ExceptionType: ex.GetType()));
}
