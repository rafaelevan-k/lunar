using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Media.Models;
using Lunar.Domain.Shared;
using Lunar.Windows.Media;
using Xunit;

namespace Lunar.Windows.IntegrationTests.Media;

// Everything here uses a fake media-session backend: nothing touches the machine's real media sessions.
public sealed class GsmtcMediaServiceTests
{
    internal sealed class FakeBackend : IGsmtcBackend
    {
        private readonly object _gate = new();

        public List<MediaSessionReading> Readings { get; set; } = [];

        public MediaException? ReadError { get; set; }

        public TimeSpan ReadDelay { get; set; }

        public int Reads { get; private set; }

        public List<(string Id, GsmtcAction Action)> Sent { get; } = [];

        public List<TimeSpan> SeekTargets { get; } = [];

        public GsmtcCommandResult SendResult { get; set; } = GsmtcCommandResult.Accepted;

        public MediaException? SendError { get; set; }

        public TimeSpan SendDelay { get; set; }

        public int MaxConcurrentSends { get; private set; }

        private int _sending;

        public bool Disposed { get; private set; }

        public bool HasSubscribers => Changed is not null;

        public event Action? Changed;

        public async Task<IReadOnlyList<MediaSessionReading>> ReadAsync(CancellationToken ct)
        {
            lock (_gate)
                Reads++;
            if (ReadDelay > TimeSpan.Zero)
                await Task.Delay(ReadDelay, ct);
            ct.ThrowIfCancellationRequested();
            if (ReadError is not null)
                throw ReadError;
            lock (_gate)
                return [.. Readings];
        }

        public async Task<GsmtcCommandResult> SendAsync(string sessionId, GsmtcAction action, TimeSpan target, CancellationToken ct)
        {
            var running = Interlocked.Increment(ref _sending);
            try
            {
                lock (_gate)
                {
                    MaxConcurrentSends = Math.Max(MaxConcurrentSends, running);
                    Sent.Add((sessionId, action));
                    if (action == GsmtcAction.Seek)
                        SeekTargets.Add(target);
                }

                if (SendDelay > TimeSpan.Zero)
                    await Task.Delay(SendDelay, ct);
                ct.ThrowIfCancellationRequested();
                if (SendError is not null)
                    throw SendError;
                return SendResult;
            }
            finally
            {
                Interlocked.Decrement(ref _sending);
            }
        }

        public void Raise() => Changed?.Invoke();

        public void Dispose() => Disposed = true;
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Recorder : IObserver<IReadOnlyList<MediaSessionSnapshot>>
    {
        private readonly object _gate = new();

        public List<IReadOnlyList<MediaSessionSnapshot>> Values { get; } = [];

        public bool Completed { get; private set; }

        public void OnNext(IReadOnlyList<MediaSessionSnapshot> value)
        {
            lock (_gate)
                Values.Add(value);
        }

        public void OnError(Exception error) => throw new InvalidOperationException("OnError must never be called.");

        public void OnCompleted() => Completed = true;

        public IReadOnlyList<MediaSessionSnapshot>[] Snapshot()
        {
            lock (_gate)
                return [.. Values];
        }
    }

    private sealed class CollectingLog : IDiagnosticLog
    {
        public List<DiagnosticEvent> Events { get; } = [];

        public void Write(DiagnosticEvent diagnosticEvent)
        {
            lock (Events)
                Events.Add(diagnosticEvent);
        }
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private static MediaSessionReading Reading(
        string id = "player.exe",
        string? title = "Song",
        string? artist = "Artist",
        GsmtcStatus status = GsmtcStatus.Playing,
        int positionSeconds = 30,
        int durationSeconds = 200,
        bool current = true,
        byte[]? artwork = null) =>
        new(id, "Player", title, artist, status, TimeSpan.Zero, TimeSpan.FromSeconds(durationSeconds), TimeSpan.FromSeconds(positionSeconds), T0,
            1.0, CanPlay: true, CanPause: true, CanNext: true, CanPrevious: true, CanSeek: true, current, artwork ?? []);

    private static GsmtcMediaService Create(FakeBackend backend, IDiagnosticLog? log = null, FixedTime? time = null, TimeSpan? timeout = null) =>
        new(backend, log, time ?? new FixedTime(T0), timeout ?? TimeSpan.FromSeconds(5));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 3000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < end)
            await Task.Delay(5, Ct);
        Assert.True(condition(), "Condition not reached in time.");
    }

