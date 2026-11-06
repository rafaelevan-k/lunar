using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lunar.Application.Abstractions.System;
using Lunar.Application.System.Models;
using Lunar.Application.Tests.Fakes;
using Lunar.Desktop.Controls;
using Lunar.Desktop.Features.SystemControls;
using Lunar.Desktop.UITests.Themes;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Desktop.UITests.Features;

// The real page with the real theme and control styles on an STA thread, bound to a real view model over fake ports.
// Windows that are shown are closed again and do not take focus.
public sealed class SystemPageTests
{
    // The area a page gets inside the expanded island: minimum island size minus the eyes strip and the page dots.
    private const double PageWidth = 380;
    private const double PageHeight = 250;

    private sealed class Clock : ILocaleClock
    {
        public DateTimeOffset LocalNow => new(2026, 11, 4, 14, 5, 0, TimeSpan.Zero);

        public CultureInfo Culture => CultureInfo.GetCultureInfo("en-US");

        public TimeZoneInfo TimeZone => TimeZoneInfo.Utc;

        public void Refresh()
        {
        }
    }

    private sealed class Launcher : ISystemSettingsLauncher
    {
        public Task<Result> OpenAsync(SettingsPage page, CancellationToken ct) => Task.FromResult(Result.Success());
    }

    private sealed class Rig(Window window, SystemPage page, SystemPageViewModel model, FakeVolumeService volume, FakeWifiService wifi, FakeBluetoothService bluetooth) : IDisposable
    {
        public Window Window { get; } = window;

        public SystemPage Page { get; } = page;

        public SystemPageViewModel Model { get; } = model;

        public FakeVolumeService Volume { get; } = volume;

        public FakeWifiService Wifi { get; } = wifi;

        public FakeBluetoothService Bluetooth { get; } = bluetooth;

        public void Dispose()
        {
            Model.Dispose();
            Window.Close();
        }
    }

    private static ResourceDictionary Load(params string[] relative)
    {
        var path = System.IO.Path.Combine([ThemeTokenTests.DesktopDirectory, .. relative]);
        // The compiled app refers to its own types without an assembly name; a loose parse needs it.
        var text = File.ReadAllText(path).Replace("\"clr-namespace:Lunar.Desktop.Controls\"", "\"clr-namespace:Lunar.Desktop.Controls;assembly=Lunar.Desktop\"")
            .Replace("\"clr-namespace:Lunar.Desktop.Shell\"", "\"clr-namespace:Lunar.Desktop.Shell;assembly=Lunar.Desktop\"");
        return (ResourceDictionary)XamlReader.Parse(text);
    }

    private static Rig Create(string colors = "Colors.Dark.xaml", bool start = true, Action<FakeVolumeService, FakeWifiService, FakeBluetoothService>? arrange = null)
    {
        var volume = new FakeVolumeService();
        var wifi = new FakeWifiService();
        var bluetooth = new FakeBluetoothService();
        arrange?.Invoke(volume, wifi, bluetooth);

        var dispatcher = Dispatcher.CurrentDispatcher;
        var model = new SystemPageViewModel(volume, wifi, bluetooth, new Clock(), new Launcher(),
            work => { if (dispatcher.CheckAccess()) work(); else dispatcher.BeginInvoke(work); });
        var page = new SystemPage { DataContext = model };
        var window = new Window { Width = PageWidth, Height = PageHeight, Content = page, WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false };
        window.Resources.MergedDictionaries.Add(Load("Themes", colors));
        window.Resources.MergedDictionaries.Add(Load("Themes", "Typography.xaml"));
        window.Resources.MergedDictionaries.Add(Load("Themes", "Spacing.xaml"));
        window.Resources.MergedDictionaries.Add(Load("Themes", "Components.xaml"));
        window.Resources.MergedDictionaries.Add(Load("Controls", "ControlStyles.xaml"));
        window.Show();
        window.UpdateLayout();

        var rig = new Rig(window, page, model, volume, wifi, bluetooth);
        if (start)
        {
            Pump(model.InitializeAsync());
            model.RefreshClock();
            Settle(window);
        }

        return rig;
    }

