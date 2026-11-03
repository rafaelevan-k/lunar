using System.Globalization;

namespace Lunar.Application.Abstractions.System;

/// <summary>
/// An <see cref="IClock"/> that also knows the user's time zone and regional format (BE-14), so date and time are shown
/// the way the user set Windows up. Time zone and regional format can change while the app runs; they are picked up on
/// a later read (at most about 30 seconds after the change) or at once by <see cref="Refresh"/>, so a UI that re-reads
/// on its minute tick follows them without any notification. Implementations must be safe to read from any thread.
/// </summary>
public interface ILocaleClock : IClock
{
    /// <summary>The regional format for dates and times (the user's Windows setting). Never null.</summary>
    CultureInfo Culture { get; }

    /// <summary>The user's current time zone. Never null.</summary>
    TimeZoneInfo TimeZone { get; }

    /// <summary>Re-reads the time zone and regional format now instead of waiting for the next refresh.</summary>
    void Refresh();
}
