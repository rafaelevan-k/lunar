using Lunar.Application.Abstractions.Media;
using Lunar.Application.Media.Models;
using Lunar.Application.Media.UseCases;
using Lunar.Application.Tests.Fakes;
using Lunar.Desktop.Features.MediaControls;
using Lunar.Desktop.Shell;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Desktop.UITests.Features;

// FE-18: the seek slider. The position the player reports and the position the user drags to stay apart; the seek is sent once,
// on release, and a failure puts the real position back. Over a real tracker and SeekMedia and a scripted media service; time
// and the position timer are driven by hand.
public sealed class MediaSeekViewModelTests
{
    private sealed class SeekableMedia : IMediaService
    {
        private readonly TestObservable<IReadOnlyList<MediaSessionSnapshot>> _changes = new();
        private readonly object _gate = new();
        private List<MediaSessionSnapshot> _sessions = [];

        public Result SeekResult { get; set; } = Result.Success();

        /// <summary>When set, a seek waits for this before it answers.</summary>
        public TaskCompletionSource? Hold { get; set; }

        /// <summary>Where the player lands after an accepted seek; null lands exactly on the requested position.</summary>
        public TimeSpan? LandAt { get; set; }

        public List<TimeSpan> Seeks { get; } = [];

        public IObservable<IReadOnlyList<MediaSessionSnapshot>> Changes => _changes;

        public void Set(params MediaSessionSnapshot[] sessions)
        {
            lock (_gate)
                _sessions = [.. sessions];
            _changes.Publish(sessions.ToArray());
        }

        public Task<Result<IReadOnlyList<MediaSessionSnapshot>>> GetSessionsAsync(CancellationToken ct)
        {
            lock (_gate)
                return Task.FromResult(Result.Success<IReadOnlyList<MediaSessionSnapshot>>(_sessions.ToArray()));
        }

        public Task<Result> PlayPauseAsync(string sessionId, CancellationToken ct) => Task.FromResult(Result.Success());

        public Task<Result> NextAsync(string sessionId, CancellationToken ct) => Task.FromResult(Result.Success());

        public Task<Result> PreviousAsync(string sessionId, CancellationToken ct) => Task.FromResult(Result.Success());

