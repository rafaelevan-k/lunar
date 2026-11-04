using Lunar.Application.Media.Models;
using Lunar.Windows.Media;
using Xunit;

namespace Lunar.Windows.IntegrationTests.Media;

public sealed class MediaSnapshotMapperTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private static MediaSessionReading Reading(
        GsmtcStatus status = GsmtcStatus.Playing,
        int startSeconds = 0,
        int endSeconds = 200,
        int positionSeconds = 30,
        DateTimeOffset? stamp = null,
        double rate = 1.0,
        bool play = true,
        bool pause = true,
        bool next = false,
        bool previous = false,
        bool seek = true,
        string? title = "Song") =>
        new("id", "App", title, "Artist", status, TimeSpan.FromSeconds(startSeconds), TimeSpan.FromSeconds(endSeconds),
            TimeSpan.FromSeconds(positionSeconds), stamp ?? Stamp, rate, play, pause, next, previous, seek, IsCurrent: true, []);

    private static MediaSessionSnapshot Map(MediaSessionReading reading, int secondsAfterStamp = 0) =>
        MediaSnapshotMapper.ToSnapshot(reading, Stamp.AddSeconds(secondsAfterStamp));

    // ---- status ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Playing", MediaPlaybackStatus.Playing)]
    [InlineData("Paused", MediaPlaybackStatus.Paused)]
    [InlineData("Stopped", MediaPlaybackStatus.Stopped)]
    [InlineData("Closed", MediaPlaybackStatus.Stopped)]
    [InlineData("Opened", MediaPlaybackStatus.Stopped)]
    [InlineData("Changing", MediaPlaybackStatus.Unknown)]
    public void PlaybackStatesAreTranslated(string native, MediaPlaybackStatus expected) =>
        Assert.Equal(expected, MediaSnapshotMapper.StatusOf(Enum.Parse<GsmtcStatus>(native)));

    // ---- duration and position ---------------------------------------------------------------------------------------

    [Fact]
    public void ANormalTimelineHasADurationAndAPosition()
    {
        var snapshot = Map(Reading(positionSeconds: 30));

        Assert.Equal(TimeSpan.FromSeconds(200), snapshot.Duration);
        Assert.Equal(TimeSpan.FromSeconds(30), snapshot.Position);
        Assert.True(snapshot.CanSeek);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    [InlineData(100, 40)]
    public void ATimelineThatEndsAtOrBeforeItsStartHasNoDuration(int start, int end)
    {
        var snapshot = Map(Reading(startSeconds: start, endSeconds: end));

        Assert.Null(snapshot.Duration);
        Assert.Null(snapshot.Position);
        Assert.False(snapshot.CanSeek);
    }

    [Fact]
    public void AnUnboundedSentinelEndCountsAsNoDuration()
    {
        var reading = Reading() with { End = TimeSpan.FromDays(3650) };

        var snapshot = Map(reading);

        Assert.Null(snapshot.Duration);
        Assert.False(snapshot.CanSeek);
    }

    [Fact]
    public void ATimelineThatStartsLaterIsCountedFromItsStart()
    {
        var snapshot = Map(Reading(startSeconds: 60, endSeconds: 260, positionSeconds: 90));

        Assert.Equal(TimeSpan.FromSeconds(200), snapshot.Duration);
        Assert.Equal(TimeSpan.FromSeconds(30), snapshot.Position);
    }

    [Fact]
    public void WhilePlayingThePositionMovesForwardWithTimeSinceTheStamp() =>
        Assert.Equal(TimeSpan.FromSeconds(42), Map(Reading(positionSeconds: 30), secondsAfterStamp: 12).Position);

    [Fact]
    public void ThePositionMovesAtThePlaybackRate() =>
        Assert.Equal(TimeSpan.FromSeconds(54), Map(Reading(positionSeconds: 30, rate: 2.0), secondsAfterStamp: 12).Position);

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(100.0)]
    [InlineData(double.NaN)]
    public void ANonsenseRateCountsAsNormalSpeed(double rate) =>
        Assert.Equal(TimeSpan.FromSeconds(42), Map(Reading(positionSeconds: 30, rate: rate), secondsAfterStamp: 12).Position);

    [Theory]
    [InlineData("Paused")]
    [InlineData("Stopped")]
    [InlineData("Changing")]
    public void WhenNotPlayingThePositionStaysWhereWindowsPutIt(string status) =>
        Assert.Equal(TimeSpan.FromSeconds(30), Map(Reading(Enum.Parse<GsmtcStatus>(status), positionSeconds: 30), secondsAfterStamp: 120).Position);

    [Fact]
    public void ThePositionNeverPassesTheEnd() =>
        Assert.Equal(TimeSpan.FromSeconds(200), Map(Reading(positionSeconds: 190), secondsAfterStamp: 600).Position);

    [Fact]
    public void ThePositionNeverGoesBeforeTheStart() =>
        Assert.Equal(TimeSpan.Zero, Map(Reading(startSeconds: 50, endSeconds: 250, positionSeconds: 10)).Position);

    [Fact]
    public void WithoutAStampThePositionIsNotMoved() =>
        Assert.Equal(TimeSpan.FromSeconds(30), MediaSnapshotMapper.ToSnapshot(Reading(positionSeconds: 30) with { PositionStamp = null }, Stamp.AddSeconds(50)).Position);

    [Fact]
    public void AStampInTheFutureDoesNotMoveThePositionBackwards() =>
        Assert.Equal(TimeSpan.FromSeconds(30), Map(Reading(positionSeconds: 30), secondsAfterStamp: -5).Position);

    // ---- capabilities ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public void PlayPauseIsOfferedWhenEitherIsAdvertised(bool play, bool pause, bool expected) =>
        Assert.Equal(expected, Map(Reading(play: play, pause: pause)).Capabilities.CanPlayPause);

    [Fact]
    public void NextPreviousAndSeekFollowWhatTheAppAdvertises()
    {
        var capabilities = Map(Reading(next: true, previous: false, seek: true)).Capabilities;

        Assert.True(capabilities.CanNext);
        Assert.False(capabilities.CanPrevious);
        Assert.True(capabilities.CanSeek);
    }

    [Fact]
    public void SeekingNeedsBothTheCapabilityAndAFiniteDuration()
    {
        Assert.False(Map(Reading(seek: false)).CanSeek);
        Assert.False(Map(Reading(seek: true, endSeconds: 0)).CanSeek);
        Assert.True(Map(Reading(seek: true)).CanSeek);
    }

    // ---- text and artwork --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyTextIsAbsent(string? title) => Assert.Null(Map(Reading(title: title)).Title);

    [Fact]
    public void TextIsTrimmedAndLongTextIsCut()
    {
        Assert.Equal("Song", Map(Reading(title: "  Song  ")).Title);
        Assert.Equal(MediaSnapshotMapper.MaxTextLength, Map(Reading(title: new string('x', 10_000))).Title!.Length);
    }

    [Fact]
    public void ArtworkWithinTheLimitIsKeptAndEmptyArtworkIsEmpty()
    {
        var withArt = MediaSnapshotMapper.ToSnapshot(Reading() with { Artwork = [1, 2, 3] }, Stamp);
        var without = MediaSnapshotMapper.ToSnapshot(Reading(), Stamp);

        Assert.Equal<byte>([1, 2, 3], withArt.Artwork.ToArray());
        Assert.True(without.Artwork.IsEmpty);
    }

    [Fact]
    public void ArtworkOverTheLimitIsDropped()
    {
        var snapshot = MediaSnapshotMapper.ToSnapshot(Reading() with { Artwork = new byte[MediaSessionSnapshot.MaxArtworkBytes + 1] }, Stamp);

        Assert.True(snapshot.Artwork.IsEmpty);
    }

    // ---- app names ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Spotify.exe", "Spotify")]
    [InlineData("chrome.exe", "chrome")]
    [InlineData("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", "Microsoft.ZuneMusic")]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "SpotifyAB.SpotifyMusic")]
    [InlineData("  Firefox  ", "Firefox")]
    public void AppIdsBecomeReadableNames(string id, string expected) =>
        Assert.Equal(expected, MediaSnapshotMapper.FriendlyAppName(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".exe")]
    public void NothingUsableGivesNoName(string? id) => Assert.Null(MediaSnapshotMapper.FriendlyAppName(id));

    // ---- reading equality --------------------------------------------------------------------------------------------

    [Fact]
    public void ReadingsAreEqualByContentIncludingArtworkBytes()
    {
        var a = Reading() with { Artwork = [1, 2, 3] };
        var b = Reading() with { Artwork = [1, 2, 3] };
        var c = Reading() with { Artwork = [1, 2, 4] };

        Assert.True(a.SameAs(b));
        Assert.False(a.SameAs(c));
        Assert.False(a.SameAs(null));
        Assert.False(a.SameAs(Reading(positionSeconds: 31)));
        Assert.False(a.SameAs(Reading(status: GsmtcStatus.Paused)));
    }
}
