using System.Windows;
using System.Windows.Interop;

namespace Lunar.Desktop.Shell;

/// <summary>
/// Borderless transparent topmost window that hosts the island pill. Window mechanics only: it never takes
/// activation from the app the user is working in, except while the user explicitly opens it for keyboard use.
/// Business logic and OS calls do not belong here.
/// </summary>
public partial class IslandWindow : Window
{
    public IslandWindow()
    {
        InitializeComponent();

        // Size limits and placement belong to IslandPositionController (FE-03, FE-05); this window only applies them.
        SourceInitialized += OnSourceInitialized;
        Deactivated += OnDeactivated;
        Closed += OnClosed;
    }

    /// <summary>Raised after <see cref="ViewContent"/> is replaced, so the owner can measure the new content.</summary>
    public event EventHandler? ViewContentChanged;

    /// <summary>The single place later features present their content.</summary>
    public object? ViewContent
    {
        get => ViewHost.Content;
        set
        {
            ViewHost.Content = value;
            ViewContentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The size, in device-independent units, the island wants for its current content within the available space.
    /// It measures the content plus the pill's border, not the pill itself, so a size the pill is given while it
    /// animates never changes the size the window asks for.
    /// </summary>
    internal Size MeasureContent(Size available)
    {
        var border = Pill.BorderThickness;
        var chromeWidth = border.Left + border.Right;
        var chromeHeight = border.Top + border.Bottom;

        // WPF reuses a cached measurement for an element whose own layout is unchanged, so a change deep inside
        // the content would be missed. The tree is small, so every element is invalidated to force a real measure.
        InvalidateMeasureDeep(ViewHost);
        ViewHost.Measure(new Size(Math.Max(0, available.Width - chromeWidth), Math.Max(0, available.Height - chromeHeight)));
        return new Size(ViewHost.DesiredSize.Width + chromeWidth, ViewHost.DesiredSize.Height + chromeHeight);
    }

    private int _pillAnimation;

    /// <summary>
    /// Glides the pill to <paramref name="toWidth"/> x <paramref name="toHeight"/>, centred at the top, and calls
    /// <paramref name="completed"/> when it arrives. It starts from the live size when an animation is running (a
    /// reversal, so there is no jump) and from <paramref name="restWidth"/> x <paramref name="restHeight"/> otherwise:
    /// the pill's rendered size cannot be trusted at that point, because resizing the window re-lays it out at once. A newer call replaces a running one; the replaced
    /// animation's completion is never reported. With a zero <paramref name="duration"/> the pill jumps and
    /// <paramref name="completed"/> is not called (the caller completes at once).
    /// </summary>
    internal void AnimatePill(double restWidth, double restHeight, double toWidth, double toHeight, TimeSpan duration, Action completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        var version = ++_pillAnimation;
        // While animating, the explicit size is the live animated value; at rest it is unset.
        var from = double.IsNaN(Pill.Width) || double.IsNaN(Pill.Height) ? new Size(restWidth, restHeight) : new Size(Pill.Width, Pill.Height);

        Pill.HorizontalAlignment = HorizontalAlignment.Center;
        Pill.VerticalAlignment = VerticalAlignment.Top;
        Pill.BeginAnimation(WidthProperty, null);
        Pill.BeginAnimation(HeightProperty, null);
        Pill.Width = toWidth;
        Pill.Height = toHeight;
        if (duration <= TimeSpan.Zero)
            return;

        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut };
        var width = new System.Windows.Media.Animation.DoubleAnimation(from.Width, toWidth, duration)
        {
            EasingFunction = ease,
            FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd
        };
        var height = new System.Windows.Media.Animation.DoubleAnimation(from.Height, toHeight, duration)
        {
            EasingFunction = ease,
            FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd
        };
        height.Completed += (_, _) =>
        {
            if (version == _pillAnimation) // a replaced animation is never reported
                completed();
        };
        Pill.BeginAnimation(WidthProperty, width);
        Pill.BeginAnimation(HeightProperty, height);
    }

    /// <summary>
    /// Gives a pill that is at rest an explicit size, so it keeps that size when the window is resized next. Without
    /// this, enlarging the window for an expansion would stretch the pill to full size for a moment before the
    /// animation starts. Does nothing when the pill already has an explicit (animating) size.
    /// </summary>
    internal void HoldPill(double width, double height)
    {
        if (!double.IsNaN(Pill.Width) && !double.IsNaN(Pill.Height))
            return;
        Pill.HorizontalAlignment = HorizontalAlignment.Center;
        Pill.VerticalAlignment = VerticalAlignment.Top;
        Pill.Width = width;
        Pill.Height = height;
    }

    /// <summary>Stops any animation and lets the pill fill the window again.</summary>
    internal void ResetPill()
    {
        _pillAnimation++; // invalidates a running animation's completion
        Pill.BeginAnimation(WidthProperty, null);
        Pill.BeginAnimation(HeightProperty, null);
        Pill.ClearValue(WidthProperty);
        Pill.ClearValue(HeightProperty);
        Pill.HorizontalAlignment = HorizontalAlignment.Stretch;
        Pill.VerticalAlignment = VerticalAlignment.Stretch;
    }

    private static void InvalidateMeasureDeep(DependencyObject node)
    {
        if (node is UIElement element)
            element.InvalidateMeasure();
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
            InvalidateMeasureDeep(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
    }

    /// <summary>Applies the allowed range and the chosen size in device-independent units.</summary>
    internal void ApplySize(IslandLayoutLimits limits, double width, double height)
    {
        MinWidth = limits.MinWidth;
        MinHeight = limits.MinHeight;
        MaxWidth = limits.MaxWidth;
        MaxHeight = limits.MaxHeight;
        if (Width != width)
            Width = width;
        if (Height != height)
            Height = height;
    }

    /// <summary>
    /// Explicit keyboard/tray activation (ADR-0007): temporarily allows activation, takes focus, and goes back
    /// to non-activating as soon as the window loses focus.
    /// </summary>
    public void OpenForKeyboard()
    {
        var handle = new WindowInteropHelper(this).EnsureHandle();
        NonActivatingWindowStyle.Remove(handle);
        Show();
        Activate();
        Focus();
    }

    private void OnSourceInitialized(object? sender, EventArgs e) =>
        NonActivatingWindowStyle.Apply(new WindowInteropHelper(this).Handle);

    private void OnDeactivated(object? sender, EventArgs e) =>
        NonActivatingWindowStyle.Apply(new WindowInteropHelper(this).Handle);

    private void OnClosed(object? sender, EventArgs e)
    {
        SourceInitialized -= OnSourceInitialized;
        Deactivated -= OnDeactivated;
        Closed -= OnClosed;
    }
}
