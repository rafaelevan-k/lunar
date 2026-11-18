using Lunar.Application.Settings.Models;
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

    /// <summary>The collapsed island while recording (ADR-0024): the lines, the time and two buttons do not fit the normal pill.</summary>
    public const double RecordingIdleWidth = 232;
    public const double ExpandedMinWidth = 380;
    public const double ExpandedMinHeight = 320;

    /// <summary>The Notch (ADR-0027, amended 2026-11-18): shorter and wider than the Pill, like the notch-style islands on current MacBooks.</summary>
    public const double NotchIdleHeight = 32;
    public const double NotchIdleWidth = 230;
    public const double NotchMediaIdleWidth = 280;
    public const double NotchRecordingIdleWidth = 300;

    /// <summary>The open Notch is a wide panel hanging from the top edge, with the pages as tabs along its top.</summary>
    public const double NotchExpandedMinWidth = 680;
    public const double NotchExpandedMinHeight = 310;

    /// <summary>Space kept between an expanded island and the work-area edges, in DIPs.</summary>
    public const double EdgeMargin = 8;

    public static IslandLayoutLimits LimitsFor(bool expanded, DisplayInfo display, double topOffsetDip) =>
        LimitsFor(expanded, display, topOffsetDip, IslandStyle.Pill);

    /// <summary>The collapsed pill's height for a style.</summary>
    public static double IdleHeightFor(IslandStyle style) => style == IslandStyle.Notch ? NotchIdleHeight : IdleHeight;

    public static IslandLayoutLimits LimitsFor(bool expanded, DisplayInfo display, double topOffsetDip, IslandStyle style)
    {
        ArgumentNullException.ThrowIfNull(display);
        if (!double.IsFinite(topOffsetDip) || topOffsetDip < 0)
            throw new ArgumentOutOfRangeException(nameof(topOffsetDip), topOffsetDip, "Offset must be zero or positive.");

        var idleWidth = IdleWidthFor(IslandActivity.Eyes, style);
        var idleHeight = IdleHeightFor(style);
        if (!expanded)
            return new IslandLayoutLimits(idleWidth, idleHeight, idleWidth, idleHeight);
        var minWidth = style == IslandStyle.Notch ? NotchExpandedMinWidth : ExpandedMinWidth;
        var minHeight = style == IslandStyle.Notch ? NotchExpandedMinHeight : ExpandedMinHeight;

        var availableWidth = (display.WorkArea.Width / display.ScaleX) - (2 * EdgeMargin);
        var availableHeight = (display.WorkArea.Height / display.ScaleY) - topOffsetDip - EdgeMargin;

        // Never smaller than the pill, even on a tiny work area.
        var maxWidth = Math.Max(availableWidth, idleWidth);
        var maxHeight = Math.Max(availableHeight, idleHeight);
        return new IslandLayoutLimits(
            Math.Min(minWidth, maxWidth),
            Math.Min(minHeight, maxHeight),
            maxWidth,
            maxHeight);
    }

    /// <summary>How wide the collapsed pill is for what it shows.</summary>
    public static double IdleWidthFor(IslandActivity activity) => IdleWidthFor(activity, IslandStyle.Pill);

    /// <summary>How wide the collapsed pill is for what it shows, in a style. The Notch gives music more room: the picture at one end, the bars at the other.</summary>
    public static double IdleWidthFor(IslandActivity activity, IslandStyle style) => style == IslandStyle.Notch
        ? activity switch
        {
            IslandActivity.Recording => NotchRecordingIdleWidth,
            IslandActivity.MediaPlaying => NotchMediaIdleWidth,
            _ => NotchIdleWidth
        }
        : activity == IslandActivity.Recording ? RecordingIdleWidth : IdleWidth;

    /// <summary>
    /// The window's own limits (FE-33): always as wide as the expanded panel, and only its height follows the layout. Windows moves a layered
    /// window and replaces its picture at different moments, so a window that moved sideways on every expand and collapse showed some frames
    /// with the eyes far from the pill. With the left edge fixed there is nothing to get out of step. The part of the window beside the idle
    /// pill is fully transparent, so it is neither visible nor clickable.
    /// </summary>
    public static IslandLayoutLimits WindowLimitsFor(bool expanded, DisplayInfo display, double topOffsetDip) =>
        WindowLimitsFor(expanded, display, topOffsetDip, IslandStyle.Pill);

    public static IslandLayoutLimits WindowLimitsFor(bool expanded, DisplayInfo display, double topOffsetDip, IslandStyle style)
    {
        var panel = LimitsFor(expanded: true, display, topOffsetDip, style);
        var height = IdleHeightFor(style);
        return expanded ? panel : panel with { MinHeight = height, MaxHeight = height };
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
