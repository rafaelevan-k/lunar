using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Lunar.Desktop.Shell;

/// <summary>
/// The Notch outline (ADR-0027): square on top against the screen's edge, small outward shoulders at both top corners that blend into the edge, and
/// rounded bottom corners. The shoulders are carved out of the pill's own width, so no layout or size changes between the styles; every activity
/// keeps its content clear of the 8 DIP shoulder at each side. Pure geometry.
/// </summary>
internal static class IslandOutline
{
    /// <summary>Size of each shoulder in DIPs.</summary>
    internal const double Shoulder = 8;

    /// <summary>The window border the Notch has (none; High Contrast keeps one on the sides and bottom).</summary>
    internal static Thickness Border(Lunar.Application.Settings.Models.IslandStyle style) =>
        style != Lunar.Application.Settings.Models.IslandStyle.Notch ? new Thickness(1)
        : SystemParameters.HighContrast ? new Thickness(1, 0, 1, 1) : new Thickness(0);

    /// <summary>Bottom corner radius for a height: a little rounder than a hardware notch while idle, the panel radius once open.</summary>
    internal static double NotchBottomRadius(double height) =>
        double.IsFinite(height) && height > 0 ? Math.Min(height * 0.3, MaxBottomRadius) : 0;

    /// <summary>The open Notch's bottom corners: rounder than the Pill's panel, as on the notch-style islands.</summary>
    internal const double MaxBottomRadius = 28;

    /// <summary>The Notch outline for a pill of this size, or null when it is too small to draw.</summary>
    internal static Geometry? Notch(double width, double height, bool shoulders)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 2 || height <= 2)
            return null;

        var s = shoulders ? Math.Min(Shoulder, Math.Min(width / 4, height / 2)) : 0;
        var r = Math.Max(0, Math.Min(NotchBottomRadius(height), Math.Min(height - s, (width - (2 * s)) / 2)));
        var figure = new PathFigure { StartPoint = new Point(0, 0), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new LineSegment(new Point(width, 0), true));
        if (s > 0)
            figure.Segments.Add(new ArcSegment(new Point(width - s, s), new Size(s, s), 0, false, SweepDirection.Counterclockwise, true));
        figure.Segments.Add(new LineSegment(new Point(width - s, height - r), true));
        if (r > 0)
            figure.Segments.Add(new ArcSegment(new Point(width - s - r, height), new Size(r, r), 0, false, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(new Point(s + r, height), true));
        if (r > 0)
            figure.Segments.Add(new ArcSegment(new Point(s, height - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(new Point(s, s), true));
        if (s > 0)
            figure.Segments.Add(new ArcSegment(new Point(0, 0), new Size(s, s), 0, false, SweepDirection.Counterclockwise, true));

        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }
}

/// <summary>The Notch's corner radius for an element height: none on top, <see cref="IslandOutline.NotchBottomRadius"/> at the bottom.</summary>
public sealed class NotchRadiusConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var bottom = value is double height ? IslandOutline.NotchBottomRadius(height) : 0;
        return new CornerRadius(0, 0, bottom, bottom);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
