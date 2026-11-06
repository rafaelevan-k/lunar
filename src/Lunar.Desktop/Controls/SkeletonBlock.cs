using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Lunar.Desktop.Behaviors;

namespace Lunar.Desktop.Controls;

/// <summary>
/// A placeholder block shown while content loads (FE-12). It pulses softly only while it is active, visible, loaded and
/// motion is allowed (<see cref="Motion.ReduceMotionProperty"/>); in every other case it is a static block and no
/// animation clock keeps running, so a collapsed or hidden island pays nothing. It is not focusable and tells assistive
/// technology only "Loading".
/// </summary>
public sealed class SkeletonBlock : Control
{
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(SkeletonBlock), new PropertyMetadata(true, OnPulseInputChanged));

    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(SkeletonBlock), new PropertyMetadata(new CornerRadius(6)));

    private static readonly TimeSpan PulseTime = TimeSpan.FromMilliseconds(900);

    static SkeletonBlock()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SkeletonBlock), new FrameworkPropertyMetadata(typeof(SkeletonBlock)));
        // The inherited reduced-motion value reaches this control from an ancestor; react when it changes.
        Motion.ReduceMotionProperty.OverrideMetadata(typeof(SkeletonBlock),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits, OnPulseInputChanged));
    }

    public SkeletonBlock()
    {
        Focusable = false;
        IsTabStop = false;
        MinHeight = 12;
        AutomationProperties.SetName(this, Lunar.Desktop.Resources.Strings.Get("Common.Loading"));
        Loaded += (_, _) => UpdatePulse();
        Unloaded += (_, _) => UpdatePulse();
        IsVisibleChanged += (_, _) => UpdatePulse();
    }

    /// <summary>Whether content is still loading. When it turns false the block stops pulsing (the owner then hides it).</summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <summary>True while the pulse animation is running (for tests).</summary>
    internal bool IsPulsing { get; private set; }

    private static void OnPulseInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SkeletonBlock)d).UpdatePulse();

    private void UpdatePulse()
    {
        var shouldPulse = IsActive && IsLoaded && IsVisible && !Motion.GetReduceMotion(this);
        if (shouldPulse == IsPulsing)
            return;

        IsPulsing = shouldPulse;
        BeginAnimation(OpacityProperty, null);
        if (!shouldPulse)
        {
            Opacity = 1;
            return;
        }

        BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.45, PulseTime)
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
    }
}
