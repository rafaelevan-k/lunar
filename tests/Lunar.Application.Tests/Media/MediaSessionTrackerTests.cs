using Lunar.Application.Abstractions.Media;
using Lunar.Application.Media.Models;
using Lunar.Application.Media.UseCases;
using Lunar.Application.Tests.Fakes;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Application.Tests.Media;

public sealed class MediaSessionTrackerTests
{
    private sealed class FakeMedia : IMediaService
    {
        public TestObservable<IReadOnlyList<MediaSessionSnapshot>> Feed { get; } = new();

        public IReadOnlyList<MediaSessionSnapshot> Sessions { get; set; } = [];

        public Result<IReadOnlyList<MediaSessionSnapshot>>? ReadResult { get; set; }

        public Action? OnRead { get; set; }

        public IObservable<IReadOnlyList<MediaSessionSnapshot>> Changes => Feed;

        public Task<Result<IReadOnlyList<MediaSessionSnapshot>>> GetSessionsAsync(CancellationToken ct)
        {
            OnRead?.Invoke();
            return Task.FromResult(ReadResult ?? Result.Success(Sessions));
        }

        public Task<Result> PlayPauseAsync(string sessionId, CancellationToken ct) => Task.FromResult(Result.Success());

        public Task<Result> NextAsync(string sessionId, CancellationToken ct) => Task.FromResult(Result.Success());

        public Task<Result> PreviousAsync(string sessionId, CancellationToken ct) => Task.FromResult(Result.Success());

        public Task<Result> SeekAsync(string sessionId, TimeSpan position, CancellationToken ct) => Task.FromResult(Result.Success());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Recorder : IObserver<ActiveMediaState>
    {
        public List<ActiveMediaState> Values { get; } = [];

        public bool Completed { get; private set; }

        public void OnNext(ActiveMediaState value) => Values.Add(value);

        public void OnError(Exception error) => throw new InvalidOperationException("OnError must never be called.");

        public void OnCompleted() => Completed = true;
    }

    private static MediaSessionSnapshot S(string id, bool current = false, MediaPlaybackStatus status = MediaPlaybackStatus.Paused) =>
        new(id, "App", "Song " + id, "Artist", status, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(100),
            new MediaCapabilities(true, true, true, true), current, ReadOnlyMemory<byte>.Empty);

    private static IReadOnlyList<MediaSessionSnapshot> L(params MediaSessionSnapshot[] sessions) => sessions;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(MediaSessionTracker Tracker, FakeMedia Media, Recorder Seen)> Started(params MediaSessionSnapshot[] sessions)
    {
        var media = new FakeMedia { Sessions = sessions };
        var tracker = new MediaSessionTracker(media);
        var seen = new Recorder();
        tracker.Changes.Subscribe(seen);
        Assert.True((await tracker.InitializeAsync(Ct)).IsSuccess);
        return (tracker, media, seen);
    }

    // ---- the policy --------------------------------------------------------------------------------------------------

    [Fact]
    public void NoSessionMeansNoActiveSession() => Assert.Null(MediaSessionTracker.Choose([], null));

    [Fact]
    public void TheSessionWindowsCallsCurrentIsActive() =>
        Assert.Equal("b", MediaSessionTracker.Choose(L(S("a", status: MediaPlaybackStatus.Playing), S("b", current: true)), null)!.Id);

    [Fact]
    public void WithoutACurrentSessionAPlayingOneWins() =>
        Assert.Equal("c", MediaSessionTracker.Choose(L(S("a"), S("b"), S("c", status: MediaPlaybackStatus.Playing)), null)!.Id);

    [Fact]
    public void WithoutAnythingElseTheLowestIdWins() =>
        Assert.Equal("a", MediaSessionTracker.Choose(L(S("c"), S("a"), S("b")), null)!.Id);

    [Fact]
    public void TheResultNeverDependsOnTheOrderOfTheList()
    {
        var sessions = new[] { S("m", status: MediaPlaybackStatus.Playing), S("c", status: MediaPlaybackStatus.Playing), S("z", current: true), S("a", current: true) };

        var results = new HashSet<string>();
        foreach (var order in Permutations(sessions))
            results.Add(MediaSessionTracker.Choose(order, null)!.Id);

        Assert.Equal(["a"], results); // the lowest id among the current ones
    }

    [Fact]
    public void ThePickBeatsEveryOtherRuleWhileItIsOpen() =>
        Assert.Equal("c", MediaSessionTracker.Choose(L(S("a", current: true), S("b", status: MediaPlaybackStatus.Playing), S("c")), "c")!.Id);

