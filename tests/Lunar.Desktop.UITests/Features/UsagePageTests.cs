using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using Lunar.Application.Usage;
using Lunar.Application.Usage.Models;
using Lunar.Desktop.Features.Usage;
using Lunar.Desktop.UITests.Themes;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Desktop.UITests.Features;

// FE-27, FE-29: the real Usage page, the real theme and control styles and the real brandmark, on an STA thread, bound to the real view model over
// fakes of its ports. How it looks to a person (the logo on screen, the colors on a real island, Narrator, scaling, High Contrast) is a
// manual check (NOT_RUN here).
public sealed class UsagePageTests
{
    private const double PageWidth = 380;
    private const double PageHeight = 250;

    private static ResourceDictionary Load(params string[] relative)
    {
        var path = System.IO.Path.Combine([ThemeTokenTests.DesktopDirectory, .. relative]);
        var text = File.ReadAllText(path).Replace("\"clr-namespace:Lunar.Desktop.Controls\"", "\"clr-namespace:Lunar.Desktop.Controls;assembly=Lunar.Desktop\"")
            .Replace("\"clr-namespace:Lunar.Desktop.Shell\"", "\"clr-namespace:Lunar.Desktop.Shell;assembly=Lunar.Desktop\"");
        return (ResourceDictionary)XamlReader.Parse(text);
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
            throw new InvalidOperationException("WPF usage page check failed.", failure);
    }

    private static void Pump(Func<bool> done)
    {
        var end = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < end)
        {
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
            Thread.Sleep(2);
        }

        Assert.True(done(), "The page did not reach the expected state in time.");
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

    private sealed class Rig : IDisposable
    {
        public UsagePageViewModelTests.Source Source { get; } = new();

        public UsagePageViewModelTests.Settings Settings { get; } = new();

        public UsageMonitor Monitor { get; }

        public UsagePageViewModel Model { get; }

        public UsagePage Page { get; }

        public Window Window { get; }

        public Rig(bool enabled)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            Settings.Current = Lunar.Application.Settings.Models.LunarSettings.Defaults with { Usage = new Lunar.Application.Settings.Models.UsageSettings { Enabled = enabled } };
            Monitor = new UsageMonitor(Source, new UsageMonitorOptions { Delay = (_, ct) => Task.Delay(Timeout.Infinite, ct), RefreshGap = TimeSpan.Zero });
            Model = new UsagePageViewModel(
                Monitor, new Lunar.Application.Settings.SettingsService(Settings),
                work => { if (dispatcher.CheckAccess()) work(); else dispatcher.BeginInvoke(work); },
                log: null, new UsagePageViewModelTests.FixedTime(), CultureInfo.InvariantCulture);
            Page = new UsagePage { DataContext = Model };
            Window = new Window { Width = PageWidth, Height = PageHeight, Content = Page, WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false };
            Window.Resources.MergedDictionaries.Add(Load("Themes", "Colors.Dark.xaml"));
            Window.Resources.MergedDictionaries.Add(Load("Themes", "Typography.xaml"));
            Window.Resources.MergedDictionaries.Add(Load("Themes", "Spacing.xaml"));
            Window.Resources.MergedDictionaries.Add(Load("Themes", "Components.xaml"));
            Window.Resources.MergedDictionaries.Add(Load("Controls", "ControlStyles.xaml"));
            Window.Show();
            var started = Model.InitializeAsync(CancellationToken.None);
            Pump(() => started.IsCompleted);
            Pump(() => Model.Mode != UsagePageMode.Loading);
            Window.UpdateLayout();
        }

