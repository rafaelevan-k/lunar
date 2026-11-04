using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Lunar.Desktop.Features.Eyes;

/// <summary>
/// The two eyes (FE-07 artwork). With an <see cref="EyeAnimationController"/> attached (FE-08) the lids blink; the view
/// also follows the pointer with the pupils, shows hover and press feedback, and raises <see cref="ClickCommand"/> on a
/// click (FE-09). All motion is WPF animation on the render thread: there is no per-frame code and no timer here.
/// The pointer is only tracked while it is over this view (the island window), never elsewhere on the screen, and the
/// pupils return to neutral when it leaves. With reduced motion there is no gaze and the feedback is instant.
/// </summary>
public partial class EyesView : UserControl
{
    /// <summary>How far the lids close while asleep: 0 is open, 1 is fully closed.</summary>
    internal const double AsleepClosure = 0.55;

    internal const double HoverScale = 1.08;
    internal const double PressScale = 0.92;

    private static readonly TimeSpan GazeFollow = TimeSpan.FromMilliseconds(90);
    private static readonly TimeSpan GazeReturn = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan FeedbackTime = TimeSpan.FromMilliseconds(110);
    private static readonly IEasingFunction EaseOut = CreateEase();

    private EyeAnimationController? _controller;
    private bool _pressed;