    [Fact]
    public void APickThatIsNotInTheListIsIgnoredByThePolicy() =>
        Assert.Equal("a", MediaSessionTracker.Choose(L(S("a", current: true), S("b")), "gone")!.Id);

    // ---- start-up ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task InitializingReadsTheSessionsAndPublishesTheActiveOne()
    {
        var (tracker, _, seen) = await Started(S("a"), S("b", current: true));

        Assert.Equal("b", tracker.Current!.Active!.Id);
        Assert.False(tracker.Current.IsUserChoice);
        Assert.Equal(2, tracker.Current.Sessions.Count);
        Assert.Single(seen.Values);
    }

    [Fact]
    public async Task NoSessionIsAnEmptyActiveState()
    {
        var (tracker, _, _) = await Started();

        Assert.Null(tracker.Current!.Active);
        Assert.Empty(tracker.Current.Sessions);
    }

    [Fact]
    public async Task AFailedFirstReadIsReturnedButTheTrackerKeepsListening()
    {
        var media = new FakeMedia { ReadResult = Result.Failure<IReadOnlyList<MediaSessionSnapshot>>(new OperationError(ErrorCode.Timeout, "slow", retryable: true)) };
        var tracker = new MediaSessionTracker(media);

        var first = await tracker.InitializeAsync(Ct);
        media.Feed.Publish(L(S("a")));

        Assert.Equal(ErrorCode.Timeout, first.Error!.Code);
        Assert.Equal("a", tracker.Current!.Active!.Id);
    }

    [Fact]
    public async Task AListThatArrivesDuringTheFirstReadIsNewerAndWins()
    {
        var media = new FakeMedia { Sessions = L(S("old")) };
        media.OnRead = () => media.Feed.Publish(L(S("new")));
        var tracker = new MediaSessionTracker(media);

        var result = await tracker.InitializeAsync(Ct);

        Assert.Equal("new", result.Value.Active!.Id);
        Assert.Equal("new", tracker.Current!.Active!.Id);
    }

    [Fact]
    public async Task InitializingTwiceSubscribesOnlyOnce()
    {
        var (tracker, media, _) = await Started(S("a"));

        _ = await tracker.InitializeAsync(Ct);

        Assert.Equal(1, media.Feed.ObserverCount);
    }

    [Fact]
    public async Task InitializingWithACancelledTokenDoesNothing()
    {
        var media = new FakeMedia();
        var tracker = new MediaSessionTracker(media);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Assert.True((await tracker.InitializeAsync(cts.Token)).IsCancelled);
        Assert.Equal(0, media.Feed.ObserverCount);
    }

    // ---- the user's choice -------------------------------------------------------------------------------------------

    [Fact]
    public async Task APickMakesThatSessionActiveAndIsMarkedAsTheUsers()
    {
        var (tracker, _, seen) = await Started(S("a", current: true), S("b"));

        var result = tracker.Select("b");

        Assert.True(result.IsSuccess);
        Assert.Equal("b", tracker.Current!.Active!.Id);
        Assert.True(tracker.Current.IsUserChoice);
        Assert.Equal(2, seen.Values.Count);
    }

    [Fact]
    public async Task APickSurvivesTheOsChangingItsCurrentSession()
    {
        var (tracker, media, _) = await Started(S("a", current: true), S("b"));
        tracker.Select("b");

        media.Feed.Publish(L(S("a"), S("b", status: MediaPlaybackStatus.Playing), S("c", current: true)));

        Assert.Equal("b", tracker.Current!.Active!.Id);
        Assert.True(tracker.Current.IsUserChoice);
    }

    [Fact]
    public async Task APickSurvivesChangesToItsOwnMedia()
    {
        var (tracker, media, _) = await Started(S("a", current: true), S("b"));
        tracker.Select("b");

        media.Feed.Publish(L(S("a", current: true), S("b", status: MediaPlaybackStatus.Playing)));

        Assert.Equal(MediaPlaybackStatus.Playing, tracker.Current!.Active!.Status);
        Assert.True(tracker.Current.IsUserChoice);
    }

    [Fact]
    public async Task WhenThePickedSessionClosesTheOsCurrentSessionTakesOver()
    {
        var (tracker, media, _) = await Started(S("a", current: true), S("b"));
        tracker.Select("b");

        media.Feed.Publish(L(S("a", current: true)));

        Assert.Equal("a", tracker.Current!.Active!.Id);
        Assert.False(tracker.Current.IsUserChoice);
    }