        public void Dispose()
        {
            Window.Close();
            Model.Dispose();
            Monitor.Dispose();
        }
    }

    private static T Named<T>(UsagePage page, string name)
        where T : class =>
        (page.FindName(name) as T) ?? throw new InvalidOperationException(name + " is missing.");

    private static IEnumerable<string> AllTexts(DependencyObject root) =>
        Descendants<TextBlock>(root).Select(t => t.Text);

    private static Result<UsageSnapshot> Refused(string message) =>
        Result.Failure<UsageSnapshot>(new OperationError(ErrorCode.AccessDenied, message));

    [Fact]
    public void TheLimitsShowWithPercentBarsResetTimesAndTheUnmodifiedBrandmark() => RunOnSta(() =>
    {
        using var rig = new Rig(enabled: true);
        Pump(() => rig.Model.ShowLimits);
        rig.Window.UpdateLayout();

        var texts = AllTexts(rig.Page).ToList();
        Assert.Contains("67% used", texts);
        Assert.Contains("31% used", texts);
        Assert.Contains("5-hour limit", texts);
        Assert.Contains("Weekly limit", texts);
        Assert.Equal([67d, 31d], Descendants<ProgressBar>(rig.Page).Select(b => b.Value));
        Assert.All(Descendants<ProgressBar>(rig.Page), bar => Assert.Equal(100, bar.Maximum));

        var logo = Named<Image>(rig.Page, "Brandmark");
        Assert.NotNull(logo.Source);
        Assert.Equal(800, ((System.Windows.Media.Imaging.BitmapSource)logo.Source).PixelWidth);
        Assert.Equal(172, ((System.Windows.Media.Imaging.BitmapSource)logo.Source).PixelHeight);
        Assert.Equal("Claude", AutomationProperties.GetName(logo));
        // Unmodified: shown at its own aspect ratio, never stretched.
        Assert.Equal(Stretch.Uniform, logo.Stretch);
        Assert.True(Math.Abs((logo.ActualWidth / logo.ActualHeight) - (800d / 172d)) < 0.1, "The logo keeps its proportions.");

        Assert.Equal(Visibility.Collapsed, Named<Border>(rig.Page, "OffCard").Visibility);
        Assert.Equal(Visibility.Collapsed, Named<Border>(rig.Page, "LoginCard").Visibility);
        Assert.Equal(Visibility.Visible, Named<Button>(rig.Page, "RefreshButton").Visibility);
    });

    [Fact]
    public void EachLimitHasASpokenNameWithItsPercentAndReset() => RunOnSta(() =>
    {
        using var rig = new Rig(enabled: true);
        Pump(() => rig.Model.ShowLimits);

        var names = Descendants<ProgressBar>(rig.Page).Select(AutomationProperties.GetName).ToList();

        Assert.StartsWith("5-hour limit: 67% used. Resets in 2 h", names[0]);
        Assert.StartsWith("Weekly limit: 31% used. Resets in 3 d", names[1]);
    });

    [Fact]
    public void TheContentFitsThePageWidthAndTheSwitchSitsInTheIslandsOwnColors() => RunOnSta(() =>
    {
        using var rig = new Rig(enabled: true);
        Pump(() => rig.Model.ShowLimits);
        rig.Window.UpdateLayout();

        var scroll = Named<ScrollViewer>(rig.Page, "PageScroll");
        Assert.True(scroll.ExtentWidth <= scroll.ViewportWidth + 0.5, $"No sideways overflow ({scroll.ExtentWidth} > {scroll.ViewportWidth}).");
        var toggle = Named<Button>(rig.Page, "EnableSwitch");
        Assert.Equal("Show Claude usage", AutomationProperties.GetName(toggle));
        Assert.True(Lunar.Desktop.Controls.SwitchState.GetIsOn(toggle));
    });

    // The island gives a page 250 DIP of height: the main faces must fit without scrolling (the scroll bar is only a fallback for big text).
    private static void AssertNoScrolling(Rig rig, string face)
    {
        rig.Window.UpdateLayout();
        var scroll = Named<ScrollViewer>(rig.Page, "PageScroll");
        Assert.True(scroll.ExtentHeight <= scroll.ViewportHeight + 0.5, $"{face}: the page needs {scroll.ExtentHeight:F0} DIP but has {scroll.ViewportHeight:F0}.");
    }

    // Turns the page off and on again so the monitor asks the (changed) source once more.
    private static void Restart(Rig rig, Func<bool> done)
    {
        ((System.Windows.Input.ICommand)rig.Model.ToggleEnabledCommand).Execute(null);
        Pump(() => !rig.Model.IsEnabled);
        ((System.Windows.Input.ICommand)rig.Model.ToggleEnabledCommand).Execute(null);
        Pump(done);
    }

    [Fact]
    public void EveryMainFaceFitsTheIslandsPageHeightWithoutScrolling() => RunOnSta(() =>
    {
        using (var off = new Rig(enabled: false))
            AssertNoScrolling(off, "Off");

        using (var active = new Rig(enabled: true))
        {
            Pump(() => active.Model.ShowLimits);
            AssertNoScrolling(active, "Active with both limits");
        }

        // The longest sentence of the credential reader, in the login card.
        using var problem = new Rig(enabled: true);
        problem.Source.Next = () => Refused("The Claude Code sign-in on this PC does not allow reading usage. Sign in again with \"claude\" (not \"claude setup-token\"), then press Refresh.");
        Restart(problem, () => problem.Model.ShowLoginProblem);
        AssertNoScrolling(problem, "Sign-in problem with the longest message");
    });

    [Fact]
    public void ThereIsNoFieldToTypeInAndNoTokenButtonAnywhere() => RunOnSta(() =>
    {
        using var rig = new Rig(enabled: true);
        Pump(() => rig.Model.ShowLimits);

        Assert.Empty(Descendants<PasswordBox>(rig.Page));
        Assert.Empty(Descendants<TextBox>(rig.Page));
        var buttonNames = Descendants<Button>(rig.Page).Select(AutomationProperties.GetName).ToList();
        Assert.DoesNotContain(buttonNames, n => n.Contains("token", StringComparison.OrdinalIgnoreCase));
    });

    [Fact]
    public void ASignInProblemIsShownInItsOwnCardWithAProblemSymbolAndRefreshIsWithinReach() => RunOnSta(() =>
    {
        using var rig = new Rig(enabled: true);
        rig.Source.Next = () => Refused(UsagePageViewModelTests.NotSignedIn);
        Restart(rig, () => rig.Model.ShowLoginProblem);
        rig.Window.UpdateLayout();

        Assert.Equal(Visibility.Visible, Named<Border>(rig.Page, "LoginCard").Visibility);
        Assert.Equal(UsagePageViewModelTests.NotSignedIn, Named<TextBlock>(rig.Page, "LoginProblemText").Text);
        Assert.Equal(Visibility.Collapsed, Named<StackPanel>(rig.Page, "StatusCard").Visibility); // not said twice
        Assert.Equal(Visibility.Visible, Named<Button>(rig.Page, "RefreshButton").Visibility);
        Assert.Contains(Descendants<TextBlock>(rig.Page), t => t.Text == "" && t.Visibility == Visibility.Visible);
        Assert.Equal(Visibility.Collapsed, Named<StackPanel>(rig.Page, "Limits").Visibility);
    });

    [Fact]
    public void OffShowsTheDisclosureAndTheSwitchAndNothingElse() => RunOnSta(() =>
    {
        using var rig = new Rig(enabled: false);

        Assert.Equal(Visibility.Visible, Named<Border>(rig.Page, "OffCard").Visibility);
        Assert.Equal(Visibility.Collapsed, Named<Border>(rig.Page, "LoginCard").Visibility);
        Assert.Equal(Visibility.Collapsed, Named<StackPanel>(rig.Page, "Limits").Visibility);
        Assert.Equal(Visibility.Collapsed, Named<Button>(rig.Page, "RefreshButton").Visibility);
        Assert.False(Lunar.Desktop.Controls.SwitchState.GetIsOn(Named<Button>(rig.Page, "EnableSwitch")));
        Assert.Contains(AllTexts(rig.Page), t => t.Contains("Claude Code sign-in", StringComparison.Ordinal));
        Assert.Equal(0, rig.Source.Calls);
    });

    [Fact]
    public void AProblemWithTheNetworkKeepsTheLastReadingAndMarksTheSlimLine() => RunOnSta(() =>
    {
        using var rig = new Rig(enabled: true);
        Pump(() => rig.Model.ShowLimits);
        rig.Source.Next = () => Result.Failure<UsageSnapshot>(new OperationError(ErrorCode.DeviceUnavailable, "Claude cannot be reached.", retryable: true));

        ((System.Windows.Input.ICommand)rig.Model.RefreshCommand).Execute(null);
        Pump(() => rig.Model.StatusIsProblem);
        rig.Window.UpdateLayout();

        Assert.Equal(Visibility.Visible, Named<StackPanel>(rig.Page, "StatusCard").Visibility);
        Assert.StartsWith("Claude cannot be reached. Showing the updated ", Named<TextBlock>(rig.Page, "StatusLine").Text);
        Assert.Contains("67% used", AllTexts(rig.Page));
        Assert.Equal(Visibility.Collapsed, Named<Border>(rig.Page, "LoginCard").Visibility);
    });

    [Fact]
    public void ThePageUsesTheClaudePaletteOnlyForItsOwnCards() => RunOnSta(() =>
    {
        using var rig = new Rig(enabled: true);
        Pump(() => rig.Model.ShowLimits);

        var header = Named<Border>(rig.Page, "Header");
        Assert.Equal(Color.FromRgb(0xFF, 0xFF, 0xFF), ((SolidColorBrush)header.Background).Color);
        var limitCard = Descendants<Border>(rig.Page).First(b => AutomationProperties.GetName(b).StartsWith("5-hour", StringComparison.Ordinal));
        Assert.Equal(Color.FromRgb(0xFA, 0xF9, 0xF5), ((SolidColorBrush)limitCard.Background).Color);
        var fill = Descendants<Border>(Descendants<ProgressBar>(rig.Page).First()).First(b => b.Name == "PART_Indicator");
        Assert.Equal(Color.FromRgb(0xD9, 0x77, 0x57), ((SolidColorBrush)fill.Background).Color);
    });

    // ---- the palette, from source (no WPF needed) -----------------------------------------------------------------

    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static Dictionary<string, string> Colors(string file) =>
        XDocument.Load(System.IO.Path.Combine(ThemeTokenTests.DesktopDirectory, "Themes", file)).Root!.Elements()
            .Where(e => e.Name.LocalName == "Color")
            .ToDictionary(e => (string)e.Attribute(Xaml + "Key")!, e => e.Value.Trim());

    [Fact]
    public void BothPalettesDefineTheSameKeys()
    {
        var brand = XDocument.Load(System.IO.Path.Combine(ThemeTokenTests.DesktopDirectory, "Themes", "Colors.Usage.xaml")).Root!.Elements()
            .Select(e => (string)e.Attribute(Xaml + "Key")!).Order().ToList();
        var high = XDocument.Load(System.IO.Path.Combine(ThemeTokenTests.DesktopDirectory, "Themes", "Colors.Usage.HighContrast.xaml")).Root!.Elements()
            .Select(e => (string)e.Attribute(Xaml + "Key")!).Order().ToList();

        Assert.NotEmpty(brand);
        Assert.Equal(brand, high);
    }

    [Fact]
    public void TheBrandColorsAreThePublishedOnes()
    {
        var colors = Colors("Colors.Usage.xaml");

        Assert.Equal("#FAF9F5", colors["Usage.Color.Card"]);
        Assert.Equal("#141413", colors["Usage.Color.Ink"]);
        Assert.Equal("#D97757", colors["Usage.Color.Fill"]);
        Assert.Equal("#E8E6DC", colors["Usage.Color.Track"]);
    }

    [Theory]
    [InlineData("Ink", "Card", 4.5)]
    [InlineData("Ink", "Logo", 4.5)]
    [InlineData("Muted", "Card", 4.5)]
    [InlineData("Error", "Card", 4.5)]
    [InlineData("OnFill", "Fill", 4.5)]
    [InlineData("Ink", "Track", 3.0)] // the bar's outline against its track (the percentage is also written)
    [InlineData("Ink", "Card", 3.0)] // outlines and the focus ring against the card
    public void EveryTextAndOutlinePairMeetsItsContrast(string foreground, string background, double minimum)
    {
        var colors = Colors("Colors.Usage.xaml");

        var ratio = ThemeTokenTests.Contrast(colors["Usage.Color." + foreground], colors["Usage.Color." + background]);

        Assert.True(ratio >= minimum, $"{foreground} on {background} is {ratio:F2}:1, below {minimum}:1.");
    }

    [Fact]
    public void HighContrastUsesWindowsColorsOnly()
    {
        var root = XDocument.Load(System.IO.Path.Combine(ThemeTokenTests.DesktopDirectory, "Themes", "Colors.Usage.HighContrast.xaml")).Root!;

        var colors = root.Elements().Where(e => e.Name.LocalName == "Static").ToList();
        Assert.NotEmpty(colors);
        Assert.All(colors, e => Assert.StartsWith("SystemColors.", (string)e.Attribute("Member")!, StringComparison.Ordinal));
    }
}