    // ---- reading -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task NoSessionIsAnEmptyListNotAnError()
    {
        await using var service = Create(new FakeBackend());

        var read = await service.GetSessionsAsync(Ct);

        Assert.True(read.IsSuccess);
        Assert.Empty(read.Value);
    }

    [Fact]
    public async Task OneSessionIsDescribedCompletely()
    {
        var backend = new FakeBackend { Readings = [Reading(artwork: [1, 2, 3])] };
        await using var service = Create(backend);

        var session = Assert.Single((await service.GetSessionsAsync(Ct)).Value);

        Assert.Equal("player.exe", session.Id);
        Assert.Equal("Player", session.AppName);
        Assert.Equal("Song", session.Title);
        Assert.Equal("Artist", session.Artist);
        Assert.Equal(MediaPlaybackStatus.Playing, session.Status);
        Assert.Equal(TimeSpan.FromSeconds(30), session.Position);
        Assert.Equal(TimeSpan.FromSeconds(200), session.Duration);
        Assert.Equal(new MediaCapabilities(true, true, true, true), session.Capabilities);
        Assert.True(session.IsCurrent);
        Assert.True(session.CanSeek);
        Assert.Equal<byte>([1, 2, 3], session.Artwork.ToArray());
    }

    [Fact]
    public async Task SeveralSessionsAreAllListedWithOneCurrent()
    {
        var backend = new FakeBackend
        {
            Readings =
            [
                Reading(id: "a.exe", current: false),
                Reading(id: "b.exe", current: true, status: GsmtcStatus.Paused),
                Reading(id: "b.exe#2", current: false, status: GsmtcStatus.Stopped)
            ]
        };
        await using var service = Create(backend);

        var sessions = (await service.GetSessionsAsync(Ct)).Value;

        Assert.Equal(["a.exe", "b.exe", "b.exe#2"], sessions.Select(s => s.Id));
        Assert.Equal("b.exe", Assert.Single(sessions, s => s.IsCurrent).Id);
        Assert.Equal([MediaPlaybackStatus.Playing, MediaPlaybackStatus.Paused, MediaPlaybackStatus.Stopped], sessions.Select(s => s.Status));
    }

    [Fact]
    public async Task PositionIsMovedForwardByTheTimeSinceWindowsStampedIt()
    {
        var time = new FixedTime(T0.AddSeconds(12));
        var backend = new FakeBackend { Readings = [Reading(positionSeconds: 30)] };
        await using var service = Create(backend, time: time);

        var session = Assert.Single((await service.GetSessionsAsync(Ct)).Value);

        Assert.Equal(TimeSpan.FromSeconds(42), session.Position);
    }

    [Fact]
    public async Task ALiveStreamHasNoDurationNoPositionAndCannotBeSeeked()
    {
        var live = Reading() with { End = TimeSpan.Zero };
        await using var service = Create(new FakeBackend { Readings = [live] });

        var session = Assert.Single((await service.GetSessionsAsync(Ct)).Value);

        Assert.Null(session.Duration);
        Assert.Null(session.Position);
        Assert.False(session.CanSeek); // even though the app advertised it
    }

    [Fact]
    public async Task AppWithoutMetadataStillAppears()
    {
        var bare = Reading(title: null, artist: "  ");
        await using var service = Create(new FakeBackend { Readings = [bare] });

        var session = Assert.Single((await service.GetSessionsAsync(Ct)).Value);

        Assert.Null(session.Title);
        Assert.Null(session.Artist);
        Assert.Equal("Player", session.AppName);
    }

    [Fact]
    public async Task OversizeArtworkIsLeftOut()
    {
        var big = Reading(artwork: new byte[MediaSessionSnapshot.MaxArtworkBytes + 1]);
        await using var service = Create(new FakeBackend { Readings = [big] });

        var session = Assert.Single((await service.GetSessionsAsync(Ct)).Value);

        Assert.True(session.Artwork.IsEmpty);
    }

    // ---- failures ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("NotSupported", ErrorCode.Unsupported, false)]
    [InlineData("AccessDenied", ErrorCode.AccessDenied, false)]
    [InlineData("Other", ErrorCode.Unexpected, true)]
    public async Task EveryFailureIsTyped(string failure, ErrorCode code, bool retryable)
    {
        var backend = new FakeBackend { ReadError = new MediaException(Enum.Parse<MediaFailure>(failure), 5, "x") };
        await using var service = Create(backend);

        var read = await service.GetSessionsAsync(Ct);

        Assert.True(read.IsFailure);
        Assert.Equal(code, read.Error!.Code);
        Assert.Equal(retryable, read.Error.Retryable);
    }