        public async Task<Result> SeekAsync(string sessionId, TimeSpan position, CancellationToken ct)
        {
            lock (Seeks)
                Seeks.Add(position);
            if (Hold is { } hold)
                await hold.Task.ConfigureAwait(false);
            if (SeekResult.IsFailure)
                return SeekResult;

            // The player moves; it does not announce it (the use case reads it back).
            lock (_gate)
            {
                _sessions = [.. _sessions.Select(s => s.Id == sessionId ? WithPosition(s, LandAt ?? position) : s)];
            }

            return SeekResult;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTimer : IOneShotTimer
    {
        private Action? _callback;

        public void Schedule(TimeSpan delay, Action callback) => _callback = callback;

        public void Cancel() => _callback = null;

        public void Fire()
        {
            var callback = _callback;
            _callback = null;
            callback?.Invoke();
        }

        public void Dispose() => Cancel();
    }

    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Rig : IDisposable
    {
        public SeekableMedia Media { get; } = new();

        public FakeTimer Timer { get; } = new();

        public FakeTime Time { get; } = new();

        public MediaSessionTracker Tracker { get; }

        public MediaPageViewModel Model { get; }

        /// <summary>Work posted to the UI, run only when a test says so; null when it runs at once.</summary>
        public Queue<Action>? Queue { get; }

        public Rig(bool queued = false)
        {
            if (queued)
                Queue = new Queue<Action>();
            Tracker = new MediaSessionTracker(Media);
            var seek = new SeekMedia(Media, new SeekReconcileOptions(1, TimeSpan.Zero, TimeSpan.FromSeconds(2)));
            Model = new MediaPageViewModel(Tracker, new MediaTransport(Media), seek, work => { if (Queue is null) work(); else Queue.Enqueue(work); }, null, Time, Timer);
        }

        public Rig Started(params MediaSessionSnapshot[] sessions)
        {
            Media.Set(sessions);
            Model.InitializeAsync(Ct).GetAwaiter().GetResult();
            Drain();
            return this;
        }

        public void Drain()
        {
            while (Queue is { Count: > 0 })
                Queue.Dequeue()();
        }

        public void Dispose()
        {
            Model.Dispose();
            Tracker.Dispose();
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MediaSessionSnapshot S(
        string title = "Song",
        MediaPlaybackStatus status = MediaPlaybackStatus.Playing,
        int? positionSeconds = 30,
        int? durationSeconds = 200,
        bool seek = true) =>
        new("a", "Player", title, "Artist", status,
            positionSeconds is { } p ? TimeSpan.FromSeconds(p) : null,
            durationSeconds is { } d ? TimeSpan.FromSeconds(d) : null,
            new MediaCapabilities(true, true, true, seek), true, ReadOnlyMemory<byte>.Empty);

    private static MediaSessionSnapshot WithPosition(MediaSessionSnapshot s, TimeSpan position) =>
        new(s.Id, s.AppName, s.Title, s.Artist, s.Status, position, s.Duration, s.Capabilities, s.IsCurrent, s.Artwork);

    private static async Task Until(Func<bool> condition)
    {
        var end = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < end)
            await Task.Delay(5, Ct);
        Assert.True(condition(), "The expected state was not reached in time.");
    }

    private static int SeekCount(Rig rig)
    {
        lock (rig.Media.Seeks)
            return rig.Media.Seeks.Count;
    }

    // ---- no jumping --------------------------------------------------------------------------------------------------

    [Fact]
    public void WithoutTheHandTheThumbFollowsThePlayer()
    {
        using var rig = new Rig().Started(S(positionSeconds: 50, durationSeconds: 200));

        Assert.True(rig.Model.CanSeek);
        Assert.False(rig.Model.ShowReadOnlyBar);
        Assert.Equal(0.25, rig.Model.SeekValue, 3);
        Assert.Equal("00:50", rig.Model.PositionText);
    }

    [Fact]
    public void WhileTheThumbIsHeldNeitherTheTickerNorThePlayerMovesIt()
    {
        using var rig = new Rig().Started(S(positionSeconds: 30, durationSeconds: 200));
        rig.Model.StartTicker();

        rig.Model.IsScrubbing = true;
        rig.Model.SeekValue = 0.5;
        Assert.Equal("01:40", rig.Model.PositionText);

        // Time passes, the ticker fires, the player reports a new position: the thumb stays under the hand.
        rig.Time.Now += TimeSpan.FromSeconds(3);
        rig.Timer.Fire();
        rig.Media.Set(S(positionSeconds: 33, durationSeconds: 200));
        rig.Time.Now += TimeSpan.FromSeconds(1);
        rig.Timer.Fire();

        Assert.Equal(0.5, rig.Model.SeekValue, 6);
        Assert.Equal("01:40", rig.Model.PositionText);
        Assert.Equal(0, SeekCount(rig)); // nothing is sent during the drag
    }

    [Fact]
    public void TheTimeShowsHoursFromOneHourOn()
    {
        using var rig = new Rig().Started(S(positionSeconds: 10, durationSeconds: 7200));

        rig.Model.IsScrubbing = true;
        rig.Model.SeekValue = 0.5;
        Assert.Equal("01:00:00", rig.Model.PositionText);

        rig.Model.SeekValue = 0.25;
        Assert.Equal("30:00", rig.Model.PositionText);
        Assert.Equal("02:00:00", rig.Model.DurationText);
    }

    [Fact]
    public void ABackgroundReportThatEchoesThePagesOwnValueIsNotTheUserMovingIt()
    {
        using var rig = new Rig().Started(S(positionSeconds: 50, durationSeconds: 200));

        rig.Model.SeekValue = rig.Model.SeekValue; // the slider echoing what the page set
        rig.Model.IsScrubbing = true;
        rig.Model.IsScrubbing = false;

        Assert.Equal(0, SeekCount(rig));
    }

    // ---- the seek ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ReleasingTheThumbSendsOneSeekToTheDraggedPosition()
    {
        using var rig = new Rig().Started(S(positionSeconds: 30, durationSeconds: 200));

        rig.Model.IsScrubbing = true;
        rig.Model.SeekValue = 0.2;
        rig.Model.SeekValue = 0.4;
        rig.Model.SeekValue = 0.5;
        rig.Model.IsScrubbing = false;

        await Until(() => SeekCount(rig) == 1);
        lock (rig.Media.Seeks)
            Assert.Equal(TimeSpan.FromSeconds(100), rig.Media.Seeks[0]);
        await Until(() => rig.Model.PositionText == "01:40");
        Assert.Null(rig.Model.ErrorMessage);
    }

    [Fact]
    public async Task TheReleasedPositionStaysWhileTheSeekIsWaitingForTheAnswer()
    {
        using var rig = new Rig().Started(S(positionSeconds: 30, durationSeconds: 200));
        rig.Media.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        rig.Model.IsScrubbing = true;
        rig.Model.SeekValue = 0.5;
        rig.Model.IsScrubbing = false;
        await Until(() => SeekCount(rig) == 1);

        // The player still reports the old position meanwhile: the page does not snap back.
        rig.Media.Set(S(positionSeconds: 31, durationSeconds: 200));
        Assert.Equal(0.5, rig.Model.SeekValue, 6);
        Assert.Equal("01:40", rig.Model.PositionText);

        rig.Media.Hold.SetResult();
        await Until(() => rig.Model.PositionText == "01:40");
        Assert.Equal(0.5, rig.Model.SeekValue, 3);
    }

    [Fact]
    public async Task AfterTheSeekThePageContinuesFromWhereThePlayerLanded()
    {
        using var rig = new Rig().Started(S(positionSeconds: 30, durationSeconds: 200));
        rig.Media.LandAt = TimeSpan.FromSeconds(90); // the player did not land on the target

        rig.Model.IsScrubbing = true;
        rig.Model.SeekValue = 0.5;
        rig.Model.IsScrubbing = false;

        await Until(() => rig.Model.PositionText == "01:30");
        Assert.Equal(0.45, rig.Model.SeekValue, 3);

        // And it keeps moving from there while it plays.
        rig.Model.StartTicker();
        rig.Time.Now += TimeSpan.FromSeconds(10);
        rig.Timer.Fire();
        Assert.Equal("01:40", rig.Model.PositionText);
    }

    [Fact]
    public async Task AFailedSeekPutsTheRealPositionBackAndSaysWhy()
    {
        using var rig = new Rig().Started(S(positionSeconds: 30, durationSeconds: 200));
        rig.Media.SeekResult = Result.Failure(new OperationError(ErrorCode.Unexpected, "refused"));

        rig.Model.IsScrubbing = true;
        rig.Model.SeekValue = 0.8;
        Assert.Equal("02:40", rig.Model.PositionText);
        rig.Model.IsScrubbing = false;

        await Until(() => rig.Model.ErrorMessage is not null);
        Assert.Equal("The player didn't move to that position.", rig.Model.ErrorMessage);
        Assert.Equal("00:30", rig.Model.PositionText);
        Assert.Equal(0.15, rig.Model.SeekValue, 3);
        Assert.True(rig.Model.CanSeek); // one refusal does not take the slider away
    }

    [Fact]
    public async Task AFailedSeekContinuesFromTheRealPositionNotTheHopedOne()
    {
        using var rig = new Rig().Started(S(positionSeconds: 30, durationSeconds: 200));
        rig.Media.SeekResult = Result.Failure(new OperationError(ErrorCode.Timeout, "slow"));
        rig.Model.StartTicker();

        rig.Model.IsScrubbing = true;
        rig.Model.SeekValue = 0.8;
        rig.Model.IsScrubbing = false;
        await Until(() => rig.Model.ErrorMessage is not null);

        rig.Time.Now += TimeSpan.FromSeconds(5);
        rig.Timer.Fire();
        Assert.Equal("00:35", rig.Model.PositionText);
        Assert.Equal("The player didn't answer.", rig.Model.ErrorMessage);
    }

    [Fact]
    public void EachSeekFailureHasItsOwnWords()
    {
        Assert.Equal("This app doesn't allow seeking.", MediaPageText.SeekFailed(ErrorCode.Unsupported));
        Assert.Equal("The media changed, so that position no longer applies.", MediaPageText.SeekFailed(ErrorCode.InvalidInput));
        Assert.Equal("The player didn't answer.", MediaPageText.SeekFailed(ErrorCode.Timeout));
        Assert.Equal("The player didn't move to that position.", MediaPageText.SeekFailed(ErrorCode.Unexpected));
    }

    [Fact]
    public async Task TheNextSeekClearsTheErrorOfTheLastOne()
    {
        using var rig = new Rig().Started(S(positionSeconds: 30, durationSeconds: 200));
        rig.Media.SeekResult = Result.Failure(new OperationError(ErrorCode.Unexpected, "refused"));
        rig.Model.IsScrubbing = true;
        rig.Model.SeekValue = 0.8;
        rig.Model.IsScrubbing = false;
        await Until(() => rig.Model.ErrorMessage is not null);

        rig.Media.SeekResult = Result.Success();
        rig.Model.IsScrubbing = true;
        rig.Model.SeekValue = 0.5;
        rig.Model.IsScrubbing = false;

        await Until(() => rig.Model.PositionText == "01:40");
        Assert.Null(rig.Model.ErrorMessage);
    }

    // ---- unsupported and changed ------------------------------------------------------------------------------------

    [Fact]
    public void ASessionThatCannotBeSeekedOffersNoSliderAndSendsNothing()
    {
        using var rig = new Rig().Started(S(seek: false));

        Assert.False(rig.Model.CanSeek);
        Assert.True(rig.Model.ShowReadOnlyBar);
        Assert.Equal("This app doesn't allow seeking", rig.Model.TimelineNote);

        var before = rig.Model.SeekValue;
        rig.Model.IsScrubbing = true;
        rig.Model.SeekValue = 0.9; // a write that should not be possible: ignored
        rig.Model.IsScrubbing = false;

        Assert.Equal(before, rig.Model.SeekValue, 6);
        Assert.Equal(0, SeekCount(rig));
    }

    [Fact]
    public void ALiveStreamHasNeitherSliderNorBar()
    {
        using var rig = new Rig().Started(S(positionSeconds: null, durationSeconds: null));

        Assert.False(rig.Model.CanSeek);
        Assert.False(rig.Model.ShowReadOnlyBar);
        Assert.False(rig.Model.HasTimeline);
    }

    [Fact]
    public void AMediaChangeDuringTheDragDropsTheDragAndSendsNothing()
    {
        using var rig = new Rig().Started(S(positionSeconds: 30, durationSeconds: 200));

        rig.Model.IsScrubbing = true;
        rig.Model.SeekValue = 0.5;
        rig.Media.Set(S(title: "Other song", positionSeconds: 0, durationSeconds: 300));

        // The new media is shown; the held thumb is ignored until it is let go.
        Assert.Equal("00:00", rig.Model.PositionText);
        rig.Model.SeekValue = 0.9;
        Assert.Equal("00:00", rig.Model.PositionText);
        rig.Model.IsScrubbing = false;

        Assert.Equal(0, SeekCount(rig));
        Assert.Equal(0, rig.Model.SeekValue, 6);
    }

    [Fact]
    public void ASessionThatClosesDuringTheDragDropsIt()
    {
        using var rig = new Rig().Started(S());

        rig.Model.IsScrubbing = true;
        rig.Model.SeekValue = 0.5;
        rig.Media.Set();
        rig.Model.IsScrubbing = false;

        Assert.Equal(0, SeekCount(rig));
        Assert.True(rig.Model.IsNoSession);
    }

    [Fact]
    public async Task AnAnswerAboutAnOlderSeekDoesNotMoveTheThumbOfANewerDrag()
    {
        using var rig = new Rig().Started(S(positionSeconds: 30, durationSeconds: 200));
        rig.Media.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        rig.Model.IsScrubbing = true;
        rig.Model.SeekValue = 0.5;
        rig.Model.IsScrubbing = false;
        await Until(() => SeekCount(rig) == 1);

        // The user grabs the thumb again before the first answer comes.
        rig.Model.IsScrubbing = true;
        rig.Model.SeekValue = 0.75;
        rig.Media.Hold.SetResult();
        await Task.Delay(100, Ct);

        Assert.Equal(0.75, rig.Model.SeekValue, 6);
        Assert.Equal("02:30", rig.Model.PositionText);
    }

    // ---- input that is not a drag -----------------------------------------------------------------------------------

    [Fact]
    public async Task AValueSetWithoutAnyDragIsOneSeekOnceTheInputIsDone()
    {
        // A screen reader sets the value directly: there is no thumb to release.
        using var rig = new Rig(queued: true).Started(S(positionSeconds: 30, durationSeconds: 200));

        rig.Model.SeekValue = 0.5;
        Assert.Equal(0, SeekCount(rig)); // not yet: the input may still turn into a drag

        rig.Drain();
        await Until(() => SeekCount(rig) == 1);
    }

    [Fact]
    public async Task AClickOnTheTrackThatBecomesADragIsOneSeekOnRelease()
    {
        // Click-to-point sets the value first and starts the drag right after; only the release seeks.
        using var rig = new Rig(queued: true).Started(S(positionSeconds: 30, durationSeconds: 200));

        rig.Model.SeekValue = 0.5;
        rig.Model.IsScrubbing = true;
        rig.Model.SeekValue = 0.6;
        rig.Drain(); // the deferred check sees a held thumb and does nothing
        Assert.Equal(0, SeekCount(rig));

        rig.Model.IsScrubbing = false;
        rig.Drain();
        await Until(() => SeekCount(rig) == 1);
        lock (rig.Media.Seeks)
            Assert.Equal(TimeSpan.FromSeconds(120), rig.Media.Seeks[0]);
    }

    [Fact]
    public void KeyboardStepsAreSecondsNotPercent()
    {
        using var rig = new Rig().Started(S(durationSeconds: 200));

        Assert.Equal(5.0 / 200, rig.Model.SeekSmallChange, 6);
        Assert.Equal(30.0 / 200, rig.Model.SeekLargeChange, 6);
    }

    [Fact]
    public void ASeekAfterDisposalSendsNothing()
    {
        var rig = new Rig().Started(S());
        rig.Model.IsScrubbing = true;
        rig.Model.SeekValue = 0.5;
        rig.Dispose();

        rig.Model.IsScrubbing = false;

        Assert.Equal(0, SeekCount(rig));
    }
}
