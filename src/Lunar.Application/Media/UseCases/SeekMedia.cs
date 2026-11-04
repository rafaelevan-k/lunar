using Lunar.Application.Abstractions.Media;
using Lunar.Application.Media.Models;
using Lunar.Domain.Shared;

namespace Lunar.Application.Media.UseCases;

/// <summary>How a seek ended: where it was asked to go, where it really went, and what the player reports afterwards.</summary>
/// <param name="Requested">The position the caller asked for.</param>
/// <param name="Target">The position the seek was sent to: <paramref name="Requested"/> kept between the start and the end of the media.</param>
/// <param name="Position">
/// What the player reports after the seek, counted from the start of the media; null when it could not be read (the session
/// closed or the read failed after the seek was accepted).
/// </param>
/// <param name="Settled">True when <paramref name="Position"/> is at the target (within the tolerance, allowing for playback moving on).</param>
public sealed record SeekOutcome(TimeSpan Requested, TimeSpan Target, TimeSpan? Position, bool Settled)
{
    /// <summary>True when the target had to be moved to stay inside the media.</summary>
    public bool WasClamped => Target != Requested;
}

/// <summary>How long the use case watches the player settle on the new position after a seek (BE-17).</summary>
/// <param name="Attempts">How many times the session is read after the seek (at least 1).</param>
/// <param name="Interval">The wait between those reads.</param>
/// <param name="Tolerance">How far from the target still counts as arrived (playback keeps moving; players report late).</param>
public sealed record SeekReconcileOptions(int Attempts, TimeSpan Interval, TimeSpan Tolerance)
{
    public static SeekReconcileOptions Default { get; } = new(4, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(2));
}

/// <summary>
/// Seeks a media session to a position the user picked (BE-17, PRD FR-MED-03). It is the one place that decides whether and
/// where a seek goes:
/// - It only seeks a session with a finite timeline that reports seek support (<see cref="IsSupported"/>); any other session
///   is refused with <see cref="ErrorCode.Unsupported"/> and nothing is sent.
/// - The session is read fresh first. A session that closed is <see cref="ErrorCode.InvalidInput"/>; a session that moved on
///   to other media since the caller looked (title, artist or length differ) is also <see cref="ErrorCode.InvalidInput"/>,
///   because a position picked on the old track means nothing on the new one. The target is kept inside the media of the fresh
///   read, so a length that changed does not send the player out of range. A paused session is seeked like any other.
/// - After the player accepts, the session is read again (a few times, as players report late) and the outcome carries the
///   position the player reports, so the UI shows what is true rather than what it hoped for.
/// Seeks go one at a time, in the order given. A cancelled call does not undo a seek already sent. Thread-safe.
/// </summary>
public sealed class SeekMedia
{
    private readonly IMediaService _service;
    private readonly SeekReconcileOptions _reconcile;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SeekMedia(IMediaService service, SeekReconcileOptions? reconcile = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
        _reconcile = reconcile ?? SeekReconcileOptions.Default;
        if (_reconcile.Attempts < 1)
            throw new ArgumentOutOfRangeException(nameof(reconcile), "At least one reconcile read is required.");
        if (_reconcile.Interval < TimeSpan.Zero || _reconcile.Tolerance < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(reconcile), "Interval and tolerance cannot be negative.");
    }

    /// <summary>True for a session with a finite timeline that reports seek support (the rule the UI uses to enable the slider).</summary>
    public static bool IsSupported(MediaSessionSnapshot session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.CanSeek;
    }

    /// <summary>Keeps <paramref name="position"/> between the start (zero) and the end (<paramref name="duration"/>) of the media.</summary>
    public static TimeSpan Clamp(TimeSpan position, TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;
        return position < TimeSpan.Zero ? TimeSpan.Zero : position > duration ? duration : position;
    }

