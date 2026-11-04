using Lunar.Application.Abstractions.Media;
using Lunar.Application.Media.Models;
using Lunar.Application.Media.UseCases;
using Lunar.Application.Tests.Fakes;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Application.Tests.Media;

public sealed class SeekMediaTests
{
    /// <summary>A media service whose sessions and seek answer are scripted; a seek can change what the next read returns.</summary>
    private sealed class ScriptedMedia : IMediaService
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource<Result>> _held = [];
        private int _running;

        public List<MediaSessionSnapshot> Sessions { get; set; } = [];

        public Queue<Result<IReadOnlyList<MediaSessionSnapshot>>> ReadScript { get; } = new();

        public List<(string Id, TimeSpan Position)> Seeks { get; } = [];

        public Result SeekAnswer { get; set; } = Result.Success();

        public Action<TimeSpan>? OnSeek { get; set; }

        public bool Hold { get; set; }

        public int Reads { get; private set; }

        public int ConcurrentMax { get; private set; }

        public int HeldCount
        {
            get
            {
                lock (_gate)
                    return _held.Count;
            }
        }

        public IObservable<IReadOnlyList<MediaSessionSnapshot>> Changes => new TestObservable<IReadOnlyList<MediaSessionSnapshot>>();

        public Task<Result<IReadOnlyList<MediaSessionSnapshot>>> GetSessionsAsync(CancellationToken ct)
        {
            lock (_gate)
            {
                Reads++;
                return Task.FromResult(ReadScript.Count > 0 ? ReadScript.Dequeue() : Result.Success<IReadOnlyList<MediaSessionSnapshot>>([.. Sessions]));
            }
        }

        public Task<Result> PlayPauseAsync(string sessionId, CancellationToken ct) => Task.FromResult(Result.Success());

        public Task<Result> NextAsync(string sessionId, CancellationToken ct) => Task.FromResult(Result.Success());

        public Task<Result> PreviousAsync(string sessionId, CancellationToken ct) => Task.FromResult(Result.Success());