    // Frozen, so the shared instance is not tied to the thread that happened to create it first.
    private static CubicEase CreateEase()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }

    public EyesView()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
        MouseEnter += OnMouseEnter;
        MouseMove += OnMouseMove;
        MouseLeave += OnMouseLeave;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        LostMouseCapture += OnLostMouseCapture;
    }

    /// <summary>Run when the user clicks the eyes (press and release over them). The owner decides what it does.</summary>
    public ICommand? ClickCommand { get; set; }

    private bool MotionAllowed => _controller is null || !_controller.ReduceMotion;

    /// <summary>
    /// A happy moment (a conversion finished): both eyes smile (arched, as in ^ ^) for about a second and, with motion allowed, hop a little.
    /// With reduced motion the smile is shown without any movement. Safe to call at any time; a second call simply restarts it.
    /// </summary>
    public void Celebrate()
    {
        var motion = MotionAllowed;
        foreach (var cheek in new[] { LeftCheekOffset, RightCheekOffset })
        {
            var smile = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            if (motion)
            {
                smile.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(170)), new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 }));
                smile.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1200))));
                smile.KeyFrames.Add(new EasingDoubleKeyFrame(30, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1420)), new CubicEase { EasingMode = EasingMode.EaseIn }));
            }
            else
            {
                // No movement: the smile simply appears and goes again.
                smile.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                smile.KeyFrames.Add(new DiscreteDoubleKeyFrame(30, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1200))));
            }

            cheek.BeginAnimation(TranslateTransform.YProperty, smile);
        }

        if (!motion)
            return;
        var hop = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(-4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(130)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(330)), new BounceEase { EasingMode = EasingMode.EaseOut, Bounces = 2, Bounciness = 3 }));
        JumpOffset.BeginAnimation(TranslateTransform.YProperty, hop);
    }

    /// <summary>Starts following <paramref name="controller"/>. The view does not own it and never disposes it.</summary>
    public void Attach(EyeAnimationController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        Detach();
        _controller = controller;
        controller.StateChanged += OnStateChanged;
        controller.IsHidden = !IsVisible;
        Apply(controller.State);
    }

    /// <summary>Stops following the controller and leaves the eyes open. Safe to call when nothing is attached.</summary>
    public void Detach()
    {
        if (_controller is null)
            return;
        _controller.StateChanged -= OnStateChanged;
        _controller.IsHidden = true; // a detached view is not shown, so the controller must not keep a timer
        _controller = null;
        Apply(EyeAnimationState.Open);
    }

    private void OnStateChanged(object? sender, EyeAnimationState state) => Apply(state);

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_controller is not null)
            _controller.IsHidden = !IsVisible;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ResetPointerState();
        Detach();
    }

    private void OnMouseEnter(object sender, MouseEventArgs e) => PointerEntered(e.GetPosition(this));

    private void OnMouseMove(object sender, MouseEventArgs e) => PointerMoved(e.GetPosition(this));

    private void OnMouseLeave(object sender, MouseEventArgs e) => PointerLeft();

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        PointerPressed();
        e.Handled = true;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed)
            return;
        PointerReleased(e.GetPosition(this));
        e.Handled = true;
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_pressed)
            return;
        _pressed = false;
        ResetPointerState(); // capture was taken away (for example the window deactivated): no click
    }

    // The pointer rules, with the position passed in so tests can drive them without a real mouse.
    internal void PointerEntered(Point position)
    {
        _controller?.NotifyInteraction();
        MoveFeedback(_pressed ? PressScale : HoverScale);
        Follow(position);
    }

    internal void PointerMoved(Point position)
    {
        _controller?.NotifyInteraction();
        Follow(position);
    }

    internal void PointerLeft()
    {
        if (!_pressed) // while pressed the pointer is captured; the release decides
            ResetPointerState();
    }

    internal void PointerPressed()
    {
        _controller?.NotifyInteraction();
        _pressed = true;
        CaptureMouse(); // so the release is seen even if the pointer slid off, and a release outside is not a click
        MoveFeedback(PressScale);
    }

    internal void PointerReleased(Point position)
    {
        var inside = IsPointerInside(position);
        _pressed = false;
        ReleaseMouseCapture();

        if (!inside)
        {
            ResetPointerState();
            return;
        }

        MoveFeedback(HoverScale);
        var command = ClickCommand;
        if (command?.CanExecute(null) == true)
            command.Execute(null);
    }

    private bool IsPointerInside(Point position) =>
        position.X >= 0 && position.Y >= 0 && position.X <= ActualWidth && position.Y <= ActualHeight;

    // Neutral gaze and normal size.
    private void ResetPointerState()
    {
        MoveGaze(LeftPupilOffset, 0, 0, GazeReturn);
        MoveGaze(RightPupilOffset, 0, 0, GazeReturn);
        MoveFeedback(1);
    }

    private void Follow(Point pointer)
    {
        if (!MotionAllowed)
            return;
        FollowEye(LeftEye, LeftPupilOffset, pointer);
        FollowEye(RightEye, RightPupilOffset, pointer);
    }

    private void FollowEye(FrameworkElement eye, TranslateTransform pupil, Point pointer)
    {
        if (eye.ActualWidth <= 0 || eye.ActualHeight <= 0)
            return;
        var center = eye.TranslatePoint(new Point(eye.ActualWidth / 2, eye.ActualHeight / 2), this);
        var (x, y) = EyeGaze.Offset(pointer.X - center.X, pointer.Y - center.Y);
        MoveGaze(pupil, x, y, GazeFollow);
    }

    private void MoveGaze(TranslateTransform pupil, double x, double y, TimeSpan duration)
    {
        Glide(pupil, TranslateTransform.XProperty, x, duration);
        Glide(pupil, TranslateTransform.YProperty, y, duration);
    }

    private void MoveFeedback(double scale)
    {
        Glide(FeedbackScale, ScaleTransform.ScaleXProperty, scale, FeedbackTime);
        Glide(FeedbackScale, ScaleTransform.ScaleYProperty, scale, FeedbackTime);
    }

    // Moves a property to its target. The resting value is set first so the end state is exact; the animation only
    // supplies the in-between frames and is dropped when finished (FillBehavior.Stop), so nothing keeps running.
    private void Glide(Animatable target, DependencyProperty property, double to, TimeSpan duration)
    {
        var from = (double)target.GetValue(property); // the live, possibly mid-animation value
        ((IAnimatable)target).BeginAnimation(property, null);
        target.SetValue(property, to);
        if (!MotionAllowed || duration <= TimeSpan.Zero || Math.Abs(from - to) < 1e-3)
            return;
        ((IAnimatable)target).BeginAnimation(property,
            new DoubleAnimation(from, to, duration) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });
    }

    private void Apply(EyeAnimationState state)
    {
        var target = state.Phase switch
        {
            EyePhase.Closing or EyePhase.Closed => 1.0,
            EyePhase.Asleep => AsleepClosure,
            _ => 0.0
        };
        MoveLid(LeftEyelid, target, state.Duration);
        MoveLid(RightEyelid, target, state.Duration);
    }

    private static void MoveLid(Rectangle lid, double target, TimeSpan duration)
    {
        var scale = (ScaleTransform)lid.RenderTransform;
        var from = scale.ScaleY; // the currently animated value, so an interrupted move continues smoothly
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleY = target; // the resting value once the animation stops
        if (duration <= TimeSpan.Zero || from == target)
            return;
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(from, target, duration) { FillBehavior = FillBehavior.Stop });
    }
}