    [Fact]
    public async Task AReadThatNeverFinishesTimesOut()
    {
        var backend = new FakeBackend { ReadDelay = TimeSpan.FromSeconds(30) };
        await using var service = Create(backend, timeout: TimeSpan.FromMilliseconds(80));

        var read = await service.GetSessionsAsync(Ct);

        Assert.Equal(ErrorCode.Timeout, read.Error!.Code);
        Assert.True(read.Error.Retryable);
    }

    [Fact]
    public async Task ACancelledCallerGetsCancelledNotATimeout()
    {
        var backend = new FakeBackend { ReadDelay = TimeSpan.FromSeconds(30) };
        await using var service = Create(backend);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var read = await service.GetSessionsAsync(cts.Token);

        Assert.True(read.IsCancelled);
    }

    [Fact]
    public async Task AlreadyCancelledNeverTouchesWindows()
    {
        var backend = new FakeBackend { Readings = [Reading()] };
        await using var service = Create(backend);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Assert.True((await service.GetSessionsAsync(cts.Token)).IsCancelled);
        Assert.Equal(0, backend.Reads);
    }

    [Fact]
    public async Task TheServiceKeepsWorkingAfterAFailure()
    {
        var backend = new FakeBackend { ReadError = new MediaException(MediaFailure.Other, 31, "x") };
        await using var service = Create(backend);
        Assert.True((await service.GetSessionsAsync(Ct)).IsFailure);

        backend.ReadError = null;
        backend.Readings = [Reading()];

        Assert.Single((await service.GetSessionsAsync(Ct)).Value);
    }

    // ---- changes -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARealChangeIsPublishedAsTheWholeListAndARepeatIsNot()
    {
        var backend = new FakeBackend { Readings = [Reading(title: "One")] };
        await using var service = Create(backend);
        var recorder = new Recorder();
        using var subscription = service.Changes.Subscribe(recorder);
        _ = await service.GetSessionsAsync(Ct);

        backend.Raise(); // nothing changed: no new list
        await Task.Delay(100, Ct);
        backend.Readings = [Reading(title: "Two")];
        backend.Raise();
        await WaitFor(() => recorder.Snapshot().Length >= 2);
        backend.Raise();
        await Task.Delay(100, Ct);

        var seen = recorder.Snapshot();
        Assert.Equal(2, seen.Length);
        Assert.Equal("One", Assert.Single(seen[0]).Title);
        Assert.Equal("Two", Assert.Single(seen[1]).Title);
    }

    [Fact]
    public async Task ASessionThatClosesIsMissingFromTheNextList()
    {
        var backend = new FakeBackend { Readings = [Reading(id: "a.exe"), Reading(id: "b.exe", current: false)] };
        await using var service = Create(backend);
        var recorder = new Recorder();
        using var subscription = service.Changes.Subscribe(recorder);
        _ = await service.GetSessionsAsync(Ct);

        backend.Readings = [Reading(id: "a.exe")];
        backend.Raise();
        await WaitFor(() => recorder.Snapshot().Length >= 2);

        Assert.Equal(["a.exe", "b.exe"], recorder.Snapshot()[0].Select(s => s.Id));
        Assert.Equal(["a.exe"], recorder.Snapshot()[1].Select(s => s.Id));
    }

    [Fact]
    public async Task AllSessionsClosingPublishesAnEmptyList()
    {
        var backend = new FakeBackend { Readings = [Reading()] };
        await using var service = Create(backend);
        var recorder = new Recorder();
        using var subscription = service.Changes.Subscribe(recorder);
        _ = await service.GetSessionsAsync(Ct);

        backend.Readings = [];
        backend.Raise();
        await WaitFor(() => recorder.Snapshot().Length >= 2);

        Assert.Empty(recorder.Snapshot()[1]);
    }

    [Fact]
    public async Task TheCurrentSessionChangingIsAChange()
    {
        var backend = new FakeBackend { Readings = [Reading(id: "a.exe", current: true), Reading(id: "b.exe", current: false)] };
        await using var service = Create(backend);
        var recorder = new Recorder();
        using var subscription = service.Changes.Subscribe(recorder);
        _ = await service.GetSessionsAsync(Ct);

        backend.Readings = [Reading(id: "a.exe", current: false), Reading(id: "b.exe", current: true)];
        backend.Raise();
        await WaitFor(() => recorder.Snapshot().Length >= 2);

        Assert.Equal("b.exe", Assert.Single(recorder.Snapshot()[1], s => s.IsCurrent).Id);
    }

