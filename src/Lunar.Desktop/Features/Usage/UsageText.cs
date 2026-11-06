using System.Globalization;
using Lunar.Application.Usage.Models;
using Lunar.Desktop.Resources;

namespace Lunar.Desktop.Features.Usage;

/// <summary>
/// The words of the Usage page (FE-27), as pure functions of a reading, the time and a culture, so every case is tested without a window.
/// Nothing here invents a figure: a limit the service did not report reads as "not reported".
/// </summary>
public static class UsageText
{
    /// <summary>"67% used", or "No figure reported" when the service gave none.</summary>
    public static string Percent(UsageWindow? window) =>
        window?.UsedPercent is { } percent ? Strings.Format("Usage.Percent", percent) : Strings.Get("Usage.NoFigure");

    /// <summary>A length of time in the two most useful units: "under a minute", "14 min", "2 h 5 min", "3 d 2 h".</summary>
    public static string Span(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1))
            return Strings.Get("Usage.SpanUnderMinute");
        if (span < TimeSpan.FromHours(1))
            return Strings.Format("Usage.SpanMin", (int)span.TotalMinutes);
        if (span < TimeSpan.FromDays(1))
        {
            var minutes = span.Minutes;
            return minutes == 0 ? Strings.Format("Usage.SpanHour", (int)span.TotalHours) : Strings.Format("Usage.SpanHourMin", (int)span.TotalHours, minutes);
        }

        var hours = span.Hours;
        return hours == 0 ? Strings.Format("Usage.SpanDay", (int)span.TotalDays) : Strings.Format("Usage.SpanDayHour", (int)span.TotalDays, hours);
    }

    /// <summary>
    /// When the limit resets: "Resets in 2 h 5 min (14:30)", with the weekday for a reset a day or more away. A reset time that has already
    /// passed says so (the next reading will show the new period); a missing one says it was not reported.
    /// </summary>
    public static string Reset(UsageWindow? window, DateTimeOffset now, CultureInfo culture, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(culture);
        if (window?.ResetsAt is not { } resetsAt)
            return Strings.Get("Usage.ResetNotReported");

        var remaining = resetsAt - now;
        if (remaining <= TimeSpan.Zero)
            return Strings.Get("Usage.ResetPassed");

        var local = TimeZoneInfo.ConvertTime(resetsAt, zone ?? TimeZoneInfo.Local);
        var clock = remaining < TimeSpan.FromDays(1)
            ? local.ToString("t", culture)
            : local.ToString("ddd", culture) + " " + local.ToString("t", culture);
        return Strings.Format("Usage.ResetIn", Span(remaining), clock);
    }

    /// <summary>When the shown reading was taken: "Updated 14:02".</summary>
    public static string Updated(DateTimeOffset fetchedAt, CultureInfo culture, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return Strings.Format("Usage.Updated", TimeZoneInfo.ConvertTime(fetchedAt, zone ?? TimeZoneInfo.Local).ToString("t", culture));
    }

    /// <summary>The name a screen reader reads for one limit.</summary>
    public static string Spoken(string title, UsageWindow? window, DateTimeOffset now, CultureInfo culture, TimeZoneInfo? zone = null) =>
        Strings.Format("Usage.Spoken", title, Percent(window), Reset(window, now, culture, zone));
}
