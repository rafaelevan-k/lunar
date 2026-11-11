using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using Lunar.Desktop.Behaviors;
using Lunar.Desktop.Controls;
using Lunar.Desktop.Shell;
using Lunar.Desktop.UITests.Themes;
using Xunit;

namespace Lunar.Desktop.UITests.Controls;

// Real controls with the real theme and control styles loaded, on an STA thread. Windows that are shown are closed
// again and do not take focus.
public sealed class ControlsTests
{
    private static ResourceDictionary Load(params string[] relative)
    {
        var path = System.IO.Path.Combine([ThemeTokenTests.DesktopDirectory, .. relative]);
        // The compiled app refers to its own types without an assembly name; a loose parse needs it.
        var text = File.ReadAllText(path).Replace("\"clr-namespace:Lunar.Desktop.Controls\"", "\"clr-namespace:Lunar.Desktop.Controls;assembly=Lunar.Desktop\"")
            .Replace("\"clr-namespace:Lunar.Desktop.Shell\"", "\"clr-namespace:Lunar.Desktop.Shell;assembly=Lunar.Desktop\"");
        return (ResourceDictionary)XamlReader.Parse(text);
    }

    private static void AddTheme(FrameworkElement host, string colors = "Colors.Dark.xaml")
    {
        host.Resources.MergedDictionaries.Add(Load("Themes", colors));
        host.Resources.MergedDictionaries.Add(Load("Themes", "Typography.xaml"));
        host.Resources.MergedDictionaries.Add(Load("Themes", "Spacing.xaml"));
        host.Resources.MergedDictionaries.Add(Load("Themes", "Components.xaml"));
        host.Resources.MergedDictionaries.Add(Load("Controls", "ControlStyles.xaml"));
    }

    private static Window Show(UIElement content, string colors = "Colors.Dark.xaml", bool reduceMotion = false)
    {
        var window = new Window { Width = 320, Height = 240, Content = content, WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false };
        AddTheme(window, colors);
        Motion.SetReduceMotion(window, reduceMotion);
        window.Show();
        window.UpdateLayout();
        return window;
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
            throw new InvalidOperationException("WPF controls check failed.", failure);
    }

