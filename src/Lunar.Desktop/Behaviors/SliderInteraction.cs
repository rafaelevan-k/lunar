using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Lunar.Desktop.Behaviors;

/// <summary>
/// Tells a view model when the user is holding a slider, so background updates can wait instead of moving the thumb under
/// the hand (FE-14). Set <see cref="IsTrackingProperty"/> on a slider and bind <see cref="IsAdjustingProperty"/>
/// one way to source:
/// - Adjusting starts when a thumb drag starts (a drag of the thumb, or a click on the track, which starts one too) or a
///   navigation key goes down, and ends when the drag completes, the key goes up, the slider loses keyboard focus, is
///   disabled or is unloaded. Whatever ends the interaction, adjusting never stays on.
/// - <see cref="WheelStepProperty"/> (when above zero) makes the mouse wheel over the slider change it by that many units
///   per notch and marks the event handled, so the wheel never scrolls or swipes anything else.
/// It holds no state of its own and no reference to a view model. Use on the UI thread.
/// </summary>
public static class SliderInteraction
{
    public static readonly DependencyProperty IsTrackingProperty = DependencyProperty.RegisterAttached(
        "IsTracking", typeof(bool), typeof(SliderInteraction), new PropertyMetadata(false, OnIsTrackingChanged));

    public static readonly DependencyProperty IsAdjustingProperty = DependencyProperty.RegisterAttached(
        "IsAdjusting", typeof(bool), typeof(SliderInteraction), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty WheelStepProperty = DependencyProperty.RegisterAttached(
        "WheelStep", typeof(double), typeof(SliderInteraction), new PropertyMetadata(0d));

    public static bool GetIsTracking(DependencyObject element) => (bool)Require(element).GetValue(IsTrackingProperty);

    public static void SetIsTracking(DependencyObject element, bool value) => Require(element).SetValue(IsTrackingProperty, value);

    public static bool GetIsAdjusting(DependencyObject element) => (bool)Require(element).GetValue(IsAdjustingProperty);

    public static void SetIsAdjusting(DependencyObject element, bool value) => Require(element).SetValue(IsAdjustingProperty, value);

    public static double GetWheelStep(DependencyObject element) => (double)Require(element).GetValue(WheelStepProperty);

    public static void SetWheelStep(DependencyObject element, double value) => Require(element).SetValue(WheelStepProperty, value);

    private static DependencyObject Require(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element;
    }

    private static void OnIsTrackingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Slider slider)
            return;

        if ((bool)e.NewValue)
        {
            slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnDragStarted));
            slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnDragCompleted));
            slider.PreviewKeyDown += OnKeyDown;
            slider.KeyUp += OnKeyUp;
            slider.LostKeyboardFocus += OnLostKeyboardFocus;
            slider.IsEnabledChanged += OnIsEnabledChanged;
            slider.Unloaded += OnUnloaded;
            slider.PreviewMouseWheel += OnMouseWheel;
        }
        else
        {
            slider.RemoveHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnDragStarted));
            slider.RemoveHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnDragCompleted));
            slider.PreviewKeyDown -= OnKeyDown;
            slider.KeyUp -= OnKeyUp;
            slider.LostKeyboardFocus -= OnLostKeyboardFocus;
            slider.IsEnabledChanged -= OnIsEnabledChanged;
            slider.Unloaded -= OnUnloaded;
            slider.PreviewMouseWheel -= OnMouseWheel;
            SetIsAdjusting(slider, false);
        }
    }

    private static void OnDragStarted(object sender, DragStartedEventArgs e) => SetIsAdjusting((Slider)sender, true);

    private static void OnDragCompleted(object sender, DragCompletedEventArgs e) => SetIsAdjusting((Slider)sender, false);

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (IsNavigationKey(e.Key))
            SetIsAdjusting((Slider)sender, true);
    }

    private static void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (IsNavigationKey(e.Key))
            SetIsAdjusting((Slider)sender, false);
    }

    private static void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => SetIsAdjusting((Slider)sender, false);

    private static void OnIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!(bool)e.NewValue)
            SetIsAdjusting((Slider)sender, false);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e) => SetIsAdjusting((Slider)sender, false);

    private static void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var slider = (Slider)sender;
        var step = GetWheelStep(slider);
        if (!(step > 0) || !slider.IsEnabled || e.Delta == 0)
            return;

        // One notch is 120 units; a precision touchpad sends smaller deltas, which move proportionally.
        var change = step * e.Delta / 120d;
        slider.Value = Math.Clamp(slider.Value + change, slider.Minimum, slider.Maximum);
        e.Handled = true;
    }

    private static bool IsNavigationKey(Key key) =>
        key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End;
}
