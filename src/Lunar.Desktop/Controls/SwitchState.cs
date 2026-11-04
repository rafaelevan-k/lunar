using System.Windows;

namespace Lunar.Desktop.Controls;

/// <summary>
/// The on/off state a switch button draws (FE-15). It is an attached property, not the button's own state: pressing a
/// button using <c>Lunar.Style.SwitchButton</c> never changes it. Bind it to the state the system confirmed, and the
/// switch moves only when that state does.
/// </summary>
public static class SwitchState
{
    public static readonly DependencyProperty IsOnProperty = DependencyProperty.RegisterAttached(
        "IsOn", typeof(bool), typeof(SwitchState), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static bool GetIsOn(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsOnProperty);
    }

    public static void SetIsOn(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsOnProperty, value);
    }
}
