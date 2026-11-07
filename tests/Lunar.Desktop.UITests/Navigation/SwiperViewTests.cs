using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using Lunar.Desktop.Navigation;
using Lunar.Desktop.UITests.Themes;
using Xunit;

namespace Lunar.Desktop.UITests.Navigation;

// Real WPF controls on an STA thread. The pointer rules are driven through the same internal entry points the mouse
// events use, because a synthetic mouse event reports the real cursor position, not the one a test wants.
public sealed class SwiperViewTests
{
    private const double Width = 378;
    private const double Height = 250;

    private sealed record Rig(Window Window, SwiperView View, PageNavigationViewModel Navigation, Slider Slider, Button PageButton) : IDisposable
    {
        public void Dispose() => Window.Close();
    }

    private static ResourceDictionary Load(string name)
    {
        using var stream = File.OpenRead(System.IO.Path.Combine(ThemeTokenTests.DesktopDirectory, "Themes", name));
        return (ResourceDictionary)XamlReader.Load(stream);
    }

    private static Rig Create(bool reduceMotion = true)
    {
        var navigation = new PageNavigationViewModel(["System", "Media", "File converter", "Notes", "Usage"]);
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = 50, Name = "VolumeSlider" };
        var button = new Button { Content = "Go" };
        var pages = new FrameworkElement[]
        {
            new StackPanel { Children = { new TextBlock { Text = "one" }, slider, button } },
            new TextBlock { Text = "two" },
            new TextBlock { Text = "three" },
            new TextBlock { Text = "four" },
            new TextBlock { Text = "five" }
        };

        var view = new SwiperView();
        view.Resources.MergedDictionaries.Add(Load("Colors.Dark.xaml"));
        view.Resources.MergedDictionaries.Add(Load("Typography.xaml"));
        view.Resources.MergedDictionaries.Add(Load("Spacing.xaml"));
        view.Resources.MergedDictionaries.Add(Load("Components.xaml"));
        view.Initialize(navigation, pages, reduceMotion);

        var window = new Window { Width = Width, Height = Height + 40, Content = view, WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        window.UpdateLayout();
        return new Rig(window, view, navigation, slider, button);
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                StaDispatcher.ShutDownCurrent();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new InvalidOperationException("WPF swiper check failed.", failure);
    }

    private static void Drag(SwiperView view, double fromX, double toX, long durationMs = 500)
    {
        view.PointerPressed(fromX);
        view.PointerMoved(toX, durationMs);
        view.PointerReleased(durationMs);
    }