    private static void Pump(Task task)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < end)
        {
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
            Thread.Sleep(2);
        }

        Assert.True(task.IsCompleted, "The task did not finish in time.");
        task.GetAwaiter().GetResult();
    }

    // Like a real click: the command runs inside the dispatcher, so its awaits come back to the UI thread.
    private static void Press(Rig rig, Button button) =>
        rig.Window.Dispatcher.Invoke(() => button.Command.Execute(null));

    private static void Settle(Window window)
    {
        window.Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
        window.UpdateLayout();
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
            throw new InvalidOperationException("WPF system page check failed.", failure);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var nested in Descendants<T>(child))
                yield return nested;
        }
    }

    private static T Named<T>(Rig rig, string name)
        where T : class => (T)rig.Page.FindName(name);

    private static Visibility VisibilityOf(UIElement element) => element.Visibility;

    // ---- states ------------------------------------------------------------------------------------------------------

    [Fact]
    public void BeforeAnythingIsReadPlaceholdersShowAndNoControlIsOffered() => RunOnSta(() =>
    {
        using var rig = Create(start: false);

        Assert.Equal(Visibility.Visible, VisibilityOf(Named<Grid>(rig, "VolumePlaceholder")));
        Assert.Equal(Visibility.Collapsed, VisibilityOf(Named<Slider>(rig, "VolumeSlider")));
        Assert.All(Descendants<SkeletonBlock>(rig.Page), block => Assert.Equal(Visibility.Visible, block.IsVisible ? Visibility.Visible : Visibility.Collapsed));
        Assert.DoesNotContain(Descendants<Button>(rig.Page), b => b is not IconButton && b.IsVisible);
        Assert.False(Named<IconButton>(rig, "MuteButton").IsEnabled);
    });

    [Fact]
    public void AfterTheFirstReadingEveryCardShowsItsState() => RunOnSta(() =>
    {
        using var rig = Create();

        Assert.Equal(Visibility.Visible, VisibilityOf(Named<Slider>(rig, "VolumeSlider")));
        Assert.Equal(Visibility.Collapsed, VisibilityOf(Named<Grid>(rig, "VolumePlaceholder")));
        Assert.Equal(50, Named<Slider>(rig, "VolumeSlider").Value);
        Assert.Equal("50%", Named<TextBlock>(rig, "VolumeValue").Text);
        Assert.Equal("2:05 PM", Named<TextBlock>(rig, "Time").Text);
        Assert.DoesNotContain(Descendants<SkeletonBlock>(rig.Page), b => b.IsVisible);
        var chips = Descendants<StatusChip>(rig.Page).ToList();
        Assert.Equal(["On, not connected", "On"], chips.Select(c => c.Text));
        Assert.Equal(["Turn off Wi-Fi", "Turn off Bluetooth"], Descendants<Button>(rig.Page).Where(b => b.IsVisible).Select(AutomationProperties.GetName).Where(n => n.StartsWith("Turn", StringComparison.Ordinal)));
    });

    [Fact]
    public void WhenThereIsNoOutputDeviceTheNoteReplacesTheSlider() => RunOnSta(() =>
    {
        using var rig = Create(arrange: (volume, _, _) => volume.RaiseExternalChange(VolumeState.Unavailable));

        Assert.Equal(Visibility.Collapsed, VisibilityOf(Named<Slider>(rig, "VolumeSlider")));
        var note = Named<TextBlock>(rig, "VolumeNoteText");
        Assert.Equal(Visibility.Visible, note.Visibility);
        Assert.Equal("No audio output device", note.Text);
        Assert.False(Named<IconButton>(rig, "MuteButton").IsEnabled);
    });

    [Fact]
    public void UnsupportedAndDeniedRadiosShowWordsAndASettingsLinkNotASwitch() => RunOnSta(() =>
    {
        using var rig = Create(arrange: (_, wifi, bluetooth) =>
        {
            wifi.State = new WifiState(CapabilityStatus.Unsupported, RadioState.Unavailable, WifiConnectivity.Unknown, false);
            bluetooth.State = new BluetoothState(CapabilityStatus.PermissionDenied, RadioState.Unknown, false);
        });

        Assert.Equal(["Not supported", "Access denied"], Descendants<StatusChip>(rig.Page).Select(c => c.Text));
        var buttons = Descendants<Button>(rig.Page).Where(b => b is not IconButton && b.IsVisible).Select(b => b.Content).ToList();
        Assert.Equal(["Settings", "Settings"], buttons);
    });

    [Fact]
    public void NoAdapterShowsNoSwitchAndNoSettingsLink() => RunOnSta(() =>
    {
        using var rig = Create(arrange: (_, wifi, _) => wifi.State = new WifiState(CapabilityStatus.Unavailable, RadioState.Unavailable, WifiConnectivity.Disconnected, false));

        var chip = Descendants<StatusChip>(rig.Page).First();
        Assert.Equal("No Wi-Fi adapter", chip.Text);
        var wifiCard = Named<ContentControl>(rig, "WifiCard");
        Assert.DoesNotContain(Descendants<Button>(wifiCard), b => b.IsVisible);
    });

    // ---- interaction -------------------------------------------------------------------------------------------------

    [Fact]
    public void TheSliderWritesThroughTheViewModel() => RunOnSta(() =>
    {
        using var rig = Create();

        Named<Slider>(rig, "VolumeSlider").Value = 70;
        Pump(rig.Model.PendingWrite);
        Settle(rig.Window);

        Assert.Equal(70, rig.Model.VolumePercent);
        Assert.Equal(0.7f, rig.Volume.State.Scalar, precision: 3);
        Assert.Equal("70%", Named<TextBlock>(rig, "VolumeValue").Text);
    });

    [Fact]
    public void AnOutsideVolumeChangeMovesTheSlider() => RunOnSta(() =>
    {
        using var rig = Create();

        rig.Volume.RaiseExternalChange(new VolumeState(0.2f, true, CapabilityStatus.Supported));
        Settle(rig.Window);

        Assert.Equal(20, Named<Slider>(rig, "VolumeSlider").Value);
        Assert.Equal("Muted", Named<TextBlock>(rig, "VolumeValue").Text);
        Assert.Equal("Unmute", AutomationProperties.GetName(Named<IconButton>(rig, "MuteButton")));
    });

    [Fact]
    public void ThePressedMuteButtonRunsTheCommand() => RunOnSta(() =>
    {
        using var rig = Create();
        var button = Named<IconButton>(rig, "MuteButton");

        Press(rig, button);
        var end = DateTime.UtcNow.AddSeconds(3);
        while (!rig.Volume.State.IsMuted && DateTime.UtcNow < end)
            Settle(rig.Window);

        Assert.True(rig.Model.IsMuted);
        Assert.True(rig.Volume.State.IsMuted);
        Assert.Equal("Unmute", button.Label);
    });

    [Fact]
    public void ARadioSwitchPressedOnTheCardChangesTheRadio() => RunOnSta(() =>
    {
        using var rig = Create();
        var toggle = Descendants<Button>(Named<ContentControl>(rig, "BluetoothCard")).First(b => AutomationProperties.GetName(b) == "Turn off Bluetooth");

        Press(rig, toggle);
        var end = DateTime.UtcNow.AddSeconds(3);
        while (rig.Bluetooth.State.Radio != RadioState.Disabled && DateTime.UtcNow < end)
            Settle(rig.Window);
        Settle(rig.Window);

        Assert.Equal(RadioState.Disabled, rig.Bluetooth.State.Radio);
        Assert.Equal("Off", Descendants<StatusChip>(Named<ContentControl>(rig, "BluetoothCard")).First().Text);
    });

    // ---- layout, accessibility, themes -------------------------------------------------------------------------------

    [Theory]
    [InlineData("Colors.Dark.xaml")]
    [InlineData("Colors.Light.xaml")]
    [InlineData("Colors.HighContrast.xaml")]
    public void ThePageFitsTheExpandedIslandInEveryTheme(string colors) => RunOnSta(() =>
    {
        using var rig = Create(colors);

        rig.Page.Measure(new Size(PageWidth, double.PositiveInfinity));

        Assert.True(rig.Page.DesiredSize.Height <= PageHeight, $"Page wants {rig.Page.DesiredSize.Height} high, {PageHeight} available.");
        Assert.True(rig.Page.DesiredSize.Width <= PageWidth);
    });

    [Fact]
    public void ThePageStillFitsWithAnErrorShownAndShowsOnlyOne() => RunOnSta(() =>
    {
        using var rig = Create();
        rig.Model.Wifi.ShowFault("Couldn't read the Wi-Fi state. Check that the WLAN service is running and try again later.");
        rig.Model.Bluetooth.ShowFault("Couldn't read the Bluetooth state.");
        Settle(rig.Window);

        rig.Page.Measure(new Size(PageWidth, double.PositiveInfinity));

        Assert.True(rig.Page.DesiredSize.Height <= PageHeight, $"Page wants {rig.Page.DesiredSize.Height} high, {PageHeight} available.");
        Assert.Equal(Visibility.Visible, Named<InlineError>(rig, "PageError").Visibility);
        Assert.StartsWith("Couldn't read the Wi-Fi", Named<InlineError>(rig, "PageError").Message, StringComparison.Ordinal);
    });

    [Fact]
    public void WithoutAnErrorThePageErrorTakesNoRoom() => RunOnSta(() =>
    {
        using var rig = Create();

        Assert.Null(Named<InlineError>(rig, "PageError").Message);
        Assert.Equal(0, Named<InlineError>(rig, "PageError").ActualHeight);
    });

    [Fact]
    public void ContentStaysClearOfTheSwiperArrows() => RunOnSta(() =>
    {
        using var rig = Create();
        rig.Page.Measure(new Size(PageWidth, double.PositiveInfinity));
        rig.Page.Arrange(new Rect(0, 0, PageWidth, PageHeight));

        foreach (var card in new[] { Named<ContentControl>(rig, "WifiCard"), Named<ContentControl>(rig, "BluetoothCard") })
        {
            var bounds = card.TransformToAncestor(rig.Page).TransformBounds(new Rect(card.RenderSize));
            Assert.True(bounds.Left >= 36 && bounds.Right <= PageWidth - 36, $"Card spans {bounds.Left}..{bounds.Right}.");
        }
    });

    [Fact]
    public void EveryControlHasAnAccessibleName() => RunOnSta(() =>
    {
        using var rig = Create();

        Assert.Equal("Volume", AutomationProperties.GetName(Named<Slider>(rig, "VolumeSlider")));
        Assert.Equal("Mute", AutomationProperties.GetName(Named<IconButton>(rig, "MuteButton")));
        foreach (var button in Descendants<Button>(rig.Page).Where(b => b is not IconButton))
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)), "A button has no accessible name.");
        foreach (var chip in Descendants<StatusChip>(rig.Page))
            Assert.False(string.IsNullOrWhiteSpace(chip.Text));
    });

    [Fact]
    public void EveryInteractiveControlIsAKeyboardTabStop() => RunOnSta(() =>
    {
        using var rig = Create();

        Assert.True(Named<Slider>(rig, "VolumeSlider").IsTabStop);
        Assert.True(Named<Slider>(rig, "VolumeSlider").Focusable);
        Assert.True(Named<IconButton>(rig, "MuteButton").Focusable);
        Assert.All(Descendants<Button>(rig.Page).Where(b => b.IsVisible), b => Assert.True(b.Focusable && b.IsTabStop));
    });

    [Fact]
    public void ThePageRendersSomethingVisible() => RunOnSta(() =>
    {
        using var rig = Create();
        rig.Page.Measure(new Size(PageWidth, PageHeight));
        rig.Page.Arrange(new Rect(0, 0, PageWidth, PageHeight));
        var bitmap = new RenderTargetBitmap((int)PageWidth, (int)PageHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(rig.Page);

        var pixels = new byte[(int)PageWidth * (int)PageHeight * 4];
        bitmap.CopyPixels(pixels, (int)PageWidth * 4, 0);

        Assert.Contains(pixels.Where((_, i) => i % 4 == 3), alpha => alpha > 0); // something was drawn
        var shots = Environment.GetEnvironmentVariable("LUNAR_SHOT_DIR");
        if (!string.IsNullOrEmpty(shots) && Directory.Exists(shots))
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(System.IO.Path.Combine(shots, "system-page.png"));
            encoder.Save(stream);
        }
    });

    // ---- the slider while the hand is on it (FE-14) ------------------------------------------------------------------

    private static void StartDrag(Slider slider) =>
        slider.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent, Source = slider });

    private static void EndDrag(Slider slider) =>
        slider.RaiseEvent(new DragCompletedEventArgs(0, 0, canceled: false) { RoutedEvent = Thumb.DragCompletedEvent, Source = slider });

    private static void PressKey(Rig rig, Slider slider, Key key, bool down)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(rig.Window)!, 0, key)
        {
            RoutedEvent = down ? Keyboard.PreviewKeyDownEvent : Keyboard.KeyUpEvent,
            Source = slider
        };
        slider.RaiseEvent(args);
    }

    private static MouseWheelEventArgs Wheel(Slider slider, int delta)
    {
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = slider };
        slider.RaiseEvent(args);
        return args;
    }

    [Fact]
    public void ADragOfTheThumbMarksTheViewModelAsAdjustingUntilItCompletes() => RunOnSta(() =>
    {
        using var rig = Create();
        var slider = Named<Slider>(rig, "VolumeSlider");

        StartDrag(slider);
        Assert.True(rig.Model.IsAdjusting);
        EndDrag(slider);

        Assert.False(rig.Model.IsAdjusting);
    });

    [Fact]
    public void AnOutsideChangeDuringADragDoesNotMoveTheThumbAndAppliesOnRelease() => RunOnSta(() =>
    {
        using var rig = Create();
        var slider = Named<Slider>(rig, "VolumeSlider");

        StartDrag(slider);
        rig.Volume.RaiseExternalChange(new VolumeState(0.9f, false, CapabilityStatus.Supported));
        Settle(rig.Window);
        Assert.Equal(50, slider.Value);

        EndDrag(slider);
        Settle(rig.Window);
        Assert.Equal(90, slider.Value);
    });

    [Fact]
    public void ADragThatMovedTheThumbKeepsTheUsersLevelWhateverElseHappened() => RunOnSta(() =>
    {
        using var rig = Create();
        var slider = Named<Slider>(rig, "VolumeSlider");

        StartDrag(slider);
        slider.Value = 25;
        rig.Volume.RaiseExternalChange(new VolumeState(0.9f, false, CapabilityStatus.Supported));
        Settle(rig.Window);
        Pump(rig.Model.PendingWrite);
        EndDrag(slider);
        Settle(rig.Window);

        Assert.Equal(25, slider.Value);
        Assert.Equal(25, rig.Model.VolumePercent);
    });

    [Fact]
    public void NavigationKeysCountAsAdjustingOtherKeysDoNot() => RunOnSta(() =>
    {
        using var rig = Create();
        var slider = Named<Slider>(rig, "VolumeSlider");

        PressKey(rig, slider, Key.A, down: true);
        Assert.False(rig.Model.IsAdjusting);

        PressKey(rig, slider, Key.Right, down: true);
        Assert.True(rig.Model.IsAdjusting);
        PressKey(rig, slider, Key.Right, down: false);

        Assert.False(rig.Model.IsAdjusting);
    });

    [Fact]
    public void AdjustingNeverStaysOnWhateverEndsTheInteraction() => RunOnSta(() =>
    {
        using var rig = Create();
        var slider = Named<Slider>(rig, "VolumeSlider");

        StartDrag(slider);
        slider.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, slider, Named<IconButton>(rig, "MuteButton"))
        {
            RoutedEvent = Keyboard.LostKeyboardFocusEvent
        });
        Assert.False(rig.Model.IsAdjusting); // focus moved away

        StartDrag(slider);
        slider.IsEnabled = false;
        Assert.False(rig.Model.IsAdjusting); // disabled (the device went away)
        slider.IsEnabled = true;

        StartDrag(slider);
        rig.Window.Content = null;
        Settle(rig.Window);
        Assert.False(rig.Model.IsAdjusting); // unloaded
    });

    [Fact]
    public void TheWheelOverTheSliderChangesItByTwoPerNotchAndIsHandled() => RunOnSta(() =>
    {
        using var rig = Create();
        var slider = Named<Slider>(rig, "VolumeSlider");

        var up = Wheel(slider, 120);
        var down = Wheel(slider, -240);

        Assert.True(up.Handled);
        Assert.True(down.Handled);
        Assert.Equal(48, slider.Value); // 50 + 2 - 4
    });

    [Fact]
    public void TheWheelStopsAtTheEnds() => RunOnSta(() =>
    {
        using var rig = Create();
        var slider = Named<Slider>(rig, "VolumeSlider");

        Wheel(slider, 12000);
        Assert.Equal(100, slider.Value);

        Wheel(slider, -24000);
        Assert.Equal(0, slider.Value);
    });

    [Fact]
    public void ASmallPrecisionTouchpadDeltaMovesProportionally() => RunOnSta(() =>
    {
        using var rig = Create();
        var slider = Named<Slider>(rig, "VolumeSlider");

        Wheel(slider, 60);

        Assert.Equal(51, slider.Value);
    });

    [Fact]
    public void WithoutAWheelStepTheWheelIsLeftAlone() => RunOnSta(() =>
    {
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = 50 };
        Lunar.Desktop.Behaviors.SliderInteraction.SetIsTracking(slider, true);

        var args = Wheel(slider, 120);

        Assert.False(args.Handled);
        Assert.Equal(50, slider.Value);
    });

    [Fact]
    public void TurningTrackingOffRemovesTheBehaviorAndResetsAdjusting() => RunOnSta(() =>
    {
        using var rig = Create();
        var slider = Named<Slider>(rig, "VolumeSlider");
        StartDrag(slider);

        Lunar.Desktop.Behaviors.SliderInteraction.SetIsTracking(slider, false);
        Assert.False(rig.Model.IsAdjusting);
        StartDrag(slider);

        Assert.False(rig.Model.IsAdjusting);
    });

    [Fact]
    public void TheSliderDimsWhileMutedAndNotOtherwise() => RunOnSta(() =>
    {
        using var rig = Create();
        var host = Named<Grid>(rig, "SliderHost");
        Assert.Equal(1.0, host.Opacity);

        rig.Volume.RaiseExternalChange(new VolumeState(0.5f, true, CapabilityStatus.Supported));
        Settle(rig.Window);
        Assert.Equal(0.5, host.Opacity);

        rig.Volume.RaiseExternalChange(new VolumeState(0.5f, false, CapabilityStatus.Supported));
        Settle(rig.Window);
        Assert.Equal(1.0, host.Opacity);
    });

    [Fact]
    public void MovingTheThumbWhileMutedUnmutesOnScreen() => RunOnSta(() =>
    {
        using var rig = Create(arrange: (volume, _, _) => volume.RaiseExternalChange(new VolumeState(0.5f, true, CapabilityStatus.Supported)));
        var slider = Named<Slider>(rig, "VolumeSlider");
        Assert.Equal("Unmute", AutomationProperties.GetName(Named<IconButton>(rig, "MuteButton")));

        slider.Value = 60;
        Pump(rig.Model.PendingWrite);
        Settle(rig.Window);

        Assert.Equal("Mute", AutomationProperties.GetName(Named<IconButton>(rig, "MuteButton")));
        Assert.Equal("60%", Named<TextBlock>(rig, "VolumeValue").Text);
    });

    // ---- the switch (FE-15) ------------------------------------------------------------------------------------------

    /// <summary>A Wi-Fi service that answers a switch request only when told to.</summary>
    private sealed class HoldingWifi : IWifiService
    {
        private readonly Lunar.Application.Tests.Fakes.TestObservable<WifiState> _changes = new();
        private TaskCompletionSource<Result>? _gate;

        public WifiState State { get; set; } = new(CapabilityStatus.Supported, RadioState.Enabled, WifiConnectivity.Connected, true);

        public IObservable<WifiState> Changes => _changes;

        public Task<Result<WifiState>> GetAsync(CancellationToken ct) => Task.FromResult(Result.Success(State));

        public Task<Result> SetEnabledAsync(bool enabled, CancellationToken ct)
        {
            _gate = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _gate.Task;
        }

        public void Answer(Result result, RadioState radio)
        {
            State = State with { Radio = radio };
            _changes.Publish(State);
            _gate!.SetResult(result);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static Rig CreateHolding(HoldingWifi wifi)
    {
        var volume = new FakeVolumeService();
        var bluetooth = new FakeBluetoothService();
        var dispatcher = Dispatcher.CurrentDispatcher;
        var model = new SystemPageViewModel(volume, wifi, bluetooth, new Clock(), new Launcher(),
            work => { if (dispatcher.CheckAccess()) work(); else dispatcher.BeginInvoke(work); });
        var page = new SystemPage { DataContext = model };
        var window = new Window { Width = PageWidth, Height = PageHeight, Content = page, WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false };
        window.Resources.MergedDictionaries.Add(Load("Themes", "Colors.Dark.xaml"));
        window.Resources.MergedDictionaries.Add(Load("Themes", "Typography.xaml"));
        window.Resources.MergedDictionaries.Add(Load("Themes", "Spacing.xaml"));
        window.Resources.MergedDictionaries.Add(Load("Themes", "Components.xaml"));
        window.Resources.MergedDictionaries.Add(Load("Controls", "ControlStyles.xaml"));
        window.Show();
        window.UpdateLayout();
        var rig = new Rig(window, page, model, volume, new FakeWifiService(), bluetooth);
        Pump(model.InitializeAsync());
        Settle(window);
        return rig;
    }

    private static Button WifiSwitch(Rig rig) =>
        Descendants<Button>(Named<ContentControl>(rig, "WifiCard")).First(b => AutomationProperties.GetName(b).StartsWith("Turn", StringComparison.Ordinal));

    [Fact]
    public void TheSwitchDrawsTheStateWindowsReportedAndNothingElse() => RunOnSta(() =>
    {
        using var rig = Create();

        Assert.True(Lunar.Desktop.Controls.SwitchState.GetIsOn(WifiSwitch(rig)));
        Assert.Equal("Turn off Wi-Fi", AutomationProperties.GetName(WifiSwitch(rig)));
    });

    [Fact]
    public void PressingTheSwitchDoesNotMoveItUntilWindowsConfirms() => RunOnSta(() =>
    {
        var wifi = new HoldingWifi();
        using var rig = CreateHolding(wifi);
        var toggle = WifiSwitch(rig);

        Press(rig, toggle);
        Settle(rig.Window);

        Assert.True(Lunar.Desktop.Controls.SwitchState.GetIsOn(toggle)); // still the real state
        Assert.False(toggle.IsEnabled); // one request at a time
        var chip = Descendants<StatusChip>(Named<ContentControl>(rig, "WifiCard")).First();
        Assert.Equal("Turning off…", chip.Text);
        Assert.Equal(StatusKind.Info, chip.Kind);

        wifi.Answer(Result.Success(), RadioState.Disabled);
        var end = DateTime.UtcNow.AddSeconds(3);
        while (rig.Model.Wifi.IsSwitching && DateTime.UtcNow < end)
            Settle(rig.Window);
        Settle(rig.Window);

        Assert.False(Lunar.Desktop.Controls.SwitchState.GetIsOn(WifiSwitch(rig)));
        Assert.Equal("Off", Descendants<StatusChip>(Named<ContentControl>(rig, "WifiCard")).First().Text);
        Assert.True(WifiSwitch(rig).IsEnabled);
    });

    [Fact]
    public void ARefusedRequestLeavesTheSwitchOnAndTheErrorOnThePage() => RunOnSta(() =>
    {
        var wifi = new HoldingWifi();
        using var rig = CreateHolding(wifi);
        Press(rig, WifiSwitch(rig));

        wifi.Answer(Result.Failure(new OperationError(ErrorCode.Timeout, "slow", retryable: true)), RadioState.Enabled);
        var end = DateTime.UtcNow.AddSeconds(3);
        while (rig.Model.Wifi.IsSwitching && DateTime.UtcNow < end)
            Settle(rig.Window);
        Settle(rig.Window);

        Assert.True(Lunar.Desktop.Controls.SwitchState.GetIsOn(WifiSwitch(rig)));
        Assert.Equal("Windows didn't confirm the change. Try again.", Named<InlineError>(rig, "PageError").Message);
    });

    [Fact]
    public void TheSwitchCanBePressedByAssistiveTechnology() => RunOnSta(() =>
    {
        using var rig = Create();
        var toggle = WifiSwitch(rig);

        var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(toggle);

        Assert.NotNull(peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke));
        Assert.Equal("Turn off Wi-Fi", peer.GetName());
        Assert.True(toggle.Focusable && toggle.IsTabStop);
    });

    [Fact]
    public void WithoutASwitchTheCardGivesAReasonAndTheSettingsPage() => RunOnSta(() =>
    {
        using var rig = Create(arrange: (_, wifi, bluetooth) =>
        {
            wifi.State = new WifiState(CapabilityStatus.Unsupported, RadioState.Unavailable, WifiConnectivity.Unknown, false);
            bluetooth.State = new BluetoothState(CapabilityStatus.Supported, RadioState.Disabled, CanToggle: false);
        });

        var cards = new[] { Named<ContentControl>(rig, "WifiCard"), Named<ContentControl>(rig, "BluetoothCard") };
        var hints = cards.Select(card => Descendants<TextBlock>(card).First(t => t.Name == "HintText")).ToList();
        Assert.Equal(["This PC doesn't allow controlling it.", "Can't be switched from here."], hints.Select(h => h.Text));
        Assert.All(hints, h => Assert.True(h.IsVisible));
        Assert.All(cards, card => Assert.DoesNotContain(Descendants<Button>(card), b => b.IsVisible && AutomationProperties.GetName(b).StartsWith("Turn", StringComparison.Ordinal)));
        Assert.All(cards, card => Assert.Single(Descendants<Button>(card), b => b.IsVisible && AutomationProperties.GetName(b) == "Open Settings"));
    });

    [Fact]
    public void ACardWithASwitchShowsNoReasonText() => RunOnSta(() =>
    {
        using var rig = Create();

        var hints = Descendants<TextBlock>(rig.Page).Where(t => t.Name == "HintText");
        Assert.All(hints, h => Assert.False(h.IsVisible));
    });

    [Fact]
    public void TheWorstCaseCardsStillFitTheExpandedIsland() => RunOnSta(() =>
    {
        using var rig = Create(arrange: (_, wifi, bluetooth) =>
        {
            wifi.State = new WifiState(CapabilityStatus.Unsupported, RadioState.Unavailable, WifiConnectivity.Unknown, false);
            bluetooth.State = new BluetoothState(CapabilityStatus.PermissionDenied, RadioState.Unknown, false);
        });

        rig.Page.Measure(new Size(PageWidth, double.PositiveInfinity));

        Assert.True(rig.Page.DesiredSize.Height <= PageHeight, $"Page wants {rig.Page.DesiredSize.Height} high, {PageHeight} available.");
    });

    [Fact]
    public void TheWorstCaseCardsWithAnErrorStillFit() => RunOnSta(() =>
    {
        using var rig = Create(arrange: (_, wifi, bluetooth) =>
        {
            wifi.State = new WifiState(CapabilityStatus.Unsupported, RadioState.Unavailable, WifiConnectivity.Unknown, false);
            bluetooth.State = new BluetoothState(CapabilityStatus.PermissionDenied, RadioState.Unknown, false);
        });
        rig.Model.Wifi.ShowFault("Windows didn't allow this change. Use Settings to switch Bluetooth.");
        Settle(rig.Window);

        rig.Page.Measure(new Size(PageWidth, double.PositiveInfinity));

        Assert.True(rig.Page.DesiredSize.Height <= PageHeight, $"Page wants {rig.Page.DesiredSize.Height} high, {PageHeight} available.");
    });

    [Fact]
    public void TheWorstCasePageRendersWhenAskedTo() => RunOnSta(() =>
    {
        var shots = Environment.GetEnvironmentVariable("LUNAR_SHOT_DIR");
        if (string.IsNullOrEmpty(shots) || !Directory.Exists(shots))
            return; // only to look at the page by eye; nothing to assert otherwise

        using var rig = Create(arrange: (_, wifi, bluetooth) =>
        {
            wifi.State = new WifiState(CapabilityStatus.Unsupported, RadioState.Unavailable, WifiConnectivity.Unknown, false);
            bluetooth.State = new BluetoothState(CapabilityStatus.Supported, RadioState.Disabled, CanToggle: false);
        });
        rig.Model.Wifi.ShowFault("Windows didn't allow this change. Use Settings to switch Bluetooth.");
        Settle(rig.Window);
        rig.Page.Measure(new Size(PageWidth, PageHeight));
        rig.Page.Arrange(new Rect(0, 0, PageWidth, PageHeight));
        var bitmap = new RenderTargetBitmap((int)PageWidth, (int)PageHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(rig.Page);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(System.IO.Path.Combine(shots, "system-page-worst.png"));
        encoder.Save(stream);
    });

    [Fact]
    public void TheClockIsOneAccessibleItemAndTheZoneIsItsTooltip() => RunOnSta(() =>
    {
        using var rig = Create();
        var clock = Named<StackPanel>(rig, "Clock");

        Assert.Equal("2:05 PM, Wednesday, November 4, 2026", AutomationProperties.GetName(clock));
        Assert.Equal(TimeZoneInfo.Utc.StandardName, AutomationProperties.GetHelpText(clock));
        Assert.Equal(TimeZoneInfo.Utc.StandardName, clock.ToolTip);
    });

    // ---- boundaries --------------------------------------------------------------------------------------------------

    [Fact]
    public void NoWindowsApiOrLogicLivesInTheXamlOrItsCodeBehind()
    {
        var folder = System.IO.Path.Combine(ThemeTokenTests.DesktopDirectory, "Features", "System");
        var forbidden = new[] { "DllImport", "LibraryImport", "Windows.Devices", "Registry", "Process.Start", "Marshal.", "CoreAudio", "Wlan", "IVolumeService", "IWifiService", "IBluetoothService" };

        foreach (var file in new[] { "SystemPage.xaml", "SystemPage.xaml.cs" })
        {
            var text = File.ReadAllText(System.IO.Path.Combine(folder, file));
            foreach (var word in forbidden)
                Assert.DoesNotContain(word, text, StringComparison.Ordinal);
        }

        var codeBehind = File.ReadAllText(System.IO.Path.Combine(folder, "SystemPage.xaml.cs"));
        Assert.DoesNotContain("async ", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("Click=", File.ReadAllText(System.IO.Path.Combine(folder, "SystemPage.xaml")), StringComparison.Ordinal);
    }

    [Fact]
    public void TheViewModelNamesNoWindowsInteropOrImplementationType()
    {
        var folder = System.IO.Path.Combine(ThemeTokenTests.DesktopDirectory, "Features", "System");
        foreach (var file in Directory.GetFiles(folder, "*.cs"))
        {
            var text = File.ReadAllText(file);
            foreach (var word in new[] { "DllImport", "Lunar.Windows.", "Windows.Devices", "System.Runtime.InteropServices", "Process.Start" })
                Assert.DoesNotContain(word, text, StringComparison.Ordinal);
        }
    }

    // FE-24: names, tab stops and tab order of the real page, in English and in Indonesian (the longer words must fit and be named too).
    [Theory]
    [InlineData("en")]
    [InlineData("id")]
    public void TheSystemPageIsNamedAndReachableByKeyboard(string language) => RunOnSta(() =>
    {
        Lunar.Desktop.Resources.Strings.Use(language);
        try
        {
            using var rig = Create();
            Lunar.Desktop.UITests.Accessibility.AutomationAudit.AssertAccessible(rig.Window);
        }
        finally
        {
            Lunar.Desktop.Resources.Strings.Use("en");
        }
    });

    // FE-24: Indonesian words are longer; the page must still fit the island (and nothing may be cut off).
    [Fact]
    public void TheSystemPageStillFitsTheIslandInIndonesian() => RunOnSta(() =>
    {
        Lunar.Desktop.Resources.Strings.Use("id");
        try
        {
            foreach (var colors in new[] { "Colors.Dark.xaml", "Colors.Light.xaml", "Colors.HighContrast.xaml" })
            {
                using var rig = Create(colors);
                rig.Page.Measure(new Size(PageWidth, double.PositiveInfinity));
                Assert.True(rig.Page.DesiredSize.Height <= PageHeight, $"Page wants {rig.Page.DesiredSize.Height} high, {PageHeight} available.");
                Assert.True(rig.Page.DesiredSize.Width <= PageWidth);
            }
        }
        finally
        {
            Lunar.Desktop.Resources.Strings.Use("en");
        }
    });
}
