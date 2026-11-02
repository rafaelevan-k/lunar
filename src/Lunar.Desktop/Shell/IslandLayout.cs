using Lunar.Application.Shell.Models;

namespace Lunar.Desktop.Shell;

/// <summary>Allowed window size in device-independent units for one layout.</summary>
public sealed record IslandLayoutLimits(double MinWidth, double MinHeight, double MaxWidth, double MaxHeight);

/// <summary>
/// Pure sizing rules for the island window (PRD §3.1, FE-03), in device-independent units (DIPs), so one rule set
/// holds at every DPI scale. The idle pill is a fixed 160×44. The expanded layout is at least 380×320 and grows to
/// fit its content, but never beyond the display's work area minus an edge margin (and the top offset), so it is
/// not clipped at any scale. If the work area is smaller than the default expanded size, the minimum shrinks to fit.
/// </summary>
public static class IslandLayout
{
    public const double IdleWidth = 160;
    public const double IdleHeight = 44;
    public const double ExpandedMinWidth = 380;
    public const double ExpandedMinHeight = 320;

    /// <summary>Space kept between an expanded island and the work-area edges, in DIPs.</summary>
    public const double EdgeMargin = 8;

    public static IslandLayoutLimits LimitsFor(bool expanded, DisplayInfo display, double topOffsetDip)
    {
        ArgumentNullException.ThrowIfNull(display);
        if (!double.IsFinite(topOffsetDip) || topOffsetDip < 0)
            throw new ArgumentOutOfRangeException(nameof(topOffsetDip), topOffsetDip, "Offset must be zero or positive.");

        if (!expanded)
            return new IslandLayoutLimits(IdleWidth, IdleHeight, IdleWidth, IdleHeight);

        var availableWidth = (display.WorkArea.Width / display.ScaleX) - (2 * EdgeMargin);
        var availableHeight = (display.WorkArea.Height / display.ScaleY) - topOffsetDip - EdgeMargin;

        // Never smaller than the pill, even on a tiny work area.
        var maxWidth = Math.Max(availableWidth, IdleWidth);
        var maxHeight = Math.Max(availableHeight, IdleHeight);
        return new IslandLayoutLimits(
            Math.Min(ExpandedMinWidth, maxWidth),
            Math.Min(ExpandedMinHeight, maxHeight),
            maxWidth,
            maxHeight);
    }

    /// <summary>The measured content size clamped into the limits; non-finite or negative sizes count as zero.</summary>
    public static (double Width, double Height) Fit(IslandLayoutLimits limits, double contentWidth, double contentHeight)
    {
        ArgumentNullException.ThrowIfNull(limits);
        return (Clamp(contentWidth, limits.MinWidth, limits.MaxWidth), Clamp(contentHeight, limits.MinHeight, limits.MaxHeight));
    }

    private static double Clamp(double value, double min, double max)
    {
        var safe = double.IsFinite(value) && value > 0 ? value : 0;
        return Math.Clamp(safe, min, max);
    }
}
