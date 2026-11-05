using System.Globalization;
using Lunar.Application.Usage.Models;
using Lunar.Desktop.Features.Usage;
using Xunit;

namespace Lunar.Desktop.UITests.Features;

// FE-27: the words of the Usage page, as pure functions of a reading, the time and a culture.
public sealed class UsageTextTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private static UsageWindow Window(double? used, DateTimeOffset? resets) => new(UsageWindowKind.FiveHour, used, resets);

    [Theory]
    [InlineData(0.0, "0% used")]
    [InlineData(0.674, "67% used")]
    [InlineData(0.675, "68% used")]
    [InlineData(1.0, "100% used")]
    [InlineData(2.5, "100% used")] // never past full
    public void PercentIsAWholeNumberClampedToTheRange(double used, string expected) =>
        Assert.Equal(expected, UsageText.Percent(Window(used, null)));

    [Fact]
    public void AMissingFigureIsSaidNotInvented()
    {
        Assert.Equal("No figure reported", UsageText.Percent(Window(null, Now)));
        Assert.Equal("No figure reported", UsageText.Percent(null));
    }

    [Theory]
    [InlineData(0, "under a minute")]
    [InlineData(59, "under a minute")]
    [InlineData(60, "1 min")]
    [InlineData(14 * 60, "14 min")]
    [InlineData(3600, "1 h")]
    [InlineData(2 * 3600 + 5 * 60, "2 h 5 min")]
    [InlineData(86400, "1 d")]
    [InlineData(3 * 86400 + 2 * 3600, "3 d 2 h")]
    public void SpanUsesTheTwoMostUsefulUnits(int seconds, string expected) =>
        Assert.Equal(expected, UsageText.Span(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void AResetToday_ShowsTheRemainingTimeAndTheClockTime()
    {
        var text = UsageText.Reset(Window(0.5, Now.AddMinutes(125)), Now, Invariant, TimeZoneInfo.Utc);

        Assert.Equal("Resets in 2 h 5 min (14:05)", text);
    }

    [Fact]
    public void AResetADayOrMoreAway_AlsoNamesTheWeekday()
    {
        var text = UsageText.Reset(Window(0.5, Now.AddDays(3)), Now, Invariant, TimeZoneInfo.Utc);

        Assert.Equal("Resets in 3 d (Sun 12:00)", text); // 2026-11-08 is a Sunday
    }

    [Fact]
    public void TheClockTimeFollowsTheGivenTimeZone()
    {
        var plusSeven = TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(7), "test", "test");

        var text = UsageText.Reset(Window(0.5, Now.AddHours(1)), Now, Invariant, plusSeven);

        Assert.Equal("Resets in 1 h (20:00)", text); // 13:00 UTC is 20:00 at +7
    }

    [Fact]
    public void APassedResetTimeIsSaidAsSuchAndAMissingOneAsNotReported()
    {
        Assert.Equal("Reset time has passed; waiting for the next reading", UsageText.Reset(Window(0.9, Now.AddSeconds(-1)), Now, Invariant));
        Assert.Equal("Reset time has passed; waiting for the next reading", UsageText.Reset(Window(0.9, Now), Now, Invariant));
        Assert.Equal("Reset time not reported", UsageText.Reset(Window(0.9, null), Now, Invariant));
        Assert.Equal("Reset time not reported", UsageText.Reset(null, Now, Invariant));
    }

    [Fact]
    public void UpdatedNamesTheClockTimeOfTheReading() =>
        Assert.Equal("Updated 12:00", UsageText.Updated(Now, Invariant, TimeZoneInfo.Utc));

    [Fact]
    public void TheSpokenNameCarriesTheTitleThePercentAndTheReset()
    {
        var text = UsageText.Spoken("Weekly limit", Window(0.4, Now.AddHours(3)), Now, Invariant, TimeZoneInfo.Utc);

        Assert.Equal("Weekly limit: 40% used. Resets in 3 h (15:00)", text);
    }
}