        public async Task<Result> SeekAsync(string sessionId, TimeSpan position, CancellationToken ct)
        {
            ConcurrentMax = Math.Max(ConcurrentMax, Interlocked.Increment(ref _running));
            try
            {
                lock (_gate)
                    Seeks.Add((sessionId, position));
                if (Hold)
                {
                    var gate = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (_gate)
                        _held.Add(gate);
                    return await gate.Task;
                }

                OnSeek?.Invoke(position);
                return SeekAnswer;
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }

        public void ReleaseOne()
        {
            TaskCompletionSource<Result> gate;
            lock (_gate)
            {
                gate = _held[0];
                _held.RemoveAt(0);
            }

            gate.SetResult(Result.Success());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static readonly SeekReconcileOptions NoWait = new(3, TimeSpan.Zero, TimeSpan.FromSeconds(2));

    private static MediaSessionSnapshot Session(
        string id = "a",
        int? positionSeconds = 30,
        int? durationSeconds = 200,
        bool canSeek = true,
        MediaPlaybackStatus status = MediaPlaybackStatus.Playing,
        string title = "Song") =>
        new(id, "App", title, "Artist", status,
            positionSeconds is { } p ? TimeSpan.FromSeconds(p) : null,
            durationSeconds is { } d ? TimeSpan.FromSeconds(d) : null,
            new MediaCapabilities(CanPlayPause: true, CanNext: true, CanPrevious: true, CanSeek: canSeek),
            isCurrent: true, ReadOnlyMemory<byte>.Empty);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SeekMedia Create(ScriptedMedia media) => new(media, NoWait);

    // ---- positions ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(-10, 100, 0)]
    [InlineData(0, 100, 0)]
    [InlineData(40, 100, 40)]
    [InlineData(100, 100, 100)]
    [InlineData(250, 100, 100)]
    [InlineData(5, 0, 0)]
    public void ATargetIsKeptBetweenTheStartAndTheEnd(int target, int duration, int expected) =>
        Assert.Equal(TimeSpan.FromSeconds(expected), SeekMedia.Clamp(TimeSpan.FromSeconds(target), TimeSpan.FromSeconds(duration)));

    [Fact]
    public void ANegativeDurationCountsAsNone() =>
        Assert.Equal(TimeSpan.Zero, SeekMedia.Clamp(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(-3)));

    [Theory]
    [InlineData(0.0, 200, 0)]
    [InlineData(0.5, 200, 100)]
    [InlineData(1.0, 200, 200)]
    [InlineData(-0.2, 200, 0)]
    [InlineData(1.7, 200, 200)]
    [InlineData(double.NaN, 200, 0)]
    [InlineData(double.PositiveInfinity, 200, 200)]
    [InlineData(double.NegativeInfinity, 200, 0)]
    [InlineData(0.5, 0, 0)]
    [InlineData(0.5, -10, 0)]
    public void AFractionOfTheTimelineBecomesAPosition(double fraction, int duration, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), SeekMedia.FromFraction(fraction, TimeSpan.FromSeconds(duration)));

    [Fact]
    public void AFractionNeverLeavesTheMediaEvenForHugeDurations()
    {
        var duration = TimeSpan.FromDays(6);

        var position = SeekMedia.FromFraction(1.0, duration);

        Assert.Equal(duration, position);
    }

    // ---- what is supported -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, 200, true)]
    [InlineData(false, 200, false)]
    [InlineData(true, 0, false)]
    [InlineData(true, null, false)]
    public void OnlyAFiniteSeekableTimelineIsSupported(bool canSeek, int? duration, bool expected) =>
        Assert.Equal(expected, SeekMedia.IsSupported(Session(canSeek: canSeek, durationSeconds: duration)));

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task ZeroOrUnknownDurationIsRefusedAndNothingIsRead(int? duration)
    {
        var media = new ScriptedMedia { Sessions = [Session(durationSeconds: duration)] };

        var result = await Create(media).ExecuteAsync(Session(durationSeconds: duration), TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(ErrorCode.Unsupported, result.Error!.Code);
        Assert.Empty(media.Seeks);
        Assert.Equal(0, media.Reads);
    }

    [Fact]
    public async Task ASessionWithoutSeekSupportIsRefusedAndNothingIsSent()
    {
        var media = new ScriptedMedia { Sessions = [Session(canSeek: false)] };

        var result = await Create(media).ExecuteAsync(Session(canSeek: false), TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(ErrorCode.Unsupported, result.Error!.Code);
        Assert.Empty(media.Seeks);
    }

    [Fact]
    public async Task ASessionThatStoppedBeingSeekableSinceTheUiLookedIsRefused()
    {
        var media = new ScriptedMedia { Sessions = [Session(canSeek: false)] };

        var result = await Create(media).ExecuteAsync(Session(canSeek: true), TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(ErrorCode.Unsupported, result.Error!.Code);
        Assert.Empty(media.Seeks);
    }

    // ---- sending and clamping ----------------------------------------------------------------------------------------

    [Fact]
    public async Task ASeekIsSentToTheSessionAtTheTarget()
    {
        var media = new ScriptedMedia { Sessions = [Session("a"), Session("b")] };
        media.OnSeek = t => media.Sessions = [Session("a"), Session("b", positionSeconds: (int)t.TotalSeconds)];

        var result = await Create(media).ExecuteAsync(Session("b"), TimeSpan.FromSeconds(120), Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal([("b", TimeSpan.FromSeconds(120))], media.Seeks);
        Assert.Equal(TimeSpan.FromSeconds(120), result.Value.Target);
        Assert.False(result.Value.WasClamped);
    }

    [Theory]
    [InlineData(-30, 0)]
    [InlineData(999, 200)]
    public async Task ATargetOutsideTheMediaIsMovedInsideAndSaidSo(int requested, int expected)
    {
        var media = new ScriptedMedia { Sessions = [Session()] };

        var result = await Create(media).ExecuteAsync(Session(), TimeSpan.FromSeconds(requested), Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal([("a", TimeSpan.FromSeconds(expected))], media.Seeks);
        Assert.Equal(TimeSpan.FromSeconds(requested), result.Value.Requested);
        Assert.Equal(TimeSpan.FromSeconds(expected), result.Value.Target);
        Assert.True(result.Value.WasClamped);
    }

    [Fact]
    public async Task APausedSessionIsSeekedAndStaysPaused()
    {
        var paused = Session(status: MediaPlaybackStatus.Paused);
        var media = new ScriptedMedia { Sessions = [paused] };
        media.OnSeek = t => media.Sessions = [Session(positionSeconds: (int)t.TotalSeconds, status: MediaPlaybackStatus.Paused)];

        var result = await Create(media).ExecuteAsync(paused, TimeSpan.FromSeconds(50), Ct);

        Assert.True(result.IsSuccess);
        Assert.Single(media.Seeks);
        Assert.True(result.Value.Settled);
        Assert.Equal(TimeSpan.FromSeconds(50), result.Value.Position);
    }

    [Fact]
    public async Task ALengthThatChangedSinceTheUiLookedIsTreatedAsChangedMedia()
    {
        // The UI thought the media was 200 s; the player now says 100 s (same title and artist, length corrected).
        // The media is treated as changed, so nothing is sent at all.
        var media = new ScriptedMedia { Sessions = [Session(durationSeconds: 100)] };

        var result = await Create(media).ExecuteAsync(Session(durationSeconds: 200), TimeSpan.FromSeconds(150), Ct);

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.Empty(media.Seeks);
    }

    // ---- the session changes during the seek -------------------------------------------------------------------------

    [Fact]
    public async Task ASessionThatClosedIsInvalidInputAndNothingIsSent()
    {
        var media = new ScriptedMedia { Sessions = [Session("other")] };

        var result = await Create(media).ExecuteAsync(Session("a"), TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.Empty(media.Seeks);
    }

    [Fact]
    public async Task ASessionThatMovedToOtherMediaIsRefusedBecauseThePositionNoLongerApplies()
    {
        var media = new ScriptedMedia { Sessions = [Session(title: "Next song")] };

        var result = await Create(media).ExecuteAsync(Session(title: "Song"), TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.Empty(media.Seeks);
    }

    [Fact]
    public async Task ASessionThatClosesAfterTheSeekWasAcceptedStillReportsSuccessWithoutAPosition()
    {
        var media = new ScriptedMedia { Sessions = [Session()] };
        media.OnSeek = _ => media.Sessions = [];

        var result = await Create(media).ExecuteAsync(Session(), TimeSpan.FromSeconds(60), Ct);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.Position);
        Assert.False(result.Value.Settled);
    }

    [Fact]
    public async Task ASessionRefusingTheSeekBecauseItClosedIsReturnedAsIs()
    {
        var media = new ScriptedMedia { Sessions = [Session()], SeekAnswer = Result.Failure(new OperationError(ErrorCode.InvalidInput, "closed")) };

        var result = await Create(media).ExecuteAsync(Session(), TimeSpan.FromSeconds(60), Ct);

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
    }

    [Fact]
    public async Task AFailedReadBeforeTheSeekStopsItBeforeAnythingIsSent()
    {
        var media = new ScriptedMedia { Sessions = [Session()] };
        media.ReadScript.Enqueue(Result.Failure<IReadOnlyList<MediaSessionSnapshot>>(new OperationError(ErrorCode.Timeout, "slow", retryable: true)));

        var result = await Create(media).ExecuteAsync(Session(), TimeSpan.FromSeconds(60), Ct);

        Assert.Equal(ErrorCode.Timeout, result.Error!.Code);
        Assert.Empty(media.Seeks);
    }

    // ---- reconciling the timeline ------------------------------------------------------------------------------------

    [Fact]
    public async Task ThePositionThePlayerReportsAfterwardsIsTheOneReturned()
    {
        // Asked for 120 s; the player lands on 119 s (keyframe) and plays on.
        var media = new ScriptedMedia { Sessions = [Session()] };
        media.OnSeek = _ => media.Sessions = [Session(positionSeconds: 119)];

        var result = await Create(media).ExecuteAsync(Session(), TimeSpan.FromSeconds(120), Ct);

        Assert.Equal(TimeSpan.FromSeconds(119), result.Value.Position);
        Assert.True(result.Value.Settled);
    }

    [Fact]
    public async Task ALatePlayerIsWatchedUntilItArrives()
    {
        var media = new ScriptedMedia();
        media.ReadScript.Enqueue(Result.Success<IReadOnlyList<MediaSessionSnapshot>>([Session(positionSeconds: 30)])); // before the seek
        media.ReadScript.Enqueue(Result.Success<IReadOnlyList<MediaSessionSnapshot>>([Session(positionSeconds: 30)])); // still the old position
        media.ReadScript.Enqueue(Result.Success<IReadOnlyList<MediaSessionSnapshot>>([Session(positionSeconds: 120)])); // arrived

        var result = await Create(media).ExecuteAsync(Session(), TimeSpan.FromSeconds(120), Ct);

        Assert.Equal(TimeSpan.FromSeconds(120), result.Value.Position);
        Assert.True(result.Value.Settled);
        Assert.Equal(3, media.Reads);
    }

    [Fact]
    public async Task APlayerThatNeverArrivesIsReportedAsNotSettledWithItsLastPosition()
    {
        var media = new ScriptedMedia { Sessions = [Session(positionSeconds: 30)] };

        var result = await Create(media).ExecuteAsync(Session(), TimeSpan.FromSeconds(120), Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(TimeSpan.FromSeconds(30), result.Value.Position);
        Assert.False(result.Value.Settled);
        Assert.Equal(1 + 3, media.Reads); // the read before the seek and the bounded number after it
    }

    [Fact]
    public async Task APlayerWithoutAPositionAfterwardsIsNotSettled()
    {
        var media = new ScriptedMedia { Sessions = [Session()] };
        media.OnSeek = _ => media.Sessions = [Session(positionSeconds: null)];

        var result = await Create(media).ExecuteAsync(Session(), TimeSpan.FromSeconds(60), Ct);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.Position);
        Assert.False(result.Value.Settled);
    }

    // ---- order and cancellation --------------------------------------------------------------------------------------

    [Fact]
    public async Task SeeksGoOneAtATimeInTheOrderGiven()
    {
        var media = new ScriptedMedia { Sessions = [Session("a"), Session("b")], Hold = true };
        var seek = Create(media);

        var first = seek.ExecuteAsync(Session("a"), TimeSpan.FromSeconds(10), Ct);
        var second = seek.ExecuteAsync(Session("b"), TimeSpan.FromSeconds(20), Ct);
        await WaitFor(() => media.HeldCount == 1);
        await Task.Delay(50, Ct);

        Assert.Equal([("a", TimeSpan.FromSeconds(10))], media.Seeks);
        media.ReleaseOne();
        await first;
        await WaitFor(() => media.HeldCount == 1);
        media.ReleaseOne();
        await second;

        Assert.Equal([("a", TimeSpan.FromSeconds(10)), ("b", TimeSpan.FromSeconds(20))], media.Seeks);
        Assert.Equal(1, media.ConcurrentMax);
    }

    [Fact]
    public async Task AWaitingSeekCanBeCancelledAndIsNeverSent()
    {
        var media = new ScriptedMedia { Sessions = [Session("a"), Session("b")], Hold = true };
        var seek = Create(media);
        var first = seek.ExecuteAsync(Session("a"), TimeSpan.FromSeconds(10), Ct);
        await WaitFor(() => media.HeldCount == 1);
        using var cts = new CancellationTokenSource();
        var waiting = seek.ExecuteAsync(Session("b"), TimeSpan.FromSeconds(20), cts.Token);

        await cts.CancelAsync();
        var result = await waiting;

        Assert.True(result.IsCancelled);
        media.ReleaseOne();
        await first;
        Assert.Equal([("a", TimeSpan.FromSeconds(10))], media.Seeks);
    }

    [Fact]
    public async Task AlreadyCancelledTouchesNothing()
    {
        var media = new ScriptedMedia { Sessions = [Session()] };
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await Create(media).ExecuteAsync(Session(), TimeSpan.FromSeconds(10), cts.Token);

        Assert.True(result.IsCancelled);
        Assert.Equal(0, media.Reads);
        Assert.Empty(media.Seeks);
    }

    [Fact]
    public async Task AFailedSeekDoesNotBlockTheNext()
    {
        var media = new ScriptedMedia { Sessions = [Session()], SeekAnswer = Result.Failure(new OperationError(ErrorCode.Unexpected, "no", retryable: true)) };
        var seek = Create(media);
        Assert.True((await seek.ExecuteAsync(Session(), TimeSpan.FromSeconds(10), Ct)).IsFailure);

        media.SeekAnswer = Result.Success();

        Assert.True((await seek.ExecuteAsync(Session(), TimeSpan.FromSeconds(10), Ct)).IsSuccess);
    }

    [Fact]
    public async Task ManySeeksFromManyThreadsNeverOverlap()
    {
        var media = new ScriptedMedia { Sessions = [Session()] };
        var seek = Create(media);

        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(() => seek.ExecuteAsync(Session(), TimeSpan.FromSeconds(i), Ct), Ct)));

        Assert.Equal(100, media.Seeks.Count);
        Assert.Equal(1, media.ConcurrentMax);
    }

    [Fact]
    public async Task MissingOrInvalidArgumentsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new SeekMedia(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SeekMedia(new ScriptedMedia(), new SeekReconcileOptions(0, TimeSpan.Zero, TimeSpan.Zero)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SeekMedia(new ScriptedMedia(), new SeekReconcileOptions(1, TimeSpan.FromSeconds(-1), TimeSpan.Zero)));
        Assert.Throws<ArgumentNullException>(() => SeekMedia.IsSupported(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => new SeekMedia(new ScriptedMedia()).ExecuteAsync(null!, TimeSpan.Zero, Ct));
    }

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 3000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < end)
            await Task.Delay(5, Ct);
        Assert.True(condition(), "Condition not reached in time.");
    }
}
