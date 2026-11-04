using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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
        Assert.Equal(2, Descendants<Button>(rig.Page).Count(b => b is not IconButton && b.Content is "Turn off"));
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
        Assert.Equal(["Open Settings", "Open Settings"], buttons);
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
        var toggle = Descendants<Button>(Named<ContentControl>(rig, "BluetoothCard")).First(b => b.Content is "Turn off");

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
}
