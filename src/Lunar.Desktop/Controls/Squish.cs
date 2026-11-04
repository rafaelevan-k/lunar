using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Lunar.Desktop.Behaviors;

namespace Lunar.Desktop.Controls;

/// <summary>
/// The playful press feel (the UI revision): an element with <c>c:Squish.Enabled="True"</c> shrinks a little while it is pressed and
/// springs back when it is let go, like a soft button. It changes only a render transform (no layout, no hit area), starts nothing
/// while the control is disabled, and does nothing at all when reduced motion is on (<see cref="Motion.ReduceMotionProperty"/>). The style
/// of every button sets it, so a screen never wires it by hand.
/// </summary>
public static class Squish
{
    internal const double PressedScale = 0.92;

    private static readonly TimeSpan Down = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan Up = TimeSpan.FromMilliseconds(420);
    private static readonly IEasingFunction PressEase = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction SpringEase = Frozen(new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 4 });

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(Squish), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(EnabledProperty);
    }

    public static void SetEnabled(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(EnabledProperty, value);
    }

    private static T Frozen<T>(T easing)
        where T : Freezable
    {
        easing.Freeze();
        return easing;
    }

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;

        element.PreviewMouseLeftButtonDown -= OnPressed;
        element.PreviewMouseLeftButtonUp -= OnReleased;
        element.MouseLeave -= OnReleased;
        element.LostMouseCapture -= OnLost;
        if (e.NewValue is not true)
            return;

        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.PreviewMouseLeftButtonDown += OnPressed;
        element.PreviewMouseLeftButtonUp += OnReleased;
        element.MouseLeave += OnReleased;
        element.LostMouseCapture += OnLost;
    }

    private static void OnPressed(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { IsEnabled: true } element && !Motion.GetReduceMotion(element))
            Animate(element, PressedScale, Down, PressEase);
    }

    private static void OnReleased(object sender, MouseEventArgs e) => Release(sender);

    private static void OnLost(object sender, MouseEventArgs e) => Release(sender);

    private static void Release(object sender)
    {
        if (sender is FrameworkElement element && element.RenderTransform is ScaleTransform { ScaleX: not 1.0 })
            Animate(element, 1.0, Up, SpringEase);
    }

    private static void Animate(FrameworkElement element, double to, TimeSpan duration, IEasingFunction ease)
    {
        if (element.RenderTransform is not ScaleTransform scale || scale.IsFrozen)
        {
            scale = new ScaleTransform(1, 1);
            element.RenderTransform = scale;
        }

        var from = scale.ScaleX;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(from, to, duration) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(from, to, duration) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd });
    }
}
