using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Lunar.Desktop.Controls;

/// <summary>Shows an element only when its bound value is not null (for example an optional command).</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public static NullToVisibilityConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
