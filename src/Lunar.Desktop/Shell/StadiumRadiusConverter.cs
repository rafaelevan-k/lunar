using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Lunar.Desktop.Shell;

/// <summary>
/// Turns an element height into a corner radius of half that height, capped at the panel radius: the stadium (pill)
/// outline of the iPhone Dynamic Island with perfectly semicircular ends while idle, and the 24 DIP panel radius once
/// the element is taller than 48 DIP. A fixed huge radius (such as 999) would not do: WPF scales an oversized radius on
/// both axes, which bends the pill into an ellipse.
/// </summary>
public sealed class StadiumRadiusConverter : IValueConverter
{
    /// <summary>The panel radius token (<c>Lunar.Radius.Panel</c>) in DIPs.</summary>
    internal const double MaxRadius = 24;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double height && double.IsFinite(height) && height > 0
            ? new CornerRadius(Math.Min(height / 2, MaxRadius))
            : new CornerRadius(0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
