using System.Windows;

namespace Lunar.Desktop.Behaviors;

/// <summary>
/// The reduced-motion preference as an inherited attached property (FE-12): set it once on a root element (the island
/// shell does) and every reusable control below it reads it, so each one can drop or simplify its own animation
/// without being wired up separately.
/// </summary>
public static class Motion
{
    public static readonly DependencyProperty ReduceMotionProperty = DependencyProperty.RegisterAttached(
        "ReduceMotion",
        typeof(bool),
        typeof(Motion),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetReduceMotion(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(ReduceMotionProperty);
    }

    public static void SetReduceMotion(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ReduceMotionProperty, value);
    }
}