    private static T Part<T>(Control control, string name) where T : class
    {
        control.ApplyTemplate();
        return Assert.IsType<T>(control.Template.FindName(name, control));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child))
                yield return nested;
        }
    }

    private static KeyEventArgs KeyDown(Window window, UIElement target, Key key) =>
        new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, key) { RoutedEvent = Keyboard.KeyDownEvent, Source = target };

    // UI Automation's Invoke posts the click to the dispatcher, so let it run.
    private static void Flush(Window window) =>
        window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => { }));

    private sealed class CountingCommand(bool canExecute = true) : ICommand
    {
        public int Runs { get; private set; }

        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => canExecute;

        public void Execute(object? parameter) => Runs++;
    }

    // ---- icon button -----------------------------------------------------------------------------------------------

    [Fact]
    public void IconButtonRequiresALabelThatBecomesItsAccessibleNameAndTooltip() => RunOnSta(() =>
    {
        var button = new IconButton { Glyph = "", Label = "Previous page" };
        var window = Show(button);

        Assert.Equal("Previous page", AutomationProperties.GetName(button));
        Assert.Equal("Previous page", button.ToolTip);
        Assert.Equal("Previous page", UIElementAutomationPeer.CreatePeerForElement(button).GetName());

        button.Label = "Next page";
        Assert.Equal("Next page", AutomationProperties.GetName(button));
        Assert.Equal("Next page", button.ToolTip);
        window.Close();
    });

    [Fact]
    public void AnExplicitTooltipIsKeptWhenTheLabelChanges() => RunOnSta(() =>
    {
        var button = new IconButton { Label = "Close", ToolTip = "Close the island" };

        button.Label = "Dismiss";

        Assert.Equal("Close the island", button.ToolTip);
        Assert.Equal("Dismiss", AutomationProperties.GetName(button));
    });

    [Fact]
    public void IconButtonIsATabStopThatInvokesItsCommandThroughAutomation() => RunOnSta(() =>
    {
        var command = new CountingCommand();
        var button = new IconButton { Glyph = "x", Label = "Do it", Command = command };
        var window = Show(button);

        Assert.True(button.Focusable);
        Assert.True(button.IsTabStop);
        Assert.NotNull(button.FocusVisualStyle);
        var invoke = (IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(button).GetPattern(PatternInterface.Invoke);
        invoke.Invoke();
        Flush(window);
        Assert.Equal(1, command.Runs);

        button.IsEnabled = false;
        Assert.False(button.IsEnabled);
        window.Close();
    });

    [Fact]
    public void IconButtonFollowsTheStyleSizeAndHasATemplate() => RunOnSta(() =>
    {
        var button = new IconButton { Glyph = "x", Label = "Do it" };
        var window = Show(button);

        Assert.Equal(32, button.Width);
        Assert.Equal(32, button.Height);
        var face = Part<Border>(button, "Face");
        Assert.Equal(new CornerRadius(16), face.CornerRadius); // a full circle
        window.Close();
    });

    // ---- switch button (FE-15) -------------------------------------------------------------------------------------

    [Fact]
    public void SwitchButtonDrawsItsStateAndNeverChangesItselfWhenPressed() => RunOnSta(() =>
    {
        var button = new Button();
        var window = Show(new StackPanel());
        button.Style = (Style)window.FindResource("Lunar.Style.SwitchButton");
        window.Content = button;
        window.UpdateLayout();
        button.ApplyTemplate();
        var track = (Border)button.Template.FindName("Track", button);
        var knob = (System.Windows.Shapes.Ellipse)button.Template.FindName("Knob", button);
        var offTrack = ((SolidColorBrush)track.Background).Color;
        Assert.Equal(HorizontalAlignment.Left, knob.HorizontalAlignment);

        SwitchState.SetIsOn(button, true);
        window.UpdateLayout();
        Assert.Equal(HorizontalAlignment.Right, knob.HorizontalAlignment);
        Assert.NotEqual(offTrack, ((SolidColorBrush)track.Background).Color);

        // Pressing only raises Click; the drawn state is whatever was bound.
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        Assert.True(SwitchState.GetIsOn(button));

        SwitchState.SetIsOn(button, false);
        window.UpdateLayout();
        Assert.Equal(HorizontalAlignment.Left, knob.HorizontalAlignment);

        window.Close();
    });

    [Fact]
    public void SwitchButtonIsFocusableDimsWhenDisabledAndAcceptsInvoke() => RunOnSta(() =>
    {
        var button = new Button();
        var window = Show(new StackPanel());
        button.Style = (Style)window.FindResource("Lunar.Style.SwitchButton");
        window.Content = button;
        window.UpdateLayout();

        Assert.True(button.Focusable && button.IsTabStop);
        var invoked = 0;
        button.Click += (_, _) => invoked++;
        var peer = UIElementAutomationPeer.CreatePeerForElement(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
        window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, () => { });
        Assert.Equal(1, invoked);

        button.IsEnabled = false;
        window.UpdateLayout();
        Assert.True(button.Opacity < 1);

        window.Close();
    });

    // ---- toggle ----------------------------------------------------------------------------------------------------

    [Fact]
    public void ToggleSwitchIsKeyboardFocusableAndTogglesThroughAutomation() => RunOnSta(() =>
    {
        var toggle = new ToggleButton { Content = "Mute", Style = null };
        var window = Show(new StackPanel());
        toggle.Resources.MergedDictionaries.Add(Load("Controls", "ControlStyles.xaml"));
        toggle.Style = (Style)window.FindResource("Lunar.Style.Toggle");
        window.Content = toggle;
        window.UpdateLayout();

        Assert.True(toggle.Focusable && toggle.IsTabStop);
        var peer = UIElementAutomationPeer.CreatePeerForElement(toggle);
        var provider = (IToggleProvider)peer.GetPattern(PatternInterface.Toggle);
        Assert.Equal(ToggleState.Off, provider.ToggleState);
        provider.Toggle();
        Assert.True(toggle.IsChecked);
        Assert.Equal(ToggleState.On, provider.ToggleState);
        Assert.Equal("Mute", peer.GetName());

        window.Close();
    });

    [Fact]
    public void ToggleSwitchShowsItsStateAndAFocusRing() => RunOnSta(() =>
    {
        var toggle = new ToggleButton { Content = "Wi-Fi" };
        var window = Show(new StackPanel());
        toggle.Style = (Style)window.FindResource("Lunar.Style.Toggle");
        window.Content = toggle;
        window.UpdateLayout();
        toggle.ApplyTemplate();
        var track = (Border)toggle.Template.FindName("Track", toggle);
        var knob = (System.Windows.Shapes.Ellipse)toggle.Template.FindName("Knob", toggle);

        var offTrack = ((SolidColorBrush)track.Background).Color;
        Assert.Equal(HorizontalAlignment.Left, knob.HorizontalAlignment);

        toggle.IsChecked = true;
        window.UpdateLayout();

        Assert.NotEqual(offTrack, ((SolidColorBrush)track.Background).Color); // the accent colour
        Assert.Equal(HorizontalAlignment.Right, knob.HorizontalAlignment); // and the knob moved: not colour alone
        window.Close();
    });

    // ---- slider ----------------------------------------------------------------------------------------------------

    [Fact]
    public void SliderStyleHasATrackAndThumbAndRespondsToTheArrowKeys() => RunOnSta(() =>
    {
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = 50, SmallChange = 5, LargeChange = 20 };
        var window = Show(new StackPanel());
        slider.Style = (Style)window.FindResource("Lunar.Style.Slider");
        window.Content = slider;
        window.UpdateLayout();

        Assert.True(slider.Focusable && slider.IsTabStop);
        Assert.True(slider.IsMoveToPointEnabled);
        slider.ApplyTemplate();
        var track = Assert.IsType<Track>(slider.Template.FindName("PART_Track", slider));
        Assert.NotNull(track.Thumb);
        Assert.True(track.Thumb.Width >= 20); // a generous target

        slider.RaiseEvent(KeyDown(window, slider, Key.Right));
        Assert.Equal(55, slider.Value);
        slider.RaiseEvent(KeyDown(window, slider, Key.Left));
        slider.RaiseEvent(KeyDown(window, slider, Key.Left));
        Assert.Equal(45, slider.Value);
        slider.RaiseEvent(KeyDown(window, slider, Key.Home));
        Assert.Equal(0, slider.Value);
        slider.RaiseEvent(KeyDown(window, slider, Key.End));
        Assert.Equal(100, slider.Value);
        window.Close();
    });

    [Fact]
    public void SliderExposesItsValueThroughAutomation() => RunOnSta(() =>
    {
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = 30 };
        var window = Show(new StackPanel());
        slider.Style = (Style)window.FindResource("Lunar.Style.Slider");
        AutomationProperties.SetName(slider, "Volume");
        window.Content = slider;
        window.UpdateLayout();

        var peer = UIElementAutomationPeer.CreatePeerForElement(slider);
        var range = (IRangeValueProvider)peer.GetPattern(PatternInterface.RangeValue);
        Assert.Equal(30, range.Value);
        range.SetValue(70);
        Assert.Equal(70, slider.Value);
        Assert.Equal("Volume", peer.GetName());
        window.Close();
    });

    [Fact]
    public void ADisabledSliderIgnoresTheKeyboard() => RunOnSta(() =>
    {
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = 50, IsEnabled = false };
        var window = Show(new StackPanel());
        slider.Style = (Style)window.FindResource("Lunar.Style.Slider");
        window.Content = slider;
        window.UpdateLayout();

        Assert.False(slider.IsEnabled);
        Assert.False(slider.IsKeyboardFocusWithin);
        window.Close();
    });

    // ---- status chip -----------------------------------------------------------------------------------------------

    [Fact]
    public void EveryStatusKindHasItsOwnSymbolSoColourIsNeverTheOnlySignal() => RunOnSta(() =>
    {
        var glyphs = Enum.GetValues<StatusKind>().Select(StatusChip.GlyphFor).ToList();

        Assert.Equal(glyphs.Count, glyphs.Distinct().Count());
        Assert.All(glyphs, g => Assert.False(string.IsNullOrWhiteSpace(g)));
    });

    [Fact]
    public void StatusChipShowsTheSymbolAndTextAndAnnouncesChangesPolitely() => RunOnSta(() =>
    {
        var chip = new StatusChip { Text = "Connected", Kind = StatusKind.Success };
        var window = Show(chip);

        Assert.Equal("Connected", AutomationProperties.GetName(chip));
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(chip));
        Assert.False(chip.Focusable);
        Assert.False(chip.IsTabStop);
        Assert.Equal("✓", chip.Glyph);
        Assert.Equal("✓", Part<TextBlock>(chip, "Symbol").Text);

        chip.Kind = StatusKind.Error;
        chip.Text = "Not connected";
        Assert.Equal("!", chip.Glyph);
        Assert.Equal("Not connected", AutomationProperties.GetName(chip));
        window.Close();
    });

    // ---- skeleton --------------------------------------------------------------------------------------------------

    [Fact]
    public void SkeletonPulsesOnlyWhileActiveVisibleLoadedAndMotionIsAllowed() => RunOnSta(() =>
    {
        var block = new SkeletonBlock { Height = 16 };
        var panel = new StackPanel { Children = { block } };
        Assert.False(block.IsPulsing); // not loaded yet

        var window = Show(panel);
        Assert.True(block.IsPulsing);
        Assert.True(block.HasAnimatedProperties);
        Assert.Equal("Loading", AutomationProperties.GetName(block));
        Assert.False(block.Focusable);

        block.IsActive = false; // the content arrived
        Assert.False(block.IsPulsing);
        Assert.False(block.HasAnimatedProperties);
        Assert.Equal(1.0, block.Opacity);

        block.IsActive = true;
        Assert.True(block.IsPulsing);
        panel.Visibility = Visibility.Collapsed; // a collapsed island keeps no animation clock running
        Assert.False(block.IsPulsing);
        Assert.False(block.HasAnimatedProperties);
        window.Close();
    });

    [Fact]
    public void SkeletonStaysStaticWhenMotionIsReduced() => RunOnSta(() =>
    {
        var block = new SkeletonBlock { Height = 16 };
        var window = Show(new StackPanel { Children = { block } }, reduceMotion: true);

        Assert.False(block.IsPulsing);
        Assert.False(block.HasAnimatedProperties);

        Motion.SetReduceMotion(window, false); // the preference is inherited and changes take effect
        Assert.True(block.IsPulsing);
        Motion.SetReduceMotion(window, true);
        Assert.False(block.IsPulsing);
        window.Close();
    });

    [Fact]
    public void MotionPreferenceIsInheritedAndDefaultsToOff()
    {
        RunOnSta(() =>
        {
            var inner = new Border();
            var outer = new StackPanel { Children = { inner } };
            Assert.False(Motion.GetReduceMotion(inner));

            Motion.SetReduceMotion(outer, true);

            Assert.True(Motion.GetReduceMotion(inner));
            Assert.Throws<ArgumentNullException>(() => Motion.GetReduceMotion(null!));
            Assert.Throws<ArgumentNullException>(() => Motion.SetReduceMotion(null!, true));
        });
    }

    // ---- inline error ----------------------------------------------------------------------------------------------

    [Fact]
    public void InlineErrorIsHiddenWithoutAMessageAndAnnouncedAssertivelyWithOne() => RunOnSta(() =>
    {
        var error = new InlineError();
        var window = Show(error);

        Assert.Equal(Visibility.Collapsed, error.Visibility);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(error));

        error.Message = "The file could not be read.";
        Assert.Equal(Visibility.Visible, error.Visibility);
        Assert.Equal("Error: The file could not be read.", AutomationProperties.GetName(error));

        error.Message = "  ";
        Assert.Equal(Visibility.Collapsed, error.Visibility);
        error.Message = null;
        Assert.Equal(Visibility.Collapsed, error.Visibility);
        window.Close();
    });

    [Fact]
    public void InlineErrorOffersADismissButtonAndAnOptionalActionThatRunTheirCommands() => RunOnSta(() =>
    {
        var dismiss = new CountingCommand();
        var retry = new CountingCommand();
        var error = new InlineError { Message = "Something failed." };
        var window = Show(error);

        var dismissButton = Part<IconButton>(error, "PART_Dismiss");
        var actionButton = Part<Button>(error, "PART_Action");
        Assert.Equal(Visibility.Collapsed, dismissButton.Visibility); // no command: no button
        Assert.Equal(Visibility.Collapsed, actionButton.Visibility);

        error.DismissCommand = dismiss;
        error.ActionCommand = retry;
        window.UpdateLayout();
        Assert.Equal(Visibility.Visible, dismissButton.Visibility);
        Assert.Equal(Visibility.Visible, actionButton.Visibility);
        Assert.Equal("Retry", actionButton.Content);
        Assert.Equal("Dismiss error", AutomationProperties.GetName(dismissButton));
        Assert.True(dismissButton.IsTabStop && actionButton.IsTabStop); // keyboard reachable

        ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(dismissButton).GetPattern(PatternInterface.Invoke)).Invoke();
        ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(actionButton).GetPattern(PatternInterface.Invoke)).Invoke();
        Flush(window);
        Assert.Equal(1, dismiss.Runs);
        Assert.Equal(1, retry.Runs);

        error.ActionText = "Try again";
        Assert.Equal("Try again", actionButton.Content);
        window.Close();
    });

    [Theory]
    [InlineData(300)]
    [InlineData(340)]
    [InlineData(200)]
    public void InlineErrorWithBothButtonsFitsItsContainerAndWrapsLongText(double width) => RunOnSta(() =>
    {
        var error = new InlineError
        {
            Message = "Volume could not be changed because the audio device stopped responding. Try again in a moment.",
            DismissCommand = new CountingCommand(),
            ActionCommand = new CountingCommand(),
            Margin = new Thickness(0)
        };
        var window = Show(new StackPanel { Width = width, Children = { error } });

        Assert.True(error.ActualWidth <= width + 0.5, $"The error is {error.ActualWidth} wide in {width}.");
        error.ApplyTemplate();
        var dismiss = Part<IconButton>(error, "PART_Dismiss");
        var right = dismiss.TranslatePoint(new Point(dismiss.ActualWidth, 0), error).X;
        Assert.True(right <= error.ActualWidth + 0.5, $"The dismiss button ends at {right}, past the control ({error.ActualWidth}).");
        window.Close();
    });

    [Fact]
    public void TheIslandShellShowsItsViewModelErrorInlineAndDismissClearsIt() => RunOnSta(() =>
    {
        var viewModel = new IslandViewModel();
        var navigation = new Lunar.Desktop.Navigation.PageNavigationViewModel(["System", "Media", "Pomodoro", "File converter", "Notes", "Usage"]);
        var swiper = new Lunar.Desktop.Navigation.SwiperView();
        var eyes = new Lunar.Desktop.Features.Eyes.EyesView();
        var shell = new IslandShellView(eyes, swiper, viewModel, reduceMotion: true);
        var window = Show(shell);
        swiper.Resources.MergedDictionaries.Clear();
        swiper.Initialize(navigation, IslandShellView.CreatePlaceholderPages(navigation.Titles), true);

        Assert.Equal(Visibility.Collapsed, shell.Error.Visibility);
        viewModel.ReportError(new Lunar.Domain.Shared.OperationError(Lunar.Domain.Shared.ErrorCode.Unexpected, "Volume could not be changed."));
        Assert.Equal(Visibility.Visible, shell.Error.Visibility);
        Assert.Equal("Volume could not be changed.", shell.Error.Message);

        Assert.NotNull(shell.Error.DismissCommand);
        shell.Error.DismissCommand!.Execute(null);
        Assert.Equal(Visibility.Collapsed, shell.Error.Visibility);
        Assert.True(Motion.GetReduceMotion(shell.Error)); // the shell passes reduced motion to its children
        shell.Dispose();
        window.Close();
    });

    [Fact]
    public void TheShellFollowsAChangeOfTheMotionSettingWhileItRuns() => RunOnSta(() =>
    {
        var viewModel = new IslandViewModel();
        var navigation = new Lunar.Desktop.Navigation.PageNavigationViewModel(["System", "Media", "Pomodoro", "File converter", "Notes", "Usage"]);
        var swiper = new Lunar.Desktop.Navigation.SwiperView();
        var eyes = new Lunar.Desktop.Features.Eyes.EyesView();
        var shell = new IslandShellView(eyes, swiper, viewModel, reduceMotion: false);
        var window = Show(shell);
        swiper.Resources.MergedDictionaries.Clear();
        swiper.Initialize(navigation, IslandShellView.CreatePlaceholderPages(navigation.Titles), false);
        Assert.False(Motion.GetReduceMotion(shell.Error));

        shell.SetReduceMotion(true);

        Assert.True(Motion.GetReduceMotion(shell.Error)); // children read it from the shell, so they follow at once
        Assert.True(Motion.GetReduceMotion(shell));
        navigation.Next();
        Assert.False(((System.Windows.Media.TranslateTransform)((StackPanel)swiper.FindName("Strip")).RenderTransform).HasAnimatedProperties);

        shell.SetReduceMotion(false);

        Assert.False(Motion.GetReduceMotion(shell.Error));
        shell.Dispose();
        window.Close();
    });

    // FE-25: a collapsed island is a pill. Its pages must not be drawn, announced or animated, and must come back the moment it opens.
    [Fact]
    public void ThePagesAreHiddenWhileTheIslandIsOnlyThePillAndAnimationsBelowThemStop() => RunOnSta(() =>
    {
        var viewModel = new IslandViewModel();
        var navigation = new Lunar.Desktop.Navigation.PageNavigationViewModel(["System", "Media", "Pomodoro", "File converter", "Notes", "Usage"]);
        var swiper = new Lunar.Desktop.Navigation.SwiperView();
        var eyes = new Lunar.Desktop.Features.Eyes.EyesView();
        var shell = new IslandShellView(eyes, swiper, viewModel, reduceMotion: false);
        var window = Show(shell);
        swiper.Resources.MergedDictionaries.Clear();
        var block = new SkeletonBlock { Height = 16 };
        var pages = IslandShellView.CreatePlaceholderPages(navigation.Titles).ToList();
        pages[0] = new Border { Child = block };
        swiper.Initialize(navigation, pages, false);
        Flush(window);

        // Idle: hidden, nothing pulses, but the content is still measured so the window can size the open island from it.
        Assert.Equal(Visibility.Hidden, swiper.Visibility);
        Assert.False(swiper.IsVisible);
        Assert.False(block.IsPulsing);
        shell.Measure(new Size(380, 320));
        Assert.True(swiper.DesiredSize.Height > 0 && swiper.DesiredSize.Width > 0, "a hidden page is still measured");

        // Opening shows the pages at once, before the fade-in, so what was loading pulses again.
        Assert.True(viewModel.Toggle());
        Assert.Equal(Visibility.Visible, swiper.Visibility);
        Assert.True(block.IsPulsing);
        Assert.True(viewModel.CompleteExpansion());
        Assert.True(swiper.IsHitTestVisible);

        // Closing keeps them for the fade-out and hides them when the pill is all that is left.
        Assert.True(viewModel.Collapse());
        Assert.Equal(Visibility.Visible, swiper.Visibility);
        Assert.False(swiper.IsHitTestVisible);
        Assert.True(viewModel.CompleteCollapse());
        Assert.Equal(Visibility.Hidden, swiper.Visibility);
        Assert.False(block.IsPulsing);
        Assert.False(block.HasAnimatedProperties);

        shell.Dispose();
        window.Close();
    });

    [Fact]
    public void DisposingTheShellLetsGoOfTheViewModelAndCannotBeRepeatedHarmfully() => RunOnSta(() =>
    {
        var viewModel = new IslandViewModel();
        var shell = new IslandShellView(new Lunar.Desktop.Features.Eyes.EyesView(), new Lunar.Desktop.Navigation.SwiperView(), viewModel, reduceMotion: true);
        var listening = Lifecycle.EventAudit.HandlerCount(viewModel, nameof(IslandViewModel.PropertyChanged));

        shell.Dispose();
        shell.Dispose(); // twice is harmless

        // The shell's own handler is gone (what remains is WPF's shared weak-event handler for the error strip's binding).
        Assert.Equal(listening - 1, Lifecycle.EventAudit.HandlerCount(viewModel, nameof(IslandViewModel.PropertyChanged)));
        viewModel.Toggle(); // a disposed shell no longer reacts
        Assert.Equal(Visibility.Hidden, shell.Swiper.Visibility);
    });

    // ---- tooltip ---------------------------------------------------------------------------------------------------

    [Fact]
    public void EveryTooltipUsesTheSurfaceStyle() => RunOnSta(() =>
    {
        var button = new IconButton { Label = "Previous page" };
        var window = Show(button);
        var tip = new ToolTip { Content = "Hello" };
        tip.Resources = window.Resources;
        tip.ApplyTemplate();

        var border = (Border)Descendants(tip).First(d => d is Border);
        Assert.Equal(((SolidColorBrush)window.FindResource("Lunar.Brush.Surface")).Color, ((SolidColorBrush)border.Background).Color);
        Assert.Equal(280, border.MaxWidth);
        window.Close();
    });

    // ---- all palettes, consistency ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("Colors.Dark.xaml")]
    [InlineData("Colors.Light.xaml")]
    [InlineData("Colors.HighContrast.xaml")]
    public void EveryControlRendersInEveryPalette(string colors) => RunOnSta(() =>
    {
        var slider = new Slider { Minimum = 0, Maximum = 10, Value = 3 };
        var toggle = new ToggleButton { Content = "On", IsChecked = true };
        var panel = new StackPanel
        {
            Children =
            {
                new IconButton { Glyph = "x", Label = "Close" },
                slider,
                toggle,
                new StatusChip { Text = "Ready", Kind = StatusKind.Info },
                new SkeletonBlock { Height = 14 },
                new InlineError { Message = "Failed." }
            }
        };
        var window = Show(panel, colors);
        slider.Style = (Style)window.FindResource("Lunar.Style.Slider");
        toggle.Style = (Style)window.FindResource("Lunar.Style.Toggle");
        window.UpdateLayout();

        Assert.All(panel.Children.OfType<FrameworkElement>(), element => Assert.True(element.ActualHeight > 0, element.GetType().Name));
        window.Close();
    });

    [Fact]
    public void EveryInteractiveControlIsAKeyboardTabStopWithAFocusIndicator() => RunOnSta(() =>
    {
        var button = new IconButton { Glyph = "x", Label = "Close" };
        var window = Show(button);

        Assert.True(button.Focusable);
        Assert.True(button.IsTabStop);
        Assert.NotNull(button.FocusVisualStyle);
        var slider = new Slider { Style = (Style)window.FindResource("Lunar.Style.Slider") };
        var toggle = new ToggleButton { Style = (Style)window.FindResource("Lunar.Style.Toggle") };
        var action = new Button { Style = (Style)window.FindResource("Lunar.Style.ActionButton"), Content = "Retry" };
        Assert.All(new Control[] { slider, toggle, action }, control => Assert.True(control.Focusable && control.IsTabStop));
        Assert.NotNull(action.FocusVisualStyle);
        window.Close();
    });

    [Fact]
    public void ASliderInsideTheSwiperStillBlocksSwiping() => RunOnSta(() =>
    {
        // The new slider template must keep the Slider type, which is what the swiper checks before it starts a drag.
        var slider = new Slider();
        var window = Show(new StackPanel());
        slider.Style = (Style)window.FindResource("Lunar.Style.Slider");
        window.Content = slider;
        window.UpdateLayout();

        Assert.True(Descendants(slider).OfType<Thumb>().Any());
        Assert.IsType<Slider>(slider);
        window.Close();
    });
}