    [Fact]
    public async Task ASeekWhilePlayingIsAChangeButTimePassingIsNot()
    {
        var time = new FixedTime(T0);
        var backend = new FakeBackend { Readings = [Reading(positionSeconds: 30)] };
        await using var service = Create(backend, time: time);
        var recorder = new Recorder();
        using var subscription = service.Changes.Subscribe(recorder);
        _ = await service.GetSessionsAsync(Ct);

        time.Now = T0.AddSeconds(20); // the clock moves on, Windows reports nothing new
        backend.Raise();
        await Task.Delay(100, Ct);
        Assert.Single(recorder.Snapshot());

        backend.Readings = [Reading(positionSeconds: 120) with { PositionStamp = T0.AddSeconds(20) }]; // the user seeked
        backend.Raise();
        await WaitFor(() => recorder.Snapshot().Length >= 2);

        Assert.Equal(TimeSpan.FromSeconds(120), Assert.Single(recorder.Snapshot()[1]).Position);
    }

    [Fact]
    public async Task NewArtworkForTheSameTrackNameIsAChange()
    {
        var backend = new FakeBackend { Readings = [Reading(artwork: [1, 2, 3])] };
        await using var service = Create(backend);
        var recorder = new Recorder();
        using var subscription = service.Changes.Subscribe(recorder);
        _ = await service.GetSessionsAsync(Ct);

        backend.Readings = [Reading(artwork: [9, 9, 9])];
        backend.Raise();
        await WaitFor(() => recorder.Snapshot().Length >= 2);

        Assert.Equal<byte>([9, 9, 9], Assert.Single(recorder.Snapshot()[1]).Artwork.ToArray());
    }

    [Fact]
    public async Task ABurstOfEventsCausesFewReads()
    {
        var backend = new FakeBackend { Readings = [Reading()] };
        await using var service = Create(backend);
        _ = await service.GetSessionsAsync(Ct);
        var before = backend.Reads;

        Parallel.For(0, 500, _ => backend.Raise());
        await Task.Delay(300, Ct);

        Assert.InRange(backend.Reads - before, 1, 50);
    }

    [Fact]
    public async Task EachObserverGetsTheSameListsAndALateSubscriberStartsFromNothing()
    {
        var backend = new FakeBackend { Readings = [Reading()] };
        await using var service = Create(backend);
        var first = new Recorder();
        using var firstSubscription = service.Changes.Subscribe(first);
        _ = await service.GetSessionsAsync(Ct);

        var late = new Recorder();
        using var lateSubscription = service.Changes.Subscribe(late);

        Assert.Single(first.Snapshot());
        Assert.Empty(late.Snapshot()); // no replay (ADR-0006)
    }

    // ---- transport commands (BE-16) ---------------------------------------------------------------------------------

