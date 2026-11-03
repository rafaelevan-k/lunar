using System.Globalization;

namespace Lunar.Application.System;

/// <summary>
/// Pure date and time rules for the clock (BE-14): the user's 12 or 24 hour preference, the wait until the next minute,
/// date rollover, and formatting in the user's regional format. No clock or OS access; everything takes its inputs, so
/// each rule is tested with fixed values.
/// </summary>
public static class ClockFormat
{
    /// <summary>
    /// True when the regional format shows a 24 hour clock, that is, its short time pattern uses the hour symbol "H" (and
    /// not "h"). Quoted text and escaped characters in the pattern are ignored.
    /// </summary>
    public static bool Uses24HourClock(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var pattern = culture.DateTimeFormat.ShortTimePattern;
        var inQuote = '\0';
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (inQuote != '\0')
            {
                if (c == inQuote)
                    inQuote = '\0';
                continue;
            }

            switch (c)
            {
                case '\'' or '"':
                    inQuote = c;
                    break;
                case '\\':
                    i++; // the next character is literal
                    break;
                case 'H':
                    return true;
                case 'h':
                    return false;
            }
        }

        return false;
    }

    /// <summary>How long until the clock shows the next minute; between 1 tick and one minute, never zero.</summary>
    public static TimeSpan UntilNextMinute(DateTimeOffset now) =>
        TimeSpan.FromTicks(TimeSpan.TicksPerMinute - (now.Ticks % TimeSpan.TicksPerMinute));

    /// <summary>True when the two moments fall on different calendar dates on the clock the user sees.</summary>
    public static bool IsNewDay(DateTimeOffset previous, DateTimeOffset current) =>
        previous.Date != current.Date;

    /// <summary>The time of day in the regional format (for example "3:07 PM" or "15:07"), optionally with seconds.</summary>
    public static string FormatTime(DateTimeOffset local, CultureInfo culture, bool withSeconds = false)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var pattern = withSeconds ? culture.DateTimeFormat.LongTimePattern : culture.DateTimeFormat.ShortTimePattern;
        return local.ToString(pattern, culture);
    }

    /// <summary>The full date in the regional format (for example "Wednesday, November 4, 2026").</summary>
    public static string FormatDate(DateTimeOffset local, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return local.ToString(culture.DateTimeFormat.LongDatePattern, culture);
    }
}
