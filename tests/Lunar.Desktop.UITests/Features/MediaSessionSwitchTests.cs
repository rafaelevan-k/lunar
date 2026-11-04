using System.Windows.Media.Imaging;
using Lunar.Application.Abstractions.Media;
using Lunar.Application.Media.Models;
using Lunar.Application.Media.UseCases;
using Lunar.Application.Tests.Fakes;
using Lunar.Desktop.Features.MediaControls;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Desktop.UITests.Features;

// FE-19: several players. The page shows which one is active, offers a way to switch (only with more than one), and when the
// active player changes nothing of the old one stays: artwork, text, buttons, timeline and errors all follow the new one.
public sealed class MediaSessionSwitchTests
{
    private sealed class Rig : IDisposable
    {
        public FakeMediaService Media { get; } = new();

        public MediaSessionTracker Tracker { get; }

        public MediaPageViewModel Model { get; }

        public Rig(params MediaSessionSnapshot[] sessions)
        {
            Media.SetSessions(sessions);
            Tracker = new MediaSessionTracker(Media);
            Model = new MediaPageViewModel(Tracker, new MediaTransport(Media), new SeekMedia(Media), work => work());
            Model.InitializeAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            Model.Dispose();
            Tracker.Dispose();
        }
    }

    private static MediaSessionSnapshot S(
        string id,
        string? app = "Player",
        string? title = "Song",
        bool current = false,
        MediaPlaybackStatus status = MediaPlaybackStatus.Paused,
        bool next = true,
        int durationSeconds = 200,
        byte[]? artwork = null) =>
        new(id, app, title, "Artist", status, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(durationSeconds),
            new MediaCapabilities(true, next, true, true), current, artwork ?? []);

    private static byte[] Png()
    {
        var bitmap = new WriteableBitmap(2, 2, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, 2, 2), new byte[16], 8, 0);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    // ---- the selector ------------------------------------------------------------------------------------------------

    [Fact]
    public void WithOnePlayerThereIsNothingToSwitch()
    {
        using var rig = new Rig(S("a"));

        Assert.False(rig.Model.HasMultipleSessions);
        Assert.False(rig.Model.SwitchSessionCommand.CanExecute(null));
        Assert.Equal(string.Empty, rig.Model.SessionPositionText);
        Assert.False(rig.Model.ShowAutomatic);
    }

    [Fact]
    public void WithSeveralPlayersThePageSaysWhichOneIsShown()
    {
        using var rig = new Rig(S("a", app: "Spotify"), S("b", app: "Browser", current: true));

        Assert.True(rig.Model.HasMultipleSessions);
        Assert.Equal(2, rig.Model.SessionCount);
        Assert.Equal("Browser", rig.Model.Source);
        Assert.Equal("2 of 2", rig.Model.SessionPositionText); // ordered by id: a, b
        Assert.Equal("Switch to Spotify", rig.Model.SwitchLabel);
        Assert.True(rig.Model.SwitchSessionCommand.CanExecute(null));
        Assert.False(rig.Model.ShowAutomatic); // the page chose, not the user
    }

    [Fact]
    public void SwitchingShowsTheNextPlayerAndKeepsTheChoice()
    {
        using var rig = new Rig(S("a", app: "Spotify", title: "One"), S("b", app: "Browser", title: "Two", current: true));

        rig.Model.SwitchSessionCommand.Execute(null);

        Assert.Equal("a", rig.Model.Active!.Id);
        Assert.Equal("One", rig.Model.Title);
        Assert.Equal("Spotify", rig.Model.Source);
        Assert.Equal("1 of 2", rig.Model.SessionPositionText);
        Assert.Equal("Switch to Browser", rig.Model.SwitchLabel);
        Assert.True(rig.Model.ShowAutomatic);

        // Another list from Windows (same players) does not undo the pick.
        rig.Media.SetSessions(S("a", app: "Spotify", title: "One"), S("b", app: "Browser", title: "Two", current: true));
        Assert.Equal("a", rig.Model.Active!.Id);
    }

    [Fact]
    public void SwitchingThroughAllPlayersComesBackToTheFirst()
    {
        using var rig = new Rig(S("a"), S("b"), S("c"));

        var seen = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            seen.Add(rig.Model.Active!.Id);
            rig.Model.SwitchSessionCommand.Execute(null);
        }

