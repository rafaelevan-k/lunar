using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Lunar.Desktop.Navigation;

/// <summary>
/// The four-page swiper (FE-11): pages side by side that follow a mouse or touch drag, with arrow buttons, page dots,
/// arrow keys and a touchpad's horizontal swipe. The decisions (how far to move, which page to land on) are in
/// <see cref="SwipeRules"/> and <see cref="SwiperController"/>; this class turns pointer and key events into them and
/// draws the result.
///
/// Focus and sliders: a drag that starts on a slider, button, text box, scroll bar, combo box or any element marked with
/// <see cref="BlocksSwipeProperty"/> never moves the pages, and arrow keys are handled on the bubbling KeyDown, so a
/// focused slider (which handles its own arrow keys) is never robbed of them. The arrow buttons never take focus. The
/// mouse wheel is not used at all, so it cannot steal a slider's wheel input.
/// Must be used on the UI thread.
/// </summary>
public partial class SwiperView : UserControl
{
    /// <summary>Mark an element (for example a file drop area) so a drag that starts on it does not swipe the pages.</summary>
    public static readonly DependencyProperty BlocksSwipeProperty =
        DependencyProperty.RegisterAttached("BlocksSwipe", typeof(bool), typeof(SwiperView), new PropertyMetadata(false));

    public static bool GetBlocksSwipe(DependencyObject element) => (bool)element.GetValue(BlocksSwipeProperty);

    public static void SetBlocksSwipe(DependencyObject element, bool value) => element.SetValue(BlocksSwipeProperty, value);

    private const int WM_MOUSEHWHEEL = 0x020E;
    private static readonly IEasingFunction EaseOut = CreateEase();

    private PageNavigationViewModel? _navigation;
    private SwiperController? _controller;
    private readonly HorizontalWheelGesture _wheel = new();
    private readonly List<RadioButton> _dots = [];
    private HwndSource? _source;
    private bool _reduceMotion;
    private bool _pressed;
    private Point _pressPoint;
    private int _settle;

    public SwiperView()
    {
        InitializeComponent();
        Viewport.SizeChanged += OnViewportSizeChanged;
        Viewport.PreviewMouseLeftButtonDown += OnPreviewMouseDown;
        Viewport.PreviewMouseMove += OnPreviewMouseMove;
        Viewport.PreviewMouseLeftButtonUp += OnPreviewMouseUp;
        Viewport.LostMouseCapture += OnLostMouseCapture;
        KeyDown += OnKeyDown;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>The page the strip currently rests on (for tests and accessibility).</summary>
    internal int SelectedIndex => _navigation?.SelectedIndex ?? 0;

    internal bool IsDragging => _controller?.IsDragging == true;

    /// <summary>The strip's resting horizontal position, for tests.</summary>
    internal double StripX => StripOffset.X;

    /// <summary>The motion setting changed (ADR-0016): the next page change slides or jumps accordingly.</summary>
    public void SetReduceMotion(bool reduceMotion) => _reduceMotion = reduceMotion;

    /// <summary>Sets the pages and the model they follow. Call once, before the view is shown.</summary>
    public void Initialize(PageNavigationViewModel navigation, IReadOnlyList<FrameworkElement> pages, bool reduceMotion)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(pages);
        if (_navigation is not null)
            throw new InvalidOperationException("The swiper is already initialized.");
        if (pages.Count != PageNavigationViewModel.PageCount)
            throw new ArgumentException($"Exactly {PageNavigationViewModel.PageCount} pages are required.", nameof(pages));

        _navigation = navigation;
        _controller = new SwiperController(navigation);
        _reduceMotion = reduceMotion;

        foreach (var page in pages)
            Strip.Children.Add(page);

        for (var i = 0; i < navigation.Titles.Count; i++)
        {
            var index = i;
            var dot = new RadioButton
            {
                Style = (Style)FindResource("SwiperDot"),
                IsChecked = i == navigation.SelectedIndex
            };
            System.Windows.Automation.AutomationProperties.SetName(dot, Lunar.Desktop.Resources.Strings.Format("Nav.PageDot", navigation.Titles[i]));
            dot.Checked += (_, _) => navigation.GoTo(index);
            _dots.Add(dot);
            Dots.Children.Add(dot);
        }

        navigation.PropertyChanged += OnNavigationChanged;
        UpdateArrows();
        LayoutStrip();
    }