    /// <summary>
    /// Turns a fraction of the timeline (0 to 1, as a slider reports it) into a position. NaN counts as the start, a value
    /// outside the range is kept inside it.
    /// </summary>
    public static TimeSpan FromFraction(double fraction, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || double.IsNaN(fraction))
            return TimeSpan.Zero;
        var f = Math.Clamp(fraction, 0.0, 1.0);
        return Clamp(TimeSpan.FromTicks((long)Math.Round(duration.Ticks * f)), duration);
    }

    public async Task<Result<SeekOutcome>> ExecuteAsync(MediaSessionSnapshot session, TimeSpan target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (ct.IsCancellationRequested)
            return Result.Cancelled<SeekOutcome>();
        if (!IsSupported(session))
            return Result.Failure<SeekOutcome>(new OperationError(ErrorCode.Unsupported, "This player cannot be seeked."));

        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled<SeekOutcome>();
        }

        try
        {
            if (ct.IsCancellationRequested)
                return Result.Cancelled<SeekOutcome>();

            var fresh = await FindAsync(session.Id, ct).ConfigureAwait(false);
            if (fresh.IsCancelled)
                return Result.Cancelled<SeekOutcome>();
            if (fresh.IsFailure)
                return Result.Failure<SeekOutcome>(fresh.Error!);

            var now = fresh.Value;
            if (now is null)
                return Result.Failure<SeekOutcome>(new OperationError(ErrorCode.InvalidInput, "That media session is not open any more."));
            if (!SameMedia(session, now))
                return Result.Failure<SeekOutcome>(new OperationError(ErrorCode.InvalidInput, "The media changed, so that position no longer applies."));
            if (!IsSupported(now) || now.Duration is not { } duration)
                return Result.Failure<SeekOutcome>(new OperationError(ErrorCode.Unsupported, "This player cannot be seeked right now."));

            var clamped = Clamp(target, duration);
            var sent = await _service.SeekAsync(session.Id, clamped, ct).ConfigureAwait(false);
            if (sent.IsCancelled)
                return Result.Cancelled<SeekOutcome>();
            if (sent.IsFailure)
                return Result.Failure<SeekOutcome>(sent.Error!);

            var (position, settled) = await ReconcileAsync(session.Id, clamped, ct).ConfigureAwait(false);
            return Result.Success(new SeekOutcome(target, clamped, position, settled));
        }
        finally
        {
            _gate.Release();
        }
    }

    // Watches the player after an accepted seek: stops at the first read where it is at the target, otherwise reports the last
    // position it saw. Cancelling, a failed read or a vanished session end the watch; the seek itself already succeeded.
    private async Task<(TimeSpan? Position, bool Settled)> ReconcileAsync(string sessionId, TimeSpan target, CancellationToken ct)
    {
        TimeSpan? last = null;
        for (var attempt = 0; attempt < _reconcile.Attempts; attempt++)
        {
            if (attempt > 0 && _reconcile.Interval > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(_reconcile.Interval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return (last, false);
                }
            }

            var read = await FindAsync(sessionId, ct).ConfigureAwait(false);
            if (read.IsFailure || read.IsCancelled || read.Value is not { } current || current.Position is not { } position)
                return (last, false);

            last = position;
            if (Math.Abs((position - target).TotalMilliseconds) <= _reconcile.Tolerance.TotalMilliseconds)
                return (position, true);
        }

        return (last, false);
    }

    private async Task<Result<MediaSessionSnapshot?>> FindAsync(string sessionId, CancellationToken ct)
    {
        var read = await _service.GetSessionsAsync(ct).ConfigureAwait(false);
        if (read.IsCancelled)
            return Result.Cancelled<MediaSessionSnapshot?>();
        if (read.IsFailure)
            return Result.Failure<MediaSessionSnapshot?>(read.Error!);
        return Result.Success<MediaSessionSnapshot?>(read.Value.FirstOrDefault(s => s.Id == sessionId));
    }

    private static bool SameMedia(MediaSessionSnapshot before, MediaSessionSnapshot now) =>
        before.Title == now.Title && before.Artist == now.Artist && before.Duration == now.Duration;
}