        Assert.Equal(["a", "b", "c", "a"], seen);
    }

    [Fact]
    public void GivingTheChoiceBackReturnsToTheAutomaticPlayer()
    {
        using var rig = new Rig(S("a"), S("b", current: true));
        rig.Model.SwitchSessionCommand.Execute(null);
        Assert.Equal("a", rig.Model.Active!.Id);

        Assert.True(rig.Model.UseAutomaticCommand.CanExecute(null));
        rig.Model.UseAutomaticCommand.Execute(null);

        Assert.Equal("b", rig.Model.Active!.Id);
        Assert.False(rig.Model.ShowAutomatic);
        Assert.False(rig.Model.UseAutomaticCommand.CanExecute(null));
    }

    [Fact]
    public void WhenThePlayerThatWasPickedClosesTheOtherOneIsShown()
    {
        using var rig = new Rig(S("a"), S("b", current: true));
        rig.Model.SwitchSessionCommand.Execute(null);

        rig.Media.SetSessions(S("b", current: true));

        Assert.Equal("b", rig.Model.Active!.Id);
        Assert.False(rig.Model.HasMultipleSessions);
        Assert.False(rig.Model.SwitchSessionCommand.CanExecute(null));
    }

    // ---- nothing of the old player is left ---------------------------------------------------------------------------

    [Fact]
    public void SwitchingDropsTheOldPlayersArtworkAtOnce()
    {
        var png = Png();
        using var rig = new Rig(S("a", artwork: png), S("b", current: true));

        // b is shown first and has no artwork.
        Assert.False(rig.Model.HasArtwork);
        rig.Model.SwitchSessionCommand.Execute(null); // a: has artwork
        Assert.True(rig.Model.HasArtwork);
        rig.Model.SwitchSessionCommand.Execute(null); // b: none again, the picture of a must be gone

        Assert.False(rig.Model.HasArtwork);
        Assert.Null(rig.Model.Artwork);
    }

    [Fact]
    public void SwitchingMovesButtonsAndTimelineToTheNewPlayer()
    {
        using var rig = new Rig(S("a", next: true, durationSeconds: 200), S("b", next: false, durationSeconds: 3700, current: true));

        Assert.False(rig.Model.NextCommand.CanExecute(null));
        Assert.Equal("01:01:40", rig.Model.DurationText);

        rig.Model.SwitchSessionCommand.Execute(null);

        Assert.True(rig.Model.NextCommand.CanExecute(null));
        Assert.Equal("03:20", rig.Model.DurationText);
    }

    [Fact]
    public async Task AButtonPressedAfterSwitchingGoesToTheNewPlayerOnly()
    {
        using var rig = new Rig(S("a"), S("b", current: true));
        rig.Model.SwitchSessionCommand.Execute(null); // a

        rig.Model.NextCommand.Execute(null);
        var end = DateTime.UtcNow.AddSeconds(3);
        while (rig.Media.Commands.Count == 0 && DateTime.UtcNow < end)
            await Task.Delay(5, TestContext.Current.CancellationToken);

        Assert.Equal(["next"], rig.Media.Commands);
        Assert.Equal("a", rig.Model.Active!.Id);
    }

    [Fact]
    public async Task ARefusalFromTheOldPlayerThatArrivesAfterSwitchingIsNotShown()
    {
        var gate = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        var media = new SlowMedia(gate, S("a"), S("b", current: true));
        using var tracker = new MediaSessionTracker(media);
        using var model = new MediaPageViewModel(tracker, new MediaTransport(media), new SeekMedia(media), work => work());
        await model.InitializeAsync(TestContext.Current.CancellationToken);

        model.NextCommand.Execute(null); // to b, which will answer late
        await Task.Delay(30, TestContext.Current.CancellationToken);
        model.SwitchSessionCommand.Execute(null); // the page moves to a
        Assert.Equal("a", model.Active!.Id);

        gate.SetResult(Result.Failure(new OperationError(ErrorCode.Unexpected, "refused")));
        await Task.Delay(60, TestContext.Current.CancellationToken);

        Assert.Null(model.ErrorMessage);
    }

    private sealed class SlowMedia(TaskCompletionSource<Result> gate, params MediaSessionSnapshot[] sessions) : IMediaService
    {
        public IObservable<IReadOnlyList<MediaSessionSnapshot>> Changes { get; } = new TestObservable<IReadOnlyList<MediaSessionSnapshot>>();

        public Task<Result<IReadOnlyList<MediaSessionSnapshot>>> GetSessionsAsync(CancellationToken ct) =>
            Task.FromResult(Result.Success<IReadOnlyList<MediaSessionSnapshot>>(sessions));

        public Task<Result> PlayPauseAsync(string sessionId, CancellationToken ct) => Task.FromResult(Result.Success());

        public Task<Result> NextAsync(string sessionId, CancellationToken ct) => gate.Task;

        public Task<Result> PreviousAsync(string sessionId, CancellationToken ct) => Task.FromResult(Result.Success());

        public Task<Result> SeekAsync(string sessionId, TimeSpan position, CancellationToken ct) => Task.FromResult(Result.Success());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void SwitchingClearsAnErrorThatWasAboutTheOtherPlayer()
    {
        using var rig = new Rig(S("a"), S("b", current: true));
        rig.Model.DismissErrorCommand.Execute(null);

        rig.Model.SwitchSessionCommand.Execute(null);

        Assert.Null(rig.Model.ErrorMessage);
    }

    [Fact]
    public void ADragInProgressIsDroppedWhenThePlayerIsSwitched()
    {
        using var rig = new Rig(S("a"), S("b", current: true));

        rig.Model.IsScrubbing = true;
        rig.Model.SeekValue = 0.7;
        rig.Model.SwitchSessionCommand.Execute(null);
        rig.Model.IsScrubbing = false;

        Assert.Empty(rig.Media.Commands); // no seek went to either player
        Assert.Equal(0.15, rig.Model.SeekValue, 3);
    }

    [Fact]
    public void NoPlayerMeansNoSwitcher()
    {
        using var rig = new Rig(S("a"), S("b"));
        rig.Media.SetSessions();

        Assert.False(rig.Model.HasMultipleSessions);
        Assert.Equal(0, rig.Model.SessionCount);
        Assert.False(rig.Model.SwitchSessionCommand.CanExecute(null));
        Assert.Equal("Switch player", rig.Model.SwitchLabel);
    }
}
