using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Lunar.Desktop.Features.Eyes;

/// <summary>
/// The two eyes (FE-07 artwork). With an <see cref="EyeAnimationController"/> attached (FE-08) it animates the
/// eyelids from the controller's state using WPF animations, which run on the render thread, so there is no per-frame
/// code here. Gaze and hover (FE-09) will drive the pupil transforms.
/// </summary>
public partial class EyesView : UserControl
{
    /// <summary>How far the lids close while asleep: 0 is open, 1 is fully closed.</summary>
    internal const double AsleepClosure = 0.55;

    private EyeAnimationController? _controller;

    public EyesView()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
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

    private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

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
