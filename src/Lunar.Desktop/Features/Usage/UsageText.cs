using System.Globalization;
using Lunar.Application.Usage.Models;

namespace Lunar.Desktop.Features.Usage;

/// <summary>
/// The words of the Usage page (FE-27), as pure functions of a reading, the time and a culture, so every case is tested without a window.
/// Nothing here invents a figure: a limit the service did not report reads as "not reported".
/// </summary>
public static class UsageText
{
    /// <summary>"67% used", or "No figure reported" when the service gave none.</summary>
    public static string Percent(UsageWindow? window) =>
        window?.UsedPercent is { } percent ? $"{percent}% used" : "No figure reported";

    /// <summary>A length of time in the two most useful units: "under a minute", "14 min", "2 h 5 min", "3 d 2 h".</summary>
    public static string Span(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1))
            return "under a minute";
        if (span < TimeSpan.FromHours(1))
            return $"{(int)span.TotalMinutes} min";
        if (span < TimeSpan.FromDays(1))
        {
            var minutes = span.Minutes;
            return minutes == 0 ? $"{(int)span.TotalHours} h" : $"{(int)span.TotalHours} h {minutes} min";
        }

        var hours = span.Hours;
        return hours == 0 ? $"{(int)span.TotalDays} d" : $"{(int)span.TotalDays} d {hours} h";
    }

    /// <summary>
    /// When the limit resets: "Resets in 2 h 5 min (14:30)", with the weekday for a reset a day or more away. A reset time that has already
    /// passed says so (the next reading will show the new period); a missing one says it was not reported.
    /// </summary>
    public static string Reset(UsageWindow? window, DateTimeOffset now, CultureInfo culture, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(culture);
        if (window?.ResetsAt is not { } resetsAt)
            return "Reset time not reported";

        var remaining = resetsAt - now;
        if (remaining <= TimeSpan.Zero)
            return "Reset time has passed; waiting for the next reading";

        var local = TimeZoneInfo.ConvertTime(resetsAt, zone ?? TimeZoneInfo.Local);
        var clock = remaining < TimeSpan.FromDays(1)
            ? local.ToString("t", culture)
            : local.ToString("ddd", culture) + " " + local.ToString("t", culture);
        return $"Resets in {Span(remaining)} ({clock})";
    }

    /// <summary>When the shown reading was taken: "Updated 14:02".</summary>
    public static string Updated(DateTimeOffset fetchedAt, CultureInfo culture, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return "Updated " + TimeZoneInfo.ConvertTime(fetchedAt, zone ?? TimeZoneInfo.Local).ToString("t", culture);
    }

    /// <summary>The name a screen reader reads for one limit.</summary>
    public static string Spoken(string title, UsageWindow? window, DateTimeOffset now, CultureInfo culture, TimeZoneInfo? zone = null) =>
        $"{title}: {Percent(window)}. {Reset(window, now, culture, zone)}";
}