    private static readonly BackEase Overshoot = CreateOvershoot();

    private static BackEase CreateOvershoot()
    {
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
        ease.Freeze();
        return ease;
    }

    private static CubicEase CreateEase()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }

    // ---- layout ---------------------------------------------------------------------------------------------------

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e) => LayoutStrip();

    // Every page is exactly as large as the viewport and the strip rests on the selected page.
    private void LayoutStrip()
    {
        if (_navigation is null || _controller is null)
            return;

        var width = Viewport.ActualWidth;
        var height = Viewport.ActualHeight;
        _controller.PageWidth = width;
        Strip.Width = width * Strip.Children.Count;
        Strip.Height = height;
        foreach (FrameworkElement page in Strip.Children)
        {
            page.Width = width;
            page.Height = height;
        }

        if (!_controller.IsDragging)
            Place(-_navigation.SelectedIndex * width);
    }

    private void Place(double x)
    {
        _settle++; // a running settle animation is replaced
        StripOffset.BeginAnimation(TranslateTransform.XProperty, null);
        StripOffset.X = x;
    }

    // ---- pointer --------------------------------------------------------------------------------------------------

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_navigation is null || IsBlocked(e.OriginalSource as DependencyObject))
            return;

        // Only take keyboard focus when the click did not land on something that can take it itself (a slider, a button).
        if (!IsInsideFocusableControl(e.OriginalSource as DependencyObject))
            Focus();

        PointerPressed(e.GetPosition(Viewport).X);
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_pressed || _controller is null)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _pressed = false;
            return;
        }

        var position = e.GetPosition(Viewport);
        PointerMoved(position.X, Environment.TickCount64, captureOnStart: true);
    }

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        var wasDragging = _controller?.IsDragging == true;
        if (_pressed)
            PointerReleased(Environment.TickCount64);
        if (wasDragging)
            e.Handled = true; // a drag is not a click on whatever the pointer ended over
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_controller?.IsDragging == true)
        {
            _controller.Cancel();
            SettleTo(_navigation!.SelectedIndex);
        }

        _pressed = false;
    }

    /// <summary>Pointer rules with the position and time passed in, so tests can drive them without a real mouse.</summary>
    internal void PointerPressed(double x, bool onBlocker = false)
    {
        if (_navigation is null || onBlocker)
            return;
        _pressed = true;
        _pressPoint = new Point(x, 0);
    }

    internal void PointerMoved(double x, long timestampMs, bool captureOnStart = false)
    {
        if (_controller is null || !_pressed)
            return;

        if (!_controller.IsDragging)
        {
            if (Math.Abs(x - _pressPoint.X) < SwipeRules.Slop)
                return; // still a click

            if (!_controller.Begin(_pressPoint.X, timestampMs))
                return;
            if (captureOnStart)
                Viewport.CaptureMouse(); // the children lose the press: a drag is not a click
        }

        var offset = _controller.Move(x, timestampMs);
        _settle++;
        StripOffset.BeginAnimation(TranslateTransform.XProperty, null);
        StripOffset.X = (-_navigation!.SelectedIndex * _controller.PageWidth) + offset;
    }

    internal void PointerReleased(long timestampMs)
    {
        _pressed = false;
        if (_controller is null || _navigation is null)
            return;
        if (!_controller.IsDragging)
            return;

        var result = _controller.End(timestampMs); // decide first: releasing the capture would cancel the drag
        if (Viewport.IsMouseCaptured)
            Viewport.ReleaseMouseCapture();
        SettleTo(result.TargetIndex);
    }

    private static bool IsBlocked(DependencyObject? source)
    {
        for (var node = source; node is not null; node = GetParent(node))
        {
            if (node is Slider or TextBoxBase or ScrollBar or ComboBox or PasswordBox or ButtonBase || GetBlocksSwipe(node))
                return true;
        }

        return false;
    }

    private bool IsInsideFocusableControl(DependencyObject? source)
    {
        for (var node = source; node is not null && !ReferenceEquals(node, this); node = GetParent(node))
        {
            if (node is Control { Focusable: true } or ButtonBase)
                return true;
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject node) =>
        node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    // ---- keyboard, buttons, dots, touchpad -------------------------------------------------------------------------

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_navigation is null || e.Handled || Keyboard.Modifiers != ModifierKeys.None)
            return;

        // Left and Right follow the reading direction; a child (slider, text box, radio button) that used the key marked it handled.
        var backward = FlowDirection == FlowDirection.RightToLeft ? Key.Right : Key.Left;
        var forward = FlowDirection == FlowDirection.RightToLeft ? Key.Left : Key.Right;
        if (e.Key == backward)
            e.Handled = _navigation.Previous();
        else if (e.Key == forward)
            e.Handled = _navigation.Next();
    }

    private void OnPreviousClick(object sender, RoutedEventArgs e) => _navigation?.Previous();

    private void OnNextClick(object sender, RoutedEventArgs e) => _navigation?.Next();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _source = PresentationSource.FromVisual(this) as HwndSource;
        _source?.AddHook(WndProc);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _source?.RemoveHook(WndProc);
        _source = null;
        _controller?.Cancel();
        _pressed = false;
        _wheel.Reset();
    }

    // Precision touchpads send horizontal scrolling as WM_MOUSEHWHEEL, which WPF does not turn into an event.
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_MOUSEHWHEEL && IsMouseOver && IsVisible)
        {
            var delta = unchecked((short)((long)wParam >> 16));
            if (WheelDelta(delta, Environment.TickCount64))
                handled = true;
        }

        return IntPtr.Zero;
    }

    /// <summary>One horizontal wheel delta. True when it caused a page change.</summary>
    internal bool WheelDelta(int delta, long timestampMs)
    {
        if (_navigation is null || _controller?.IsDragging == true)
            return false;

        return _wheel.Add(delta, timestampMs) switch
        {
            1 => _navigation.Next(),
            -1 => _navigation.Previous(),
            _ => false
        };
    }

    private void OnNavigationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_navigation is null || e.PropertyName != nameof(PageNavigationViewModel.SelectedIndex))
            return;

        for (var i = 0; i < _dots.Count; i++)
            _dots[i].IsChecked = i == _navigation.SelectedIndex;
        UpdateArrows();
        if (_controller is { IsDragging: false })
            SettleTo(_navigation.SelectedIndex);

        // Moving between pages by mouse, touch or arrow keys changes no keyboard focus, so say where the user is now.
        var index = _navigation.SelectedIndex;
        Accessibility.LiveAnnouncer.Announce(this, Lunar.Desktop.Resources.Strings.Format("Nav.PageChanged", _navigation.Titles[index], index + 1, _navigation.Titles.Count), "Lunar.Page");
    }

    private void UpdateArrows()
    {
        if (_navigation is null)
            return;
        PreviousButton.IsEnabled = _navigation.CanGoPrevious;
        NextButton.IsEnabled = _navigation.CanGoNext;
    }

    // ---- motion ---------------------------------------------------------------------------------------------------

    private void SettleTo(int index)
    {
        if (_controller is null)
            return;

        var target = -index * _controller.PageWidth;
        var from = StripOffset.X;
        var version = ++_settle;
        StripOffset.BeginAnimation(TranslateTransform.XProperty, null);
        StripOffset.X = target; // the resting value
        if (_reduceMotion || Math.Abs(from - target) < 0.5)
            return;

        var animation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(SwipeRules.SettleMilliseconds))
        {
            EasingFunction = Overshoot, // the page settles with a small spring
            FillBehavior = FillBehavior.Stop
        };
        // A newer move replaces this animation; nothing more to do when this one ends (the resting value is already set).
        if (version == _settle)
            StripOffset.BeginAnimation(TranslateTransform.XProperty, animation);
    }
}
