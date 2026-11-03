using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Lunar.Desktop.Shell;

/// <summary>
/// Turns an element height into a corner radius of half that height: the stadium (pill) outline of the iPhone
/// Dynamic Island, with perfectly semicircular ends. A fixed huge radius (such as 999) would not do: WPF scales an
/// oversized radius on both axes, which bends the pill into an ellipse.
/// </summary>
public sealed class StadiumRadiusConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double height && double.IsFinite(height) && height > 0
            ? new CornerRadius(height / 2)
            : new CornerRadius(0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