    [Fact]
    public async Task PlayPauseOnAPlayingSessionPausesIt()
    {
        var backend = new FakeBackend { Readings = [Reading(status: GsmtcStatus.Playing)] };
        await using var service = Create(backend);

        var result = await service.PlayPauseAsync("player.exe", Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal([("player.exe", GsmtcAction.Pause)], backend.Sent);
    }

    [Theory]
    [InlineData("Paused")]
    [InlineData("Stopped")]
    public async Task PlayPauseOnAPausedOrStoppedSessionPlaysIt(string status)
    {
        var backend = new FakeBackend { Readings = [Reading(status: Enum.Parse<GsmtcStatus>(status))] };
        await using var service = Create(backend);

        Assert.True((await service.PlayPauseAsync("player.exe", Ct)).IsSuccess);

        Assert.Equal([("player.exe", GsmtcAction.Play)], backend.Sent);
    }

    [Fact]
    public async Task AToggleOnlyPlayerIsToggled()
    {
        var backend = new FakeBackend { Readings = [Reading() with { CanPlay = false, CanPause = false, CanToggle = true }] };
        await using var service = Create(backend);

        Assert.True((await service.PlayPauseAsync("player.exe", Ct)).IsSuccess);

        Assert.Equal([("player.exe", GsmtcAction.Toggle)], backend.Sent);
    }

    [Fact]
    public async Task NextAndPreviousAreSentToTheNamedSession()
    {
        var backend = new FakeBackend { Readings = [Reading(id: "a.exe"), Reading(id: "b.exe", current: false)] };
        await using var service = Create(backend);

        Assert.True((await service.NextAsync("b.exe", Ct)).IsSuccess);
        Assert.True((await service.PreviousAsync("a.exe", Ct)).IsSuccess);

        Assert.Equal([("b.exe", GsmtcAction.Next), ("a.exe", GsmtcAction.Previous)], backend.Sent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-such-session")]
    public async Task AnUnknownOrBlankSessionIsInvalidInputAndNothingIsSent(string id)
    {
        var backend = new FakeBackend { Readings = [Reading()] };
        await using var service = Create(backend);

        var result = await service.NextAsync(id, Ct);

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.Empty(backend.Sent);
    }

    [Fact]
    public async Task ACommandTheSessionDoesNotAdvertiseIsUnsupportedAndNeverSent()
    {
        var backend = new FakeBackend { Readings = [Reading() with { CanNext = false, CanPrevious = false, CanPlay = false, CanPause = false }] };
        await using var service = Create(backend);

        var results = new[]
        {
            await service.PlayPauseAsync("player.exe", Ct),
            await service.NextAsync("player.exe", Ct),
            await service.PreviousAsync("player.exe", Ct)
        };

        Assert.All(results, r => Assert.Equal(ErrorCode.Unsupported, r.Error!.Code));
        Assert.Empty(backend.Sent);
    }

    [Fact]
    public async Task APlayerChangingTrackIsNotAskedToPlayOrPause()
    {
        var backend = new FakeBackend { Readings = [Reading(status: GsmtcStatus.Changing)] };
        await using var service = Create(backend);

        var result = await service.PlayPauseAsync("player.exe", Ct);

        Assert.Equal(ErrorCode.Unsupported, result.Error!.Code);
        Assert.Empty(backend.Sent);
    }

    [Fact]
    public async Task TheCommandIsCheckedAgainstWhatTheSessionAdvertisesNowNotWhenItWasLastRead()
    {
        var backend = new FakeBackend { Readings = [Reading()] };
        await using var service = Create(backend);
        _ = await service.GetSessionsAsync(Ct); // the UI saw "next is available"

        backend.Readings = [Reading() with { CanNext = false }]; // and then the player stopped offering it

        var result = await service.NextAsync("player.exe", Ct);

        Assert.Equal(ErrorCode.Unsupported, result.Error!.Code);
        Assert.Empty(backend.Sent);
    }

    [Fact]
    public async Task ASessionThatClosedBetweenTheReadAndTheSendIsInvalidInput()
    {
        var backend = new FakeBackend { Readings = [Reading()], SendResult = GsmtcCommandResult.SessionGone };
        await using var service = Create(backend);

        var result = await service.NextAsync("player.exe", Ct);

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
    }

    [Fact]
    public async Task APlayerThatDoesNotAcceptTheCommandIsNotReportedAsSuccess()
    {
        var backend = new FakeBackend { Readings = [Reading()], SendResult = GsmtcCommandResult.Refused };
        await using var service = Create(backend);

        var result = await service.NextAsync("player.exe", Ct);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCode.Unexpected, result.Error!.Code);
        Assert.True(result.Error.Retryable);
    }

    [Theory]
    [InlineData("NotSupported", ErrorCode.Unsupported, false)]
    [InlineData("AccessDenied", ErrorCode.AccessDenied, false)]
    [InlineData("Other", ErrorCode.Unexpected, true)]
    public async Task EveryCommandFailureIsTyped(string failure, ErrorCode code, bool retryable)
    {
        var backend = new FakeBackend { Readings = [Reading()], SendError = new MediaException(Enum.Parse<MediaFailure>(failure), 5, "x") };
        await using var service = Create(backend);

        var result = await service.PlayPauseAsync("player.exe", Ct);

        Assert.Equal(code, result.Error!.Code);
        Assert.Equal(retryable, result.Error.Retryable);
    }

    [Fact]
    public async Task APlayerThatNeverAnswersTimesOut()
    {
        var backend = new FakeBackend { Readings = [Reading()], SendDelay = TimeSpan.FromSeconds(30) };
        await using var service = Create(backend, timeout: TimeSpan.FromMilliseconds(80));

        var result = await service.NextAsync("player.exe", Ct);

        Assert.Equal(ErrorCode.Timeout, result.Error!.Code);
        Assert.True(result.Error.Retryable);
    }

    [Fact]
    public async Task ACallerWhoCancelsWhileTheCommandIsOutGetsCancelled()
    {
        var backend = new FakeBackend { Readings = [Reading()], SendDelay = TimeSpan.FromSeconds(30) };
        await using var service = Create(backend);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));

        var result = await service.NextAsync("player.exe", cts.Token);

        Assert.True(result.IsCancelled);
    }

