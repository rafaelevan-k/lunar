using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lunar.Application.Abstractions.Media;
using Lunar.Application.Media.Models;
using Lunar.Application.Media.UseCases;
using Lunar.Application.Tests.Fakes;
using Lunar.Desktop.Features.MediaControls;
using Lunar.Desktop.Shell;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Desktop.UITests.Features;

// FE-17: what the Media page shows and does, over a real tracker and transport and a scripted media service. Time and the
// position timer are fakes driven by hand. Commands are executed the way a click does: inside the dispatcher where WPF needs it.
public sealed class MediaPageViewModelTests
{
    private sealed class ScriptedMedia : IMediaService
    {
        private readonly TestObservable<IReadOnlyList<MediaSessionSnapshot>> _changes = new();
        private List<MediaSessionSnapshot> _sessions = [];

        public Result<IReadOnlyList<MediaSessionSnapshot>>? ReadResult { get; set; }

        public Result CommandResult { get; set; } = Result.Success();

        public List<string> Commands { get; } = [];

        public IObservable<IReadOnlyList<MediaSessionSnapshot>> Changes => _changes;

        public void Set(params MediaSessionSnapshot[] sessions)
        {
            _sessions = [.. sessions];
            _changes.Publish(_sessions.ToArray());
        }

        public Task<Result<IReadOnlyList<MediaSessionSnapshot>>> GetSessionsAsync(CancellationToken ct) =>
            Task.FromResult(ReadResult ?? Result.Success<IReadOnlyList<MediaSessionSnapshot>>(_sessions.ToArray()));

        public Task<Result> PlayPauseAsync(string sessionId, CancellationToken ct) => Run("playpause:" + sessionId);

        public Task<Result> NextAsync(string sessionId, CancellationToken ct) => Run("next:" + sessionId);

        public Task<Result> PreviousAsync(string sessionId, CancellationToken ct) => Run("previous:" + sessionId);

        public Task<Result> SeekAsync(string sessionId, TimeSpan position, CancellationToken ct) => Run("seek:" + sessionId);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private Task<Result> Run(string name)
        {
            lock (Commands)
                Commands.Add(name);
            return Task.FromResult(CommandResult);
        }
    }

    private sealed class FakeTimer : IOneShotTimer
    {
        private Action? _callback;

        public TimeSpan? Pending { get; private set; }

        public int Schedules { get; private set; }

        public bool Disposed { get; private set; }

        public void Schedule(TimeSpan delay, Action callback)
        {
            Pending = delay;
            _callback = callback;
            Schedules++;
        }

        public void Cancel()
        {
            Pending = null;
            _callback = null;
        }

        public void Fire()
        {
            var callback = _callback;
            Pending = null;
            _callback = null;
            callback?.Invoke();
        }