    [Fact]
    public async Task APickThatClosedIsForgottenEvenIfTheSameIdReturns()
    {
        var (tracker, media, _) = await Started(S("a", current: true), S("b"));
        tracker.Select("b");
        media.Feed.Publish(L(S("a", current: true)));

        media.Feed.Publish(L(S("a", current: true), S("b")));

        Assert.Equal("a", tracker.Current!.Active!.Id); // the new "b" is a new session; the old pick is gone
        Assert.False(tracker.Current.IsUserChoice);
    }

    [Fact]
    public async Task WhenEverySessionClosesThereIsNoActiveSessionAndALaterPlayerIsPickedUp()
    {
        var (tracker, media, seen) = await Started(S("a", current: true));

        media.Feed.Publish(L());
        Assert.Null(tracker.Current!.Active);
        media.Feed.Publish(L(S("again", current: true)));

        Assert.Equal("again", tracker.Current!.Active!.Id);
        Assert.Equal(3, seen.Values.Count);
    }

    [Fact]
    public async Task OnlyAnOpenSessionCanBePicked()
    {
        var (tracker, _, seen) = await Started(S("a"));

        Assert.Equal(ErrorCode.InvalidInput, tracker.Select("nope").Error!.Code);
        Assert.Equal(ErrorCode.InvalidInput, tracker.Select(" ").Error!.Code);
        Assert.Single(seen.Values);
    }

    [Fact]
    public void PickingBeforeAnyListIsInvalid()
    {
        var tracker = new MediaSessionTracker(new FakeMedia());

        Assert.Equal(ErrorCode.InvalidInput, tracker.Select("a").Error!.Code);
    }

    [Fact]
    public async Task ClearingThePickReturnsToThePolicy()
    {
        var (tracker, _, seen) = await Started(S("a", current: true), S("b"));
        tracker.Select("b");

        tracker.ClearSelection();
        tracker.ClearSelection(); // nothing to clear: no new state

        Assert.Equal("a", tracker.Current!.Active!.Id);
        Assert.False(tracker.Current.IsUserChoice);
        Assert.Equal(3, seen.Values.Count);
    }

    [Fact]
    public async Task PickingTheSessionThatIsAlreadyActiveOnlyMarksItAsTheUsers()
    {
        var (tracker, _, seen) = await Started(S("a", current: true), S("b"));

        tracker.Select("a");
        tracker.Select("a"); // again: nothing changed

        Assert.True(tracker.Current!.IsUserChoice);
        Assert.Equal(2, seen.Values.Count);
    }

    // ---- lifetime ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DisposalStopsListeningCompletesObserversAndIsIdempotent()
    {
        var (tracker, media, seen) = await Started(S("a"));

        await tracker.DisposeAsync();
        tracker.Dispose();
        media.Feed.Publish(L(S("z")));

        Assert.True(seen.Completed);
        Assert.Equal(0, media.Feed.ObserverCount);
        Assert.Equal("a", tracker.Current!.Active!.Id);
        Assert.Equal(ErrorCode.DeviceUnavailable, tracker.Select("a").Error!.Code);
        Assert.Equal(ErrorCode.DeviceUnavailable, (await tracker.InitializeAsync(Ct)).Error!.Code);
    }

    [Fact]
    public async Task WhenTheServiceCompletesTheTrackerCompletesToo()
    {
        var (_, media, seen) = await Started(S("a"));

        media.Feed.Complete();

        Assert.True(seen.Completed);
    }

    [Fact]
    public async Task ManyChangesFromManyThreadsAreDeliveredOneAtATimeAndTheLastWins()
    {
        var (tracker, media, seen) = await Started(S("a"));

        await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(() => media.Feed.Publish(L(S("s" + i, current: true))), Ct)));
        tracker.Select(tracker.Current!.Active!.Id);

        Assert.Equal(tracker.Current.Active!.Id, tracker.Current.Sessions[0].Id);
        Assert.Equal(1 + 200 + 1, seen.Values.Count);
    }

    [Fact]
    public void ANullServiceIsRejected() => Assert.Throws<ArgumentNullException>(() => new MediaSessionTracker(null!));

    private static IEnumerable<MediaSessionSnapshot[]> Permutations(MediaSessionSnapshot[] items)
    {
        if (items.Length <= 1)
        {
            yield return items;
            yield break;
        }

        for (var i = 0; i < items.Length; i++)
        {
            var rest = items.Where((_, index) => index != i).ToArray();
            foreach (var tail in Permutations(rest))
                yield return [items[i], .. tail];
        }
    }
}