    private static KeyEventArgs Key(Window window, UIElement target, Key key) =>
        new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, key) { RoutedEvent = Keyboard.KeyDownEvent, Source = target };

    // ---- layout and pages ------------------------------------------------------------------------------------------

    [Fact]
    public void FivePagesSitSideBySideEachAsWideAsTheViewportAndTheFirstIsShown() => RunOnSta(() =>
    {
        using var rig = Create();

        var strip = (StackPanel)rig.View.FindName("Strip");
        Assert.Equal(5, strip.Children.Count);
        var viewport = (Grid)rig.View.FindName("Viewport");
        Assert.True(viewport.ActualWidth > 300);
        Assert.All(strip.Children.OfType<FrameworkElement>(), page => Assert.Equal(viewport.ActualWidth, page.Width, 3));
        Assert.Equal(viewport.ActualWidth * 5, strip.Width, 3);
        Assert.Equal(0, rig.View.StripX);
    });

    [Fact]
    public void PagesNeverMakeTheControlAskForMoreWidth() => RunOnSta(() =>
    {
        using var rig = Create();

        rig.View.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        // Only the arrows and the five dots ask for width (about 110 DIP), far below the island's 380 DIP; the pages never do.
        Assert.True(rig.View.DesiredSize.Width < 150, $"Desired width was {rig.View.DesiredSize.Width}.");
    });

    [Fact]
    public void InitializeValidatesAndOnlyRunsOnce() => RunOnSta(() =>
    {
        var view = new SwiperView();
        var navigation = new PageNavigationViewModel(["a", "b", "c", "d", "e"]);

        Assert.Throws<ArgumentNullException>(() => view.Initialize(null!, [], true));
        Assert.Throws<ArgumentNullException>(() => view.Initialize(navigation, null!, true));
        Assert.Throws<ArgumentException>(() => view.Initialize(navigation, [new Border()], true));
    });

    // ---- swiping ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ADragLeftPastTheThresholdShowsTheNextPageAndRightGoesBack() => RunOnSta(() =>
    {
        using var rig = Create();
        var width = ((Grid)rig.View.FindName("Viewport")).ActualWidth;

        Drag(rig.View, 300, 200);
        Assert.Equal(1, rig.Navigation.SelectedIndex);
        Assert.Equal(-width, rig.View.StripX, 3);

        Drag(rig.View, 100, 250);
        Assert.Equal(0, rig.Navigation.SelectedIndex);
        Assert.Equal(0, rig.View.StripX, 3);
    });

    [Fact]
    public void AShortDragSnapsBackAndAClickStaysAClick() => RunOnSta(() =>
    {
        using var rig = Create();

        Drag(rig.View, 300, 270); // 30 DIP, slow
        Assert.Equal(0, rig.Navigation.SelectedIndex);
        Assert.Equal(0, rig.View.StripX, 3);

        rig.View.PointerPressed(300);
        rig.View.PointerMoved(298, 10); // inside the slop
        Assert.False(rig.View.IsDragging);
        rig.View.PointerReleased(20);
        Assert.Equal(0, rig.Navigation.SelectedIndex);
    });

    [Fact]
    public void TheContentFollowsTheFingerWhileDraggingAndStretchesAtTheEnds() => RunOnSta(() =>
    {
        using var rig = Create();

        rig.View.PointerPressed(100);
        rig.View.PointerMoved(40, 400);
        Assert.True(rig.View.IsDragging);
        Assert.Equal(-60, rig.View.StripX, 3); // follows the finger

        rig.View.PointerMoved(300, 800); // past the first page to the right
        Assert.True(rig.View.StripX > 0 && rig.View.StripX < 200);
        rig.View.PointerReleased(900);
        Assert.Equal(0, rig.Navigation.SelectedIndex);
        Assert.Equal(0, rig.View.StripX, 3);
    });

    [Fact]
    public void TheIndexStaysInRangeWhateverIsDragged() => RunOnSta(() =>
    {
        using var rig = Create();
        var random = new Random(3);

        for (var i = 0; i < 200; i++)
        {
            var from = random.Next(0, 378);
            Drag(rig.View, from, from + random.Next(-400, 400), random.Next(10, 600));
            Assert.InRange(rig.Navigation.SelectedIndex, 0, 4);
        }
    });

    [Fact]
    public void ADragThatStartsOnABlockerDoesNotMoveThePages() => RunOnSta(() =>
    {
        using var rig = Create();

        rig.View.PointerPressed(300, onBlocker: true);
        rig.View.PointerMoved(100, 300);
        rig.View.PointerReleased(400);

        Assert.False(rig.View.IsDragging);
        Assert.Equal(0, rig.Navigation.SelectedIndex);
        Assert.Equal(0, rig.View.StripX);
    });

    [Fact]
    public void RealMouseDownOnASliderOrAnElementMarkedAsBlockingNeverStartsADrag() => RunOnSta(() =>
    {
        using var rig = Create();
        var marked = new Border();
        SwiperView.SetBlocksSwipe(marked, true);
        Assert.True(SwiperView.GetBlocksSwipe(marked));

        foreach (var source in new FrameworkElement[] { rig.Slider, rig.PageButton, marked })
        {
            var viewport = (Grid)rig.View.FindName("Viewport");
            // Raise it on the viewport as if the source was the original source inside it.
            viewport.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
                Source = source
            });
            rig.View.PointerMoved(100, 300); // would drag if a press had been accepted
            Assert.False(rig.View.IsDragging);
        }

        Assert.Equal(0, rig.Navigation.SelectedIndex);
    });

    [Fact]
    public void SettlingIsAnimatedUnlessReducedMotionIsOn() => RunOnSta(() =>
    {
        using var animated = Create(reduceMotion: false);
        animated.Navigation.Next();
        var transform = (TranslateTransform)((StackPanel)animated.View.FindName("Strip")).RenderTransform;
        Assert.True(transform.HasAnimatedProperties);
        Assert.Equal(-((Grid)animated.View.FindName("Viewport")).ActualWidth, transform.X, 3); // the resting value is already the target

        using var instant = Create(reduceMotion: true);
        instant.Navigation.Next();
        Assert.False(((TranslateTransform)((StackPanel)instant.View.FindName("Strip")).RenderTransform).HasAnimatedProperties);
    });

    [Fact]
    public void TheMotionSettingCanChangeWhileTheSwiperRuns() => RunOnSta(() =>
    {
        using var rig = Create(reduceMotion: true);
        var strip = (StackPanel)rig.View.FindName("Strip");

        rig.View.SetReduceMotion(false);
        rig.Navigation.Next();
        Assert.True(((TranslateTransform)strip.RenderTransform).HasAnimatedProperties);

        rig.View.SetReduceMotion(true);
        rig.Navigation.Next();
        Assert.False(((TranslateTransform)strip.RenderTransform).HasAnimatedProperties);
    });

    // ---- keyboard, buttons, dots -----------------------------------------------------------------------------------

    [Fact]
    public void LeftAndRightArrowKeysChangeThePageAndStopAtTheEnds() => RunOnSta(() =>
    {
        using var rig = Create();

        var right = Key(rig.Window, rig.View, System.Windows.Input.Key.Right);
        rig.View.RaiseEvent(right);
        Assert.True(right.Handled);
        Assert.Equal(1, rig.Navigation.SelectedIndex);

        rig.View.RaiseEvent(Key(rig.Window, rig.View, System.Windows.Input.Key.Right));
        rig.View.RaiseEvent(Key(rig.Window, rig.View, System.Windows.Input.Key.Right));
        rig.View.RaiseEvent(Key(rig.Window, rig.View, System.Windows.Input.Key.Right));
        var past = Key(rig.Window, rig.View, System.Windows.Input.Key.Right);
        rig.View.RaiseEvent(past);
        Assert.False(past.Handled); // nothing to do at the end: the key is left for others
        Assert.Equal(4, rig.Navigation.SelectedIndex);

        rig.View.RaiseEvent(Key(rig.Window, rig.View, System.Windows.Input.Key.Left));
        Assert.Equal(3, rig.Navigation.SelectedIndex);
    });

    [Fact]
    public void AFocusedSliderKeepsItsArrowKeys() => RunOnSta(() =>
    {
        using var rig = Create();
        var before = rig.Slider.Value;

        var right = Key(rig.Window, rig.Slider, System.Windows.Input.Key.Right);
        rig.Slider.RaiseEvent(right); // bubbles from the slider up to the swiper

        Assert.True(rig.Slider.Value > before); // the slider used the key
        Assert.True(right.Handled);
        Assert.Equal(0, rig.Navigation.SelectedIndex); // so the page did not change
    });

    [Fact]
    public void AKeyThatAnotherElementAlreadyHandledIsLeftAlone() => RunOnSta(() =>
    {
        using var rig = Create();
        var handled = Key(rig.Window, rig.View, System.Windows.Input.Key.Right);
        handled.Handled = true;

        rig.View.RaiseEvent(handled);

        Assert.Equal(0, rig.Navigation.SelectedIndex);
    });

    [Fact]
    public void ArrowChevronsAreBoldDrawnShapesCentredInTheirCircle() => RunOnSta(() =>
    {
        using var rig = Create();
        rig.Window.UpdateLayout();
        foreach (var (buttonName, chevronName) in new[] { ("PreviousButton", "PreviousChevron"), ("NextButton", "NextChevron") })
        {
            var button = (Button)rig.View.FindName(buttonName);
            var chevron = (System.Windows.Shapes.Path)rig.View.FindName(chevronName);

            Assert.True(chevron.StrokeThickness >= 2.2, $"{chevronName} stroke {chevron.StrokeThickness}");
            Assert.Equal(PenLineCap.Round, chevron.StrokeStartLineCap);
            Assert.NotNull(chevron.Stroke);
            Assert.Null(button.Content as string);

            // The chevron's box sits in the middle of the round face, both ways (within half a device pixel).
            var centre = chevron.TranslatePoint(new Point(chevron.ActualWidth / 2, chevron.ActualHeight / 2), button);
            Assert.InRange(centre.X, (button.ActualWidth / 2) - 0.5, (button.ActualWidth / 2) + 0.5);
            Assert.InRange(centre.Y, (button.ActualHeight / 2) - 0.5, (button.ActualHeight / 2) + 0.5);
            Assert.True(chevron.ActualHeight <= button.ActualHeight * 0.6);
        }
    });

    [Fact]
    public void ArrowButtonsFollowTheNavigationAndNeverTakeFocus() => RunOnSta(() =>
    {
        using var rig = Create();
        var previous = (Button)rig.View.FindName("PreviousButton");
        var next = (Button)rig.View.FindName("NextButton");

        Assert.False(previous.Focusable);
        Assert.False(next.Focusable);
        Assert.False(previous.IsEnabled);
        Assert.True(next.IsEnabled);

        next.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.Equal(1, rig.Navigation.SelectedIndex);
        Assert.True(previous.IsEnabled);

        rig.Navigation.GoTo(4);
        Assert.False(next.IsEnabled);
        previous.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.Equal(3, rig.Navigation.SelectedIndex);
    });

    [Fact]
    public void DotsAreAccessibleTabsThatFollowAndSelectPages() => RunOnSta(() =>
    {
        using var rig = Create();
        var dots = ((StackPanel)rig.View.FindName("Dots")).Children.OfType<RadioButton>().ToList();

        Assert.Equal(5, dots.Count);
        Assert.Equal(["System page", "Media page", "File converter page", "Notes page", "Usage page"], dots.Select(System.Windows.Automation.AutomationProperties.GetName));
        Assert.Equal([true, false, false, false, false], dots.Select(d => d.IsChecked == true));

        dots[2].IsChecked = true; // clicking a dot, or arrowing to it with the keyboard
        Assert.Equal(2, rig.Navigation.SelectedIndex);

        rig.Navigation.GoTo(1);
        Assert.Equal([false, true, false, false, false], dots.Select(d => d.IsChecked == true));
    });

    // ---- touchpad --------------------------------------------------------------------------------------------------

    [Fact]
    public void AHorizontalTouchpadSwipeMovesOnePageAndIsIgnoredWhileDragging() => RunOnSta(() =>
    {
        using var rig = Create();

        Assert.False(rig.View.WheelDelta(60, 0));
        Assert.True(rig.View.WheelDelta(60, 20));
        Assert.Equal(1, rig.Navigation.SelectedIndex);
        Assert.False(rig.View.WheelDelta(120, 40)); // the rest of the same swipe
        Assert.Equal(1, rig.Navigation.SelectedIndex);

        Assert.True(rig.View.WheelDelta(-120, 1000)); // a new swipe back
        Assert.Equal(0, rig.Navigation.SelectedIndex);

        rig.View.PointerPressed(300);
        rig.View.PointerMoved(100, 500);
        Assert.False(rig.View.WheelDelta(240, 2000)); // not while a drag is running
        rig.View.PointerReleased(600);
    });

    [Fact]
    public void ThereIsNoVerticalWheelHandlingSoSlidersKeepTheirWheel() => RunOnSta(() =>
    {
        using var rig = Create();

        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = UIElement.MouseWheelEvent, Source = rig.Slider };
        rig.Slider.RaiseEvent(wheel);

        Assert.Equal(0, rig.Navigation.SelectedIndex);
    });

    [Fact]
    public void UnloadingTheViewEndsADragWithoutChangingThePage() => RunOnSta(() =>
    {
        var rig = Create();
        rig.View.PointerPressed(300);
        rig.View.PointerMoved(100, 300);
        Assert.True(rig.View.IsDragging);

        rig.Window.Content = null; // removes the view: Unloaded
        rig.Window.UpdateLayout();
        rig.Window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Loaded, () => { });

        Assert.False(rig.View.IsDragging);
        Assert.Equal(0, rig.Navigation.SelectedIndex);
        rig.Window.Close();
    });
}