    [Fact]
    public async Task AReadFailureStopsTheCommandBeforeItIsSent()
    {
        var backend = new FakeBackend { ReadError = new MediaException(MediaFailure.Other, 31, "x") };
        await using var service = Create(backend);

        var result = await service.NextAsync("player.exe", Ct);

        Assert.True(result.IsFailure);
        Assert.Empty(backend.Sent);
    }

    [Fact]
    public async Task CommandsNeverRunAtTheSameTimeOnTheBackend()
    {
        var backend = new FakeBackend { Readings = [Reading()], SendDelay = TimeSpan.FromMilliseconds(5) };
        await using var service = Create(backend);

        var results = await Task.WhenAll(Enumerable.Range(0, 30).Select(i => i % 2 == 0 ? service.NextAsync("player.exe", Ct) : service.PreviousAsync("player.exe", Ct)));

        Assert.All(results, r => Assert.True(r.IsSuccess, r.Error?.SafeMessage));
        Assert.Equal(30, backend.Sent.Count);
        Assert.Equal(1, backend.MaxConcurrentSends);
    }

    [Fact]
    public async Task ACommandDoesNotGuessTheNewStateItOnlyReportsWhatThePlayerAccepted()
    {
        var backend = new FakeBackend { Readings = [Reading(status: GsmtcStatus.Playing)] };
        await using var service = Create(backend);
        var recorder = new Recorder();
        using var subscription = service.Changes.Subscribe(recorder);
        _ = await service.GetSessionsAsync(Ct);

        Assert.True((await service.PlayPauseAsync("player.exe", Ct)).IsSuccess);
        await Task.Delay(100, Ct);

        Assert.Single(recorder.Snapshot()); // the state changes only when Windows says so (the player did not change in this fake)
    }

    [Fact]
    public async Task CommandFailuresNeverPutMediaDetailsInTheLog()
    {
        var log = new CollectingLog();
        var backend = new FakeBackend
        {
            Readings = [Reading(title: "Secret Song", artist: "Secret Artist")],
            SendError = new MediaException(MediaFailure.Other, 31, "Secret Song by Secret Artist in Player")
        };
        await using var service = Create(backend, log);

        _ = await service.NextAsync("player.exe", Ct);
        backend.SendError = null;
        backend.SendDelay = TimeSpan.FromSeconds(30);
        _ = await Create(backend, log, timeout: TimeSpan.FromMilliseconds(30)).NextAsync("player.exe", Ct);

        Assert.NotEmpty(log.Events);
        foreach (var entry in log.Events)
        {
            Assert.DoesNotContain("Secret", entry.ToString(), StringComparison.Ordinal);
            Assert.StartsWith("media.", entry.EventId, StringComparison.Ordinal);
        }
    }

