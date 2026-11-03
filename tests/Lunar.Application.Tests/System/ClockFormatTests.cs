using System.Globalization;
using Lunar.Application.System;
using Xunit;

namespace Lunar.Application.Tests.System;

public sealed class ClockFormatTests
{
    private static readonly TimeSpan Jakarta = TimeSpan.FromHours(7);

    private static CultureInfo Culture(string shortTimePattern, string name = "en-US")
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo(name).Clone();
        culture.DateTimeFormat.ShortTimePattern = shortTimePattern;
        return culture;
    }

    // ---- 12 or 24 hour clock -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("h:mm tt", false)]
    [InlineData("hh:mm tt", false)]
    [InlineData("H:mm", true)]
    [InlineData("HH:mm", true)]
    [InlineData("HH.mm", true)]
    [InlineData("tt h:mm", false)]
    [InlineData("HH:mm 'h'", true)]
    [InlineData("'at' h:mm tt", false)] // the quoted text contains no hour symbol that counts
    [InlineData("'H' h:mm tt", false)] // a quoted H is text, the real symbol is h
    [InlineData("\\H h:mm tt", false)] // an escaped H is text too
    [InlineData("H\\h mm", true)]
    [InlineData("", false)]
    [InlineData("mm", false)]
    public void TheShortTimePatternDecidesTwelveOrTwentyFourHours(string pattern, bool expected) =>
        Assert.Equal(expected, ClockFormat.Uses24HourClock(Culture(pattern)));

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", true)]
    [InlineData("id-ID", true)]
    [InlineData("fr-FR", true)]
    [InlineData("ja-JP", true)]
    public void RealRegionalFormatsAreRecognised(string name, bool expected) =>
        Assert.Equal(expected, ClockFormat.Uses24HourClock(CultureInfo.GetCultureInfo(name)));

    [Fact]
    public void TheInvariantFallbackCultureIsTwentyFourHourAndANullCultureIsRejected()
    {
        Assert.True(ClockFormat.Uses24HourClock(CultureInfo.InvariantCulture)); // its pattern is HH:mm
        Assert.Throws<ArgumentNullException>(() => ClockFormat.Uses24HourClock(null!));
    }

    // ---- minute refresh ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 0, 60_000)]
    [InlineData(1, 0, 59_000)]
    [InlineData(30, 500, 29_500)]
    [InlineData(59, 999, 1)]
    public void TheWaitForTheNextMinuteIsWhatIsLeftOfThisOne(int second, int millisecond, double expectedMilliseconds)
    {
        var now = new DateTimeOffset(2026, 11, 4, 9, 12, second, millisecond, Jakarta);

        Assert.Equal(expectedMilliseconds, ClockFormat.UntilNextMinute(now).TotalMilliseconds, 3);
    }

    [Fact]
    public void TheWaitIsNeverZeroOrMoreThanAMinuteAndLandsOnAWholeMinute()
    {
        var random = new Random(8);
        for (var i = 0; i < 2000; i++)
        {
            var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromMinutes(random.Next(-12 * 60, 14 * 60)))
                .AddTicks(random.NextInt64(0, TimeSpan.TicksPerDay));

            var wait = ClockFormat.UntilNextMinute(now);

            Assert.True(wait > TimeSpan.Zero && wait <= TimeSpan.FromMinutes(1));
            Assert.Equal(0, (now + wait).Second);
            Assert.Equal(0, (now + wait).Ticks % TimeSpan.TicksPerMinute);
        }
    }

    // ---- date rollover -----------------------------------------------------------------------------------------------

    [Fact]
    public void MidnightStartsANewDay()
    {
        var before = new DateTimeOffset(2026, 11, 4, 23, 59, 59, 999, Jakarta);

        Assert.False(ClockFormat.IsNewDay(before, before.AddMilliseconds(-5)));
        Assert.True(ClockFormat.IsNewDay(before, before.AddMilliseconds(1)));
        Assert.False(ClockFormat.IsNewDay(before.AddMilliseconds(1), before.AddMilliseconds(2)));
    }

    [Fact]
    public void ChangingTimeZoneAcrossMidnightCountsAsANewDayOnTheClockTheUserSees()
    {
        var previous = new DateTimeOffset(2026, 11, 4, 23, 30, 0, Jakarta); // 16:30 UTC
        var inTokyo = previous.ToOffset(TimeSpan.FromHours(9)); // 01:30 on the 5th

        Assert.True(ClockFormat.IsNewDay(previous, inTokyo));
        Assert.False(ClockFormat.IsNewDay(previous, previous.ToOffset(TimeSpan.FromHours(-3)))); // 13:30 on the 4th
    }

    [Fact]
    public void TheEndOfTheYearAndALeapDayRollOverCorrectly()
    {
        Assert.True(ClockFormat.IsNewDay(new DateTimeOffset(2026, 12, 31, 23, 59, 0, Jakarta), new DateTimeOffset(2027, 1, 1, 0, 0, 0, Jakarta)));
        Assert.True(ClockFormat.IsNewDay(new DateTimeOffset(2028, 2, 28, 23, 59, 0, Jakarta), new DateTimeOffset(2028, 2, 29, 0, 0, 0, Jakarta)));
        Assert.False(ClockFormat.IsNewDay(new DateTimeOffset(2028, 2, 29, 0, 0, 0, Jakarta), new DateTimeOffset(2028, 2, 29, 23, 59, 0, Jakarta)));
    }

    // ---- formatting --------------------------------------------------------------------------------------------------

    [Fact]
    public void TimeAndDateFollowTheRegionalFormat()
    {
        var time = new DateTimeOffset(2026, 11, 4, 15, 7, 9, Jakarta);
        var english = CultureInfo.GetCultureInfo("en-US");
        var german = CultureInfo.GetCultureInfo("de-DE");

        Assert.Equal("3:07 PM", ClockFormat.FormatTime(time, english));
        Assert.Equal("3:07:09 PM", ClockFormat.FormatTime(time, english, withSeconds: true));
        Assert.Equal("Wednesday, November 4, 2026", ClockFormat.FormatDate(time, english));
        Assert.Equal("15:07", ClockFormat.FormatTime(time, german));
        Assert.Equal("15:07:09", ClockFormat.FormatTime(time, german, withSeconds: true));
        Assert.Equal("Mittwoch, 4. November 2026", ClockFormat.FormatDate(time, german));
    }

    [Fact]
    public void FormattingNeverDependsOnTheThreadCulture()
    {
        var time = new DateTimeOffset(2026, 11, 4, 15, 7, 9, Jakarta);
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ja-JP");

            Assert.Equal("3:07 PM", ClockFormat.FormatTime(time, CultureInfo.GetCultureInfo("en-US")));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void FormattersRejectANullCulture()
    {
        var time = DateTimeOffset.Now;
        Assert.Throws<ArgumentNullException>(() => ClockFormat.FormatTime(time, null!));
        Assert.Throws<ArgumentNullException>(() => ClockFormat.FormatDate(time, null!));
    }
}