        public void Dispose()
        {
            Disposed = true;
            Cancel();
        }
    }

    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Rig : IDisposable
    {
        public ScriptedMedia Media { get; } = new();

        public FakeTimer Timer { get; } = new();

        public FakeTime Time { get; } = new();

        public MediaSessionTracker Tracker { get; }

        public MediaPageViewModel Model { get; }

        public Rig()
        {
            Tracker = new MediaSessionTracker(Media);
            Model = new MediaPageViewModel(Tracker, new MediaTransport(Media), work => work(), null, Time, Timer);
        }

        public Rig Started(params MediaSessionSnapshot[] sessions)
        {
            Media.Set(sessions);
            Model.InitializeAsync(Ct).GetAwaiter().GetResult();
            return this;
        }

        public void Dispose()
        {
            Model.Dispose();
            Tracker.Dispose();
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MediaSessionSnapshot S(
        string id = "a",
        string? title = "Song",
        string? artist = "Artist",
        string? app = "Player",
        MediaPlaybackStatus status = MediaPlaybackStatus.Paused,
        int? positionSeconds = 30,
        int? durationSeconds = 200,
        bool playPause = true,
        bool next = true,
        bool previous = true,
        bool seek = true,
        bool current = true,
        byte[]? artwork = null) =>
        new(id, app, title, artist, status,
            positionSeconds is { } p ? TimeSpan.FromSeconds(p) : null,
            durationSeconds is { } d ? TimeSpan.FromSeconds(d) : null,
            new MediaCapabilities(playPause, next, previous, seek), current, artwork ?? []);

    private static void Press(ICommand command) => command.Execute(null);

    private static async Task Settled(MediaPageViewModel model)
    {
        // Commands are async void entry points: wait until none is running.
        var end = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < end && ((AsyncRelayCommand)model.PlayPauseCommand).IsRunning)
            await Task.Delay(5, Ct);
        await Task.Delay(30, Ct);
    }

    // ---- faces -------------------------------------------------------------------------------------------------------

    [Fact]
    public void BeforeAnythingIsReadThePageIsLoadingAndOffersNothing()
    {
        using var rig = new Rig();

        Assert.Equal(MediaPageMode.Loading, rig.Model.Mode);
        Assert.True(rig.Model.IsLoading);
        Assert.False(rig.Model.HasSession);
        Assert.False(rig.Model.PlayPauseCommand.CanExecute(null));
        Assert.False(rig.Model.NextCommand.CanExecute(null));
        Assert.False(rig.Model.PreviousCommand.CanExecute(null));
        Assert.False(rig.Model.RetryCommand.CanExecute(null));
    }

    [Fact]
    public void NoSessionIsExplainedNotLeftBlank()
    {
        using var rig = new Rig().Started();

        Assert.Equal(MediaPageMode.NoSession, rig.Model.Mode);
        Assert.True(rig.Model.IsNoSession);
        Assert.Equal("Nothing is playing", rig.Model.NoSessionTitle);
        Assert.Contains("Play music or a video", rig.Model.NoSessionHint, StringComparison.Ordinal);
        Assert.Null(rig.Model.Active);
        Assert.False(rig.Model.PlayPauseCommand.CanExecute(null));
    }

    [Fact]
    public void ASessionShowsItsMetadataStateAndTimeline()
    {
        using var rig = new Rig().Started(S(status: MediaPlaybackStatus.Playing, positionSeconds: 30, durationSeconds: 200));

        var model = rig.Model;
        Assert.Equal(MediaPageMode.Session, model.Mode);
        Assert.Equal("Song", model.Title);
        Assert.Equal("Artist", model.Artist);
        Assert.Equal("Player", model.Source);
        Assert.Equal("Playing", model.StatusText);
        Assert.Equal("Player · Playing", model.SourceLine);
        Assert.Equal("Song by Artist, from Player, Playing", model.Summary);
        Assert.True(model.HasTimeline);
        Assert.Equal("00:30", model.PositionText);
        Assert.Equal("03:20", model.DurationText);
        Assert.Equal(0.15, model.Progress, 3);
        Assert.Equal("00:30 of 03:20", model.TimelineLabel);
        Assert.Null(model.TimelineNote);
    }

    [Fact]
    public void AbsentMetadataShowsWordsInsteadOfBlanks()
    {
        using var rig = new Rig().Started(S(title: null, artist: " ", app: null));

        Assert.Equal("Unknown title", rig.Model.Title);
        Assert.Equal("Unknown artist", rig.Model.Artist);
        Assert.Equal("Unknown app", rig.Model.Source);
        Assert.Equal("Unknown app · Paused", rig.Model.SourceLine);
    }

    [Fact]
    public void ALiveStreamOrNoTimelineShowsANoteInsteadOfTimes()
    {
        foreach (var session in new[] { S(durationSeconds: null, positionSeconds: null), S(durationSeconds: 0, positionSeconds: 0) })
        {
            using var rig = new Rig().Started(session);

            Assert.False(rig.Model.HasTimeline);
            Assert.Equal("Live or no timeline", rig.Model.TimelineNote);
            Assert.Equal(string.Empty, rig.Model.PositionText);
            Assert.Equal(string.Empty, rig.Model.DurationText);
            Assert.Equal(0, rig.Model.Progress);
        }
    }

    [Fact]
    public void ATimelineThatCannotBeSeekedSaysSoButStillShowsTimes()
    {
        using var rig = new Rig().Started(S(seek: false));

        Assert.True(rig.Model.HasTimeline);
        Assert.Equal("This app doesn't allow seeking", rig.Model.TimelineNote);
        Assert.Equal("00:30", rig.Model.PositionText);
    }

    [Theory]
    [InlineData(59, "00:59")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "01:00:00")]
    [InlineData(3725, "01:02:05")]
    [InlineData(360000, "100:00:00")]
    public void TimesUseMinutesAndSecondsAndHoursFromOneHour(int seconds, string expected) =>
        Assert.Equal(expected, MediaPageText.FormatTime(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void ANegativeTimeShowsAsZero() => Assert.Equal("00:00", MediaPageText.FormatTime(TimeSpan.FromSeconds(-5)));

    [Fact]
    public void ALongMediaShowsHoursOnBothTimes()
    {
        using var rig = new Rig().Started(S(positionSeconds: 3725, durationSeconds: 7300));

        Assert.Equal("01:02:05", rig.Model.PositionText);
        Assert.Equal("02:01:40", rig.Model.DurationText);
    }

    // ---- buttons -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public void AButtonIsEnabledOnlyWhenThePlayerOffersThatCommand(bool playPause, bool next, bool previous)
    {
        using var rig = new Rig().Started(S(playPause: playPause, next: next, previous: previous));

        Assert.Equal(playPause, rig.Model.PlayPauseCommand.CanExecute(null));
        Assert.Equal(next, rig.Model.NextCommand.CanExecute(null));
        Assert.Equal(previous, rig.Model.PreviousCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(MediaPlaybackStatus.Playing, "Pause", "")]
    [InlineData(MediaPlaybackStatus.Paused, "Play", "")]
    [InlineData(MediaPlaybackStatus.Stopped, "Play", "")]
    [InlineData(MediaPlaybackStatus.Unknown, "Play", "")]
    public void ThePlayPauseButtonShowsWhatPressingItWouldDo(MediaPlaybackStatus status, string label, string glyph)
    {
        using var rig = new Rig().Started(S(status: status));

        Assert.Equal(label, rig.Model.PlayPauseLabel);
        Assert.Equal(glyph, rig.Model.PlayPauseGlyph);
    }

    [Fact]
    public void AnUnknownStateSaysNothingAboutPlayback()
    {
        using var rig = new Rig().Started(S(status: MediaPlaybackStatus.Unknown));

        Assert.Equal(string.Empty, rig.Model.StatusText);
        Assert.Equal("Player", rig.Model.SourceLine);
    }

    [Fact]
    public async Task PressingAButtonSendsItToTheActiveSessionAndChangesNothingByItself()
    {
        using var rig = new Rig().Started(S("a", status: MediaPlaybackStatus.Playing));

        Press(rig.Model.PlayPauseCommand);
        await Settled(rig.Model);
        Press(rig.Model.NextCommand);
        await Settled(rig.Model);
        Press(rig.Model.PreviousCommand);
        await Settled(rig.Model);

        Assert.Equal(["playpause:a", "next:a", "previous:a"], rig.Media.Commands);
        Assert.Equal("Pause", rig.Model.PlayPauseLabel); // the page waits for the player to report the new state
        Assert.Null(rig.Model.ErrorMessage);
    }

    [Fact]
    public async Task ARefusedCommandIsSaidInWordsAndCanBeDismissed()
    {
        using var rig = new Rig().Started(S());
        rig.Media.CommandResult = Result.Failure(new OperationError(ErrorCode.Unexpected, "no", retryable: true));

        Press(rig.Model.NextCommand);
        await Settled(rig.Model);

        Assert.Equal("The player didn't accept that.", rig.Model.ErrorMessage);
        Press(rig.Model.DismissErrorCommand);
        Assert.Null(rig.Model.ErrorMessage);
    }

    [Theory]
    [InlineData(ErrorCode.Unsupported, "This app doesn't offer that.")]
    [InlineData(ErrorCode.InvalidInput, "That player has closed.")]
    [InlineData(ErrorCode.Timeout, "The player didn't answer.")]
    [InlineData(ErrorCode.AccessDenied, "The player didn't accept that.")]
    public void EachFailureHasItsOwnWords(ErrorCode code, string expected) =>
        Assert.Equal(expected, MediaPageText.CommandFailed(code));

    [Fact]
    public async Task AnErrorAboutOnePlayerDoesNotStayWhenAnotherBecomesActive()
    {
        using var rig = new Rig().Started(S("a", current: true), S("b", current: false));
        rig.Media.CommandResult = Result.Failure(new OperationError(ErrorCode.Unexpected, "no"));
        Press(rig.Model.NextCommand);
        await Settled(rig.Model);
        Assert.NotNull(rig.Model.ErrorMessage);

        rig.Media.Set(S("a", current: false), S("b", current: true));

        Assert.Equal("b", rig.Model.Active!.Id);
        Assert.Null(rig.Model.ErrorMessage);
    }

    [Fact]
    public async Task ACommandPressedWhileOneIsRunningIsIgnored()
    {
        var gate = new TaskCompletionSource<Result>();
        var held = new HoldingMedia(gate);
        using var tracker = new MediaSessionTracker(held);
        held.Set(S());
        using var model = new MediaPageViewModel(tracker, new MediaTransport(held), work => work(), null, new FakeTime(), new FakeTimer());
        await model.InitializeAsync(Ct);

        Press(model.NextCommand);
        await Task.Delay(30, Ct);
        Press(model.NextCommand);
        Press(model.NextCommand);
        gate.SetResult(Result.Success());
        await Task.Delay(50, Ct);

        Assert.Equal(1, held.Sent);
    }

    private sealed class HoldingMedia(TaskCompletionSource<Result> gate) : IMediaService
    {
        private readonly TestObservable<IReadOnlyList<MediaSessionSnapshot>> _changes = new();
        private IReadOnlyList<MediaSessionSnapshot> _sessions = [];
        private int _sent;

        public int Sent => Volatile.Read(ref _sent);

        public IObservable<IReadOnlyList<MediaSessionSnapshot>> Changes => _changes;

        public void Set(params MediaSessionSnapshot[] sessions)
        {
            _sessions = sessions;
            _changes.Publish(sessions);
        }

        public Task<Result<IReadOnlyList<MediaSessionSnapshot>>> GetSessionsAsync(CancellationToken ct) =>
            Task.FromResult(Result.Success(_sessions));

        public Task<Result> PlayPauseAsync(string sessionId, CancellationToken ct) => Task.FromResult(Result.Success());

        public Task<Result> NextAsync(string sessionId, CancellationToken ct)
        {
            Interlocked.Increment(ref _sent);
            return gate.Task;
        }

        public Task<Result> PreviousAsync(string sessionId, CancellationToken ct) => Task.FromResult(Result.Success());

        public Task<Result> SeekAsync(string sessionId, TimeSpan position, CancellationToken ct) => Task.FromResult(Result.Success());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ---- changes while open ------------------------------------------------------------------------------------------

    [Fact]
    public void TheTrackersChangesUpdateThePage()
    {
        using var rig = new Rig().Started(S(status: MediaPlaybackStatus.Paused));

        rig.Media.Set(S(title: "Other", status: MediaPlaybackStatus.Playing));

        Assert.Equal("Other", rig.Model.Title);
        Assert.Equal("Playing", rig.Model.StatusText);
        Assert.Equal("Pause", rig.Model.PlayPauseLabel);
    }

    [Fact]
    public void WhenThePlayerClosesThePageSaysNothingIsPlayingAndNoButtonWorks()
    {
        using var rig = new Rig().Started(S());

        rig.Media.Set();

        Assert.True(rig.Model.IsNoSession);
        Assert.Equal(string.Empty, rig.Model.Title);
        Assert.False(rig.Model.PlayPauseCommand.CanExecute(null));
        Assert.False(rig.Model.NextCommand.CanExecute(null));
    }

    [Fact]
    public void ALaterPlayerIsPickedUpAfterNothingWasPlaying()
    {
        using var rig = new Rig().Started();

        rig.Media.Set(S(title: "Back"));

        Assert.True(rig.Model.HasSession);
        Assert.Equal("Back", rig.Model.Title);
    }

    [Fact]
    public async Task TheCommandsActOnTheSessionThatIsActiveNowNotAnOldOne()
    {
        using var rig = new Rig().Started(S("a", current: true));
        rig.Media.Set(S("b", current: true));

        Press(rig.Model.NextCommand);
        await Task.Delay(100, Ct);

        Assert.Equal(["next:b"], rig.Media.Commands);
    }

    // ---- unavailable and retry ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ErrorCode.Unsupported, "Media controls aren't available on this PC.")]
    [InlineData(ErrorCode.AccessDenied, "Windows didn't allow access to what's playing.")]
    [InlineData(ErrorCode.Timeout, "Couldn't read what's playing.")]
    [InlineData(ErrorCode.Unexpected, "Couldn't read what's playing.")]
    public async Task AFailedReadShowsItsReasonAndRetry(ErrorCode code, string expected)
    {
        using var rig = new Rig();
        rig.Media.ReadResult = Result.Failure<IReadOnlyList<MediaSessionSnapshot>>(new OperationError(code, "x", retryable: true));

        await rig.Model.InitializeAsync(Ct);

        Assert.Equal(MediaPageMode.Unavailable, rig.Model.Mode);
        Assert.Equal(expected, rig.Model.UnavailableText);
        Assert.True(rig.Model.RetryCommand.CanExecute(null));
    }

    [Fact]
    public async Task RetryReadsAgainAndShowsTheSessionWhenItWorks()
    {
        using var rig = new Rig();
        rig.Media.ReadResult = Result.Failure<IReadOnlyList<MediaSessionSnapshot>>(new OperationError(ErrorCode.Timeout, "x", retryable: true));
        await rig.Model.InitializeAsync(Ct);
        rig.Media.ReadResult = null;
        rig.Media.Set(S(title: "Found"));
        Assert.True(rig.Model.HasSession); // the tracker was listening all along

        // A fresh rig shows the same through Retry alone.
        using var second = new Rig();
        second.Media.ReadResult = Result.Failure<IReadOnlyList<MediaSessionSnapshot>>(new OperationError(ErrorCode.Timeout, "x", retryable: true));
        await second.Model.InitializeAsync(Ct);
        second.Media.ReadResult = Result.Success<IReadOnlyList<MediaSessionSnapshot>>([S(title: "Retried")]);

        Press(second.Model.RetryCommand);
        var end = DateTime.UtcNow.AddSeconds(3);
        while (!second.Model.HasSession && DateTime.UtcNow < end)
            await Task.Delay(5, Ct);

        Assert.Equal("Retried", second.Model.Title);
        Assert.Null(second.Model.UnavailableText);
    }

    [Fact]
    public void RetryIsOnlyOfferedWhileUnavailable()
    {
        using var rig = new Rig().Started(S());

        Assert.False(rig.Model.RetryCommand.CanExecute(null));
    }

    // ---- position ----------------------------------------------------------------------------------------------------

    [Fact]
    public void NoTimerRunsUntilTheTickerIsStarted()
    {
        using var rig = new Rig().Started(S(status: MediaPlaybackStatus.Playing));

        Assert.Null(rig.Timer.Pending);
        Assert.Equal(0, rig.Timer.Schedules);
    }

    [Fact]
    public void WhilePlayingThePositionMovesAlongEverySecond()
    {
        using var rig = new Rig().Started(S(status: MediaPlaybackStatus.Playing, positionSeconds: 30));

        rig.Model.StartTicker();
        Assert.Equal(TimeSpan.FromSeconds(1), rig.Timer.Pending);
        Assert.Equal("00:30", rig.Model.PositionText);

        rig.Time.Now += TimeSpan.FromSeconds(1);
        rig.Timer.Fire();
        Assert.Equal("00:31", rig.Model.PositionText);
        Assert.Equal(TimeSpan.FromSeconds(1), rig.Timer.Pending);

        rig.Time.Now += TimeSpan.FromSeconds(10); // a late tick corrects itself from the real time
        rig.Timer.Fire();
        Assert.Equal("00:41", rig.Model.PositionText);
    }

    [Fact]
    public void ThePositionNeverPassesTheEnd()
    {
        using var rig = new Rig().Started(S(status: MediaPlaybackStatus.Playing, positionSeconds: 195, durationSeconds: 200));
        rig.Model.StartTicker();

        rig.Time.Now += TimeSpan.FromSeconds(60);
        rig.Timer.Fire();

        Assert.Equal("03:20", rig.Model.PositionText);
        Assert.Equal(1.0, rig.Model.Progress);
    }

    [Fact]
    public void APausedOrStoppedSessionHoldsItsPositionAndRunsNoTimer()
    {
        foreach (var status in new[] { MediaPlaybackStatus.Paused, MediaPlaybackStatus.Stopped, MediaPlaybackStatus.Unknown })
        {
            using var rig = new Rig().Started(S(status: status, positionSeconds: 30));

            rig.Model.StartTicker();
            rig.Time.Now += TimeSpan.FromSeconds(20);
            rig.Model.StartTicker();

            Assert.Null(rig.Timer.Pending);
            Assert.Equal("00:30", rig.Model.PositionText);
        }
    }

    [Fact]
    public void PressingPlayStartsTheMovementAndPausingStopsIt()
    {
        using var rig = new Rig().Started(S(status: MediaPlaybackStatus.Paused, positionSeconds: 30));
        rig.Model.StartTicker();
        Assert.Null(rig.Timer.Pending);

        rig.Media.Set(S(status: MediaPlaybackStatus.Playing, positionSeconds: 30));
        Assert.NotNull(rig.Timer.Pending);

        rig.Media.Set(S(status: MediaPlaybackStatus.Paused, positionSeconds: 42));
        Assert.Null(rig.Timer.Pending);
        Assert.Equal("00:42", rig.Model.PositionText);
    }

    [Fact]
    public void ALiveStreamHasNoPositionToMove()
    {
        using var rig = new Rig().Started(S(status: MediaPlaybackStatus.Playing, durationSeconds: null, positionSeconds: null));

        rig.Model.StartTicker();

        Assert.Null(rig.Timer.Pending);
    }

    [Fact]
    public void StoppingTheTickerStopsTheTimerAndStartingRefreshesAgain()
    {
        using var rig = new Rig().Started(S(status: MediaPlaybackStatus.Playing, positionSeconds: 30));
        rig.Model.StartTicker();

        rig.Model.StopTicker();
        rig.Time.Now += TimeSpan.FromSeconds(50);
        Assert.Null(rig.Timer.Pending);
        Assert.Equal("00:30", rig.Model.PositionText);

        rig.Model.StartTicker();
        Assert.Equal("01:20", rig.Model.PositionText);
        Assert.NotNull(rig.Timer.Pending);
    }

    [Fact]
    public void StartingTwiceSchedulesOnce()
    {
        using var rig = new Rig().Started(S(status: MediaPlaybackStatus.Playing));

        rig.Model.StartTicker();
        rig.Model.StartTicker();

        Assert.Equal(1, rig.Timer.Schedules);
    }

    // ---- island lifetime ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheTickerFollowsTheIslandOpeningAndClosing()
    {
        using var rig = new Rig().Started(S(status: MediaPlaybackStatus.Playing));
        var island = new IslandViewModel();
        using var lifetime = new MediaLifetime(island, rig.Model);
        Assert.Null(rig.Timer.Pending); // the pill: nothing runs

        island.Toggle();
        island.CompleteExpansion();
        Assert.NotNull(rig.Timer.Pending);

        island.Toggle();
        island.CompleteCollapse();
        Assert.Null(rig.Timer.Pending);
    }

    [Fact]
    public void AnAlreadyOpenIslandStartsTheTickerAtOnceAndLettingGoStopsIt()
    {
        using var rig = new Rig().Started(S(status: MediaPlaybackStatus.Playing));
        var island = new IslandViewModel();
        island.Toggle();
        island.CompleteExpansion();
        var lifetime = new MediaLifetime(island, rig.Model);
        Assert.NotNull(rig.Timer.Pending);

        lifetime.Dispose();
        lifetime.Dispose();

        Assert.Null(rig.Timer.Pending);
        island.Toggle();
        island.CompleteCollapse();
        island.Toggle();
        island.CompleteExpansion();
        Assert.Null(rig.Timer.Pending); // let go: later changes are ignored
    }

    [Fact]
    public void MediaLifetimeRejectsMissingParts()
    {
        using var rig = new Rig();

        Assert.Throws<ArgumentNullException>(() => new MediaLifetime(null!, rig.Model));
        Assert.Throws<ArgumentNullException>(() => new MediaLifetime(new IslandViewModel(), null!));
    }

    // ---- artwork -----------------------------------------------------------------------------------------------------

    private static byte[] Png()
    {
        var pixels = new byte[4 * 4 * 4];
        Array.Fill(pixels, (byte)200);
        var bitmap = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, pixels, 16);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                StaDispatcher.ShutDownCurrent();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new InvalidOperationException("WPF media page check failed.", failure);
    }

    [Fact]
    public void ArtworkIsDecodedOnceAndKeptWhileTheBytesStay() => RunOnSta(() =>
    {
        using var rig = new Rig().Started(S(artwork: Png()));
        Assert.True(rig.Model.HasArtwork);
        var first = rig.Model.Artwork;
        Assert.True(first!.IsFrozen);

        rig.Media.Set(S(artwork: Png(), status: MediaPlaybackStatus.Playing, positionSeconds: 31));

        Assert.Same(first, rig.Model.Artwork); // the same bytes: no decoding again
    });

    [Fact]
    public void NewArtworkReplacesTheOldAndNoArtworkClearsIt() => RunOnSta(() =>
    {
        using var rig = new Rig().Started(S(artwork: Png()));
        var first = rig.Model.Artwork;

        rig.Media.Set(S(artwork: [.. Png(), 0]));
        Assert.NotSame(first, rig.Model.Artwork);

        rig.Media.Set(S(artwork: []));
        Assert.False(rig.Model.HasArtwork);
        Assert.Null(rig.Model.Artwork);
    });

    [Fact]
    public void ArtworkThatIsNotAnImageIsNoArtworkNotAnError() => RunOnSta(() =>
    {
        using var rig = new Rig().Started(S(artwork: [1, 2, 3, 4, 5]));

        Assert.False(rig.Model.HasArtwork);
        Assert.Null(rig.Model.ErrorMessage);
        Assert.Equal(MediaPageMode.Session, rig.Model.Mode);
    });

    [Fact]
    public void NoStaleArtworkRemainsWhenTheSessionChangesToOneWithout() => RunOnSta(() =>
    {
        using var rig = new Rig().Started(S("a", artwork: Png(), current: true), S("b", current: false));
        Assert.True(rig.Model.HasArtwork);

        rig.Media.Set(S("a", current: false, artwork: Png()), S("b", current: true));

        Assert.Equal("b", rig.Model.Active!.Id);
        Assert.False(rig.Model.HasArtwork);
    });

    [Fact]
    public void ArtworkDecoderTurnsEmptyAndHostileBytesIntoNothing() => RunOnSta(() =>
    {
        Assert.Null(ArtworkDecoder.Decode(ReadOnlyMemory<byte>.Empty));
        Assert.Null(ArtworkDecoder.Decode(new byte[2048]));
        Assert.Null(ArtworkDecoder.Decode(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 })); // a PNG signature and nothing else
        Assert.NotNull(ArtworkDecoder.Decode(Png()));
    });

    // ---- lifetime ----------------------------------------------------------------------------------------------------

    [Fact]
    public void DisposalStopsListeningAndReleasesTheTimer()
    {
        var rig = new Rig().Started(S(status: MediaPlaybackStatus.Playing));
        rig.Model.StartTicker();

        rig.Model.Dispose();
        rig.Model.Dispose();
        rig.Media.Set(S(title: "After"));

        Assert.True(rig.Timer.Disposed);
        Assert.Equal("Song", rig.Model.Title);
        rig.Tracker.Dispose();
    }

    [Fact]
    public async Task InitializingTwiceOrAfterDisposalDoesNothing()
    {
        var rig = new Rig();
        rig.Media.Set(S());
        await rig.Model.InitializeAsync(Ct);
        await rig.Model.InitializeAsync(Ct);
        rig.Model.Dispose();
        await rig.Model.InitializeAsync(Ct);

        Assert.Empty(rig.Media.Commands);
        rig.Tracker.Dispose();
    }

    [Fact]
    public void MissingPartsAreRejected()
    {
        var media = new ScriptedMedia();
        var tracker = new MediaSessionTracker(media);
        var transport = new MediaTransport(media);

        Assert.Throws<ArgumentNullException>(() => new MediaPageViewModel(null!, transport, w => w()));
        Assert.Throws<ArgumentNullException>(() => new MediaPageViewModel(tracker, null!, w => w()));
        Assert.Throws<ArgumentNullException>(() => new MediaPageViewModel(tracker, transport, null!));
        tracker.Dispose();
    }
}