    // ---- seeking (BE-17) ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASeekIsSentToTheNamedSessionAtTheRequestedPosition()
    {
        var backend = new FakeBackend { Readings = [Reading("a.exe"), Reading("b.exe")] };
        await using var service = Create(backend);

        var result = await service.SeekAsync("b.exe", TimeSpan.FromSeconds(90), Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal([("b.exe", GsmtcAction.Seek)], backend.Sent);
        Assert.Equal([TimeSpan.FromSeconds(90)], backend.SeekTargets);
    }

    [Fact]
    public async Task TheTargetIsConvertedToTheTimelineOfThePlayer()
    {
        // The player counts from 1 minute; position 10 s into the media is 1:10 on its timeline.
        var reading = Reading() with { Start = TimeSpan.FromSeconds(60), End = TimeSpan.FromSeconds(260), Position = TimeSpan.FromSeconds(90) };
        var backend = new FakeBackend { Readings = [reading] };
        await using var service = Create(backend);

        _ = await service.SeekAsync("player.exe", TimeSpan.FromSeconds(10), Ct);

        Assert.Equal([TimeSpan.FromSeconds(70)], backend.SeekTargets);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(200, 200)]
    [InlineData(500, 200)]
    public async Task TheTargetIsKeptInsideTheMedia(int requested, int expected)
    {
        var backend = new FakeBackend { Readings = [Reading(durationSeconds: 200)] };
        await using var service = Create(backend);

        var result = await service.SeekAsync("player.exe", TimeSpan.FromSeconds(requested), Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal([TimeSpan.FromSeconds(expected)], backend.SeekTargets);
    }

    [Fact]
    public async Task APausedSessionIsSeekedLikeAnyOther()
    {
        var backend = new FakeBackend { Readings = [Reading(status: GsmtcStatus.Paused)] };
        await using var service = Create(backend);

        var result = await service.SeekAsync("player.exe", TimeSpan.FromSeconds(40), Ct);

        Assert.True(result.IsSuccess);
        Assert.Single(backend.SeekTargets);
    }

    [Fact]
    public async Task ASessionWithoutAFiniteTimelineIsUnsupportedAndNothingIsSent()
    {
        // Zero duration (end at start), an end before the start, and an unbounded sentinel are all "no timeline".
        foreach (var reading in new[]
                 {
                     Reading(durationSeconds: 0),
                     Reading() with { End = TimeSpan.FromSeconds(-5) },
                     Reading() with { End = TimeSpan.FromDays(400) }
                 })
        {
            var backend = new FakeBackend { Readings = [reading] };
            await using var service = Create(backend);

            var result = await service.SeekAsync("player.exe", TimeSpan.FromSeconds(5), Ct);

            Assert.Equal(ErrorCode.Unsupported, result.Error!.Code);
            Assert.Empty(backend.Sent);
        }
    }

    [Fact]
    public async Task ASessionThatDoesNotAdvertiseSeekingIsUnsupportedAndNothingIsSent()
    {
        var backend = new FakeBackend { Readings = [Reading() with { CanSeek = false }] };
        await using var service = Create(backend);

        var result = await service.SeekAsync("player.exe", TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(ErrorCode.Unsupported, result.Error!.Code);
        Assert.Empty(backend.Sent);
    }

    [Fact]
    public async Task SeekingIsCheckedAgainstWhatThePlayerAdvertisesNowNotWhatTheUiSaw()
    {
        var backend = new FakeBackend { Readings = [Reading()] };
        await using var service = Create(backend);
        Assert.True((await service.GetSessionsAsync(Ct)).Value[0].CanSeek);
        backend.Readings = [Reading() with { CanSeek = false }];

        var result = await service.SeekAsync("player.exe", TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(ErrorCode.Unsupported, result.Error!.Code);
        Assert.Empty(backend.Sent);
    }

    [Theory]
    [InlineData("no-such-session")]
    [InlineData("")]
    [InlineData("  ")]
    public async Task SeekingAnUnknownOrBlankSessionIsInvalidInputAndNothingIsSent(string id)
    {
        var backend = new FakeBackend { Readings = [Reading()] };
        await using var service = Create(backend);

        var result = await service.SeekAsync(id, TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.Empty(backend.Sent);
    }

    [Fact]
    public async Task ASessionThatClosesUnderTheSeekIsInvalidInput()
    {
        var backend = new FakeBackend { Readings = [Reading()], SendResult = GsmtcCommandResult.SessionGone };
        await using var service = Create(backend);

        var result = await service.SeekAsync("player.exe", TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
    }

    [Fact]
    public async Task APlayerThatRefusesTheSeekIsARetryableFailure()
    {
        var backend = new FakeBackend { Readings = [Reading()], SendResult = GsmtcCommandResult.Refused };
        await using var service = Create(backend);

        var result = await service.SeekAsync("player.exe", TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(ErrorCode.Unexpected, result.Error!.Code);
        Assert.True(result.Error.Retryable);
    }

    [Fact]
    public async Task ASeekThatTheWindowsApiFailsOrDoesNotAnswerIsTyped()
    {
        var backend = new FakeBackend { Readings = [Reading()], SendError = new MediaException(MediaFailure.AccessDenied, 5, "Secret title") };
        await using var service = Create(backend);
        Assert.Equal(ErrorCode.AccessDenied, (await service.SeekAsync("player.exe", TimeSpan.FromSeconds(5), Ct)).Error!.Code);

        backend.SendError = null;
        backend.SendDelay = TimeSpan.FromSeconds(30);
        var slow = await Create(backend, timeout: TimeSpan.FromMilliseconds(30)).SeekAsync("player.exe", TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(ErrorCode.Timeout, slow.Error!.Code);
    }

    [Fact]
    public async Task SeekingHonoursCancellationAndDisposal()
    {
        var service = Create(new FakeBackend { Readings = [Reading()] });
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        Assert.True((await service.SeekAsync("player.exe", TimeSpan.FromSeconds(5), cts.Token)).IsCancelled);

        await service.DisposeAsync();

        Assert.Equal(ErrorCode.DeviceUnavailable, (await service.SeekAsync("player.exe", TimeSpan.FromSeconds(5), Ct)).Error!.Code);
    }

    [Fact]
    public async Task SeeksAndCommandsGoOneAtATime()
    {
        var backend = new FakeBackend { Readings = [Reading()], SendDelay = TimeSpan.FromMilliseconds(5) };
        await using var service = Create(backend);

        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(
            () => i % 2 == 0 ? service.SeekAsync("player.exe", TimeSpan.FromSeconds(i), Ct) : service.NextAsync("player.exe", Ct), Ct)));

        Assert.Equal(40, backend.Sent.Count);
        Assert.Equal(1, backend.MaxConcurrentSends);
    }

    [Fact]
    public async Task CommandsHonourCancellationAndDisposal()
    {
        var service = Create(new FakeBackend { Readings = [Reading()] });
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        Assert.True((await service.PlayPauseAsync("player.exe", cts.Token)).IsCancelled);

        await service.DisposeAsync();

        Assert.Equal(ErrorCode.DeviceUnavailable, (await service.NextAsync("player.exe", Ct)).Error!.Code);
    }

    // ---- lifetime and privacy ----------------------------------------------------------------------------------------

    [Fact]
    public async Task DisposalCompletesObserversClosesTheBackendAndIsIdempotent()
    {
        var backend = new FakeBackend { Readings = [Reading()] };
        var service = Create(backend);
        var recorder = new Recorder();
        service.Changes.Subscribe(recorder);

        await service.DisposeAsync();
        await service.DisposeAsync();
        service.Dispose();

        Assert.True(recorder.Completed);
        Assert.True(backend.Disposed);
        Assert.False(backend.HasSubscribers);
    }

    [Fact]
    public async Task ReadingAfterDisposalFailsAsClosedAndEventsAreIgnored()
    {
        var backend = new FakeBackend { Readings = [Reading()] };
        var service = Create(backend);
        await service.DisposeAsync();
        var reads = backend.Reads;

        var read = await service.GetSessionsAsync(Ct);
        backend.Raise();
        await Task.Delay(50, Ct);

        Assert.Equal(ErrorCode.DeviceUnavailable, read.Error!.Code);
        Assert.Equal(reads, backend.Reads);
    }

    [Fact]
    public async Task SynchronousDisposalWorksToo()
    {
        var backend = new FakeBackend();
        var service = Create(backend);

        service.Dispose();

        Assert.True(backend.Disposed);
        Assert.Equal(ErrorCode.DeviceUnavailable, (await service.GetSessionsAsync(Ct)).Error!.Code);
    }

    [Fact]
    public async Task NoTitleArtistOrAppNameEverReachesTheLog()
    {
        var log = new CollectingLog();
        var secret = Reading(title: "Secret Song", artist: "Secret Artist");
        var backend = new FakeBackend { Readings = [secret], ReadError = new MediaException(MediaFailure.Other, 31, "Secret Song by Secret Artist in Player") };
        await using var service = Create(backend, log);

        _ = await service.GetSessionsAsync(Ct);
        backend.ReadError = null;
        _ = await service.GetSessionsAsync(Ct);
        backend.ReadDelay = TimeSpan.FromSeconds(30);
        _ = await Create(backend, log, timeout: TimeSpan.FromMilliseconds(30)).GetSessionsAsync(Ct);

        Assert.NotEmpty(log.Events);
        foreach (var entry in log.Events)
        {
            var text = entry.ToString();
            Assert.DoesNotContain("Secret", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Player", text, StringComparison.Ordinal);
            Assert.StartsWith("media.", entry.EventId, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ConstructorArgumentsAreValidated()
    {
        await using var backendOwner = Create(new FakeBackend());

        Assert.Throws<ArgumentNullException>(() => new GsmtcMediaService(null!, null, TimeProvider.System, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentNullException>(() => new GsmtcMediaService(new FakeBackend(), null, null!, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GsmtcMediaService(new FakeBackend(), null, TimeProvider.System, TimeSpan.Zero));
    }
}
