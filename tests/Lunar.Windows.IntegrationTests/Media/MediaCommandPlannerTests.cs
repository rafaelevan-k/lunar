using Lunar.Application.Media.Models;
using Lunar.Windows.Media;
using Xunit;

namespace Lunar.Windows.IntegrationTests.Media;

public sealed class MediaCommandPlannerTests
{
    private static MediaSessionReading Reading(
        GsmtcStatus status,
        bool play = false,
        bool pause = false,
        bool toggle = false,
        bool next = false,
        bool previous = false) =>
        new("id", "App", "Song", "Artist", status, TimeSpan.Zero, TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(10), null, 1.0,
            play, pause, next, previous, CanSeek: true, IsCurrent: true, [], toggle);

    private static string Plan(MediaSessionReading reading, MediaCommand command) =>
        MediaCommandPlanner.Plan(reading, command)?.ToString() ?? "none";

    [Theory]
    [InlineData("Playing", true, true, false, "Pause")]
    [InlineData("Playing", false, true, false, "Pause")]
    [InlineData("Playing", true, false, false, "none")]
    [InlineData("Playing", false, false, true, "Toggle")]
    [InlineData("Playing", true, true, true, "Pause")]
    [InlineData("Playing", false, false, false, "none")]
    [InlineData("Paused", true, true, false, "Play")]
    [InlineData("Paused", true, false, false, "Play")]
    [InlineData("Paused", false, true, false, "none")]
    [InlineData("Paused", false, false, true, "Toggle")]
    [InlineData("Stopped", true, false, false, "Play")]
    [InlineData("Closed", true, false, false, "Play")]
    [InlineData("Opened", false, false, true, "Toggle")]
    [InlineData("Changing", true, true, true, "none")]
    public void PlayPauseUsesWhatThePlayerOffersForItsState(string status, bool play, bool pause, bool toggle, string expected) =>
        Assert.Equal(expected, Plan(Reading(Enum.Parse<GsmtcStatus>(status), play, pause, toggle), MediaCommand.PlayPause));

    [Theory]
    [InlineData(true, "Next")]
    [InlineData(false, "none")]
    public void NextIsSentOnlyWhenAdvertised(bool next, string expected) =>
        Assert.Equal(expected, Plan(Reading(GsmtcStatus.Playing, next: next), MediaCommand.Next));

    [Theory]
    [InlineData(true, "Previous")]
    [InlineData(false, "none")]
    public void PreviousIsSentOnlyWhenAdvertised(bool previous, string expected) =>
        Assert.Equal(expected, Plan(Reading(GsmtcStatus.Paused, previous: previous), MediaCommand.Previous));

    [Fact]
    public void ANextOrPreviousIsNotAffectedByPlayState()
    {
        foreach (var status in Enum.GetValues<GsmtcStatus>())
        {
            Assert.Equal("Next", Plan(Reading(status, next: true), MediaCommand.Next));
            Assert.Equal("Previous", Plan(Reading(status, previous: true), MediaCommand.Previous));
        }
    }

    [Fact]
    public void AnUnknownCommandIsNeverPlanned() =>
        Assert.Equal("none", Plan(Reading(GsmtcStatus.Playing, true, true, true, true, true), (MediaCommand)99));

    [Fact]
    public void ATogglePlayerCountsAsOfferingPlayPauseInTheSnapshot()
    {
        var snapshot = MediaSnapshotMapper.ToSnapshot(Reading(GsmtcStatus.Paused, toggle: true), DateTimeOffset.UnixEpoch);

        Assert.True(snapshot.Capabilities.CanPlayPause);
        Assert.False(snapshot.Capabilities.CanNext);
    }

    [Fact]
    public void ANullSessionIsRejected() =>
        Assert.Throws<ArgumentNullException>(() => MediaCommandPlanner.Plan(null!, MediaCommand.Next));
}
