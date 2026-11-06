using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Lunar.Application.Abstractions.Settings;
using Lunar.Application.Abstractions.Shell;
using Lunar.Application.Settings;
using Lunar.Application.Settings.Models;
using Lunar.Application.Shell.Models;
using Lunar.Desktop.Controls;
using Lunar.Desktop.Features.Converter;
using Lunar.Desktop.Features.Settings;
using Lunar.Desktop.UITests.Themes;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Desktop.UITests.Features;

// FE-23: the real Settings window on an STA thread, far off screen and never activated, over the real view model and service with fakes for
// the file, the displays and the folder dialog. How it looks to a person on a real desktop, Narrator reading it, 100-200% scaling, a second
// monitor and the real tray click are manual checks (NOT_RUN here).
public sealed class SettingsWindowTests
{
    private sealed class Repository : ISettingsRepository
    {
        public LunarSettings Stored { get; set; } = LunarSettings.Defaults;

        public SettingsRecovery Recovery { get; set; } = SettingsRecovery.None;

        public ErrorCode? FailSaveWith { get; set; }

        public Task<Result<SettingsLoadResult>> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success(new SettingsLoadResult(Stored, Recovery)));

        public Task<Result> SaveAsync(LunarSettings settings, CancellationToken cancellationToken)
        {
            if (FailSaveWith is { } code)
                return Task.FromResult(Result.Failure(new OperationError(code, "Cannot write.")));
            Stored = settings;
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class Environment : IWindowEnvironment
    {
        public event Action? DisplaysChanged
        {
            add { }
            remove { }
        }

        public Result Start() => Result.Success();

        public Result<IReadOnlyList<DisplayInfo>> GetDisplays() => Result.Success<IReadOnlyList<DisplayInfo>>(
        [
            new("\\\\.\\DISPLAY1", true, new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 96, 96),
            new("\\\\.\\DISPLAY2", false, new PixelRect(1920, 0, 1280, 720), new PixelRect(1920, 0, 1280, 680), 96, 96)
        ]);

        public void Dispose()
        {
        }
    }

    private sealed class Folders : IFolderPicker
    {
        public string? PickFolder() => null;
    }

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
            throw new InvalidOperationException("WPF settings window check failed.", failure);
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(() => { }));

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
        public Repository Repository { get; } = new();

        public SettingsService Service { get; }

        public SettingsViewModel Model { get; private set; } = null!;

        public SettingsWindow Window { get; private set; } = null!;

        public Rig(Action<Repository>? configure = null, string palette = "Colors.Dark.xaml", double width = 480, double height = 680)
        {
            configure?.Invoke(Repository);
            Service = new SettingsService(Repository);
            Service.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
            Model = new SettingsViewModel(Service, new Environment(), new Folders(), work => work(), false);
            Window = new SettingsWindow { DataContext = Model };
            Window.Resources.MergedDictionaries.Add(Load("Themes", palette));
            Window.Resources.MergedDictionaries.Add(Load("Themes", "Typography.xaml"));
            Window.Resources.MergedDictionaries.Add(Load("Themes", "Spacing.xaml"));
            Window.Resources.MergedDictionaries.Add(Load("Themes", "Components.xaml"));
            Window.Resources.MergedDictionaries.Add(Load("Controls", "ControlStyles.xaml"));
            Window.WindowStartupLocation = WindowStartupLocation.Manual;
            Window.Left = -20000;
            Window.Top = -20000;
            Window.Width = width;
            Window.Height = height;
            Window.ShowActivated = false;
            Window.ShowInTaskbar = false;
            Window.Show();
            Window.UpdateLayout();
        }

        public T Named<T>(string name)
            where T : class =>
            (Window.FindName(name) as T) ?? throw new InvalidOperationException(name + " is missing.");

        public void Dispose()
        {
            Window.Close();
            Model.Dispose();
            Service.Dispose();
        }
    }

    private static string NameOf(DependencyObject element)
    {
        var name = AutomationProperties.GetName(element);
        if (!string.IsNullOrWhiteSpace(name))
            return name;
        return (element as ContentControl)?.Content as string ?? string.Empty;
    }

    private static bool Shown(UIElement element)
    {
        for (DependencyObject? current = element; current is UIElement ui; current = VisualTreeHelper.GetParent(current))
        {
            if (ui.Visibility != Visibility.Visible)
                return false;
        }

        return true;
    }

    [Fact]
    public void EveryControlThatCanBeUsedHasAnAccessibleName() => RunOnSta(() =>
    {
        using var rig = new Rig();

        var controls = Descendants<ButtonBase>(rig.Window).Where(c => c is not RepeatButton && Shown(c)).ToList();

        Assert.True(controls.Count >= 12, $"Only {controls.Count} controls were found.");
        Assert.All(controls, c => Assert.False(string.IsNullOrWhiteSpace(NameOf(c)), $"{c.GetType().Name} has no name."));
    });

    [Fact]
    public void ThereIsNoFreeTextFieldBecauseEverythingIsAChoiceASwitchOrAFolderDialog() => RunOnSta(() =>
    {
        using var rig = new Rig();

        Assert.Empty(Descendants<TextBox>(rig.Window));
        Assert.Empty(Descendants<PasswordBox>(rig.Window));
        Assert.Empty(Descendants<ComboBox>(rig.Window));
    });

    [Fact]
    public void TheWindowIsAnOrdinaryActivatingWindowWithATaskbarEntryAndATitle() => RunOnSta(() =>
    {
        var window = new SettingsWindow();

        Assert.Equal("Lunar settings", window.Title);
        Assert.Equal("Lunar settings", AutomationProperties.GetName(window));
        Assert.True(window.ShowInTaskbar);
        Assert.True(window.ShowActivated);
        Assert.NotEqual(WindowStyle.None, window.WindowStyle);
        Assert.False(window.Topmost);
        window.Close();
    });

    [Fact]
    public void EachSwitchDrawsTheSavedStateAndPressingItSavesAndMovesIt() => RunOnSta(() =>
    {
        using var rig = new Rig();
        var reduce = rig.Named<Button>("ReduceMotionSwitch");
        var eyes = rig.Named<Button>("EyesSwitch");
        var diagnostics = rig.Named<Button>("DiagnosticsSwitch");

        Assert.False(SwitchState.GetIsOn(reduce));
        Assert.True(SwitchState.GetIsOn(eyes));
        Assert.False(SwitchState.GetIsOn(diagnostics));

        reduce.Command.Execute(null);
        eyes.Command.Execute(null);
        Pump();

        Assert.True(SwitchState.GetIsOn(reduce));
        Assert.False(SwitchState.GetIsOn(eyes));
        Assert.True(rig.Repository.Stored.Appearance.ReduceMotion);
        Assert.False(rig.Repository.Stored.Appearance.EyeAnimationEnabled);
    });

    [Fact]
    public void AFailedSaveLeavesTheSwitchWhereItWasAndShowsWhy() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.Repository.FailSaveWith = ErrorCode.IoFailure;
        var reduce = rig.Named<Button>("ReduceMotionSwitch");
        Assert.Equal(Visibility.Collapsed, rig.Named<Border>("ProblemCard").Visibility);

        reduce.Command.Execute(null);
        Pump();
        rig.Window.UpdateLayout();

        Assert.False(SwitchState.GetIsOn(reduce));
        Assert.Equal(Visibility.Visible, rig.Named<Border>("ProblemCard").Visibility);
        Assert.Equal(SettingsText.SaveFailed, rig.Named<TextBlock>("ProblemText").Text);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(rig.Named<Border>("ProblemCard")));
    });

    [Fact]
    public void StartWithWindowsIsShownSwitchedOffDisabledAndSaysWhy() => RunOnSta(() =>
    {
        using var rig = new Rig();
        var startup = rig.Named<Button>("StartupSwitch");

        Assert.False(startup.IsEnabled);
        Assert.False(SwitchState.GetIsOn(startup));
        Assert.Contains("not available", AutomationProperties.GetName(startup), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SettingsText.StartupUnavailable, rig.Named<TextBlock>("StartupHint").Text);
    });

    [Fact]
    public void ThemeAndDisplayChoicesAreRadioButtonsThatSayWhichOneIsSelected() => RunOnSta(() =>
    {
        using var rig = new Rig(r => r.Stored = LunarSettings.Defaults with { Appearance = new AppearanceSettings { Theme = ThemePreference.Dark } });

        var themes = Descendants<RadioButton>(rig.Named<ItemsControl>("ThemeList")).ToList();
        var monitors = Descendants<RadioButton>(rig.Named<ItemsControl>("MonitorList")).ToList();

        Assert.Equal(["Match Windows", "Dark", "Light"], themes.Select(NameOf));
        Assert.Equal([false, true, false], themes.Select(t => t.IsChecked == true));
        Assert.Equal(3, monitors.Count); // main display plus two connected
        Assert.Equal([true, false, false], monitors.Select(m => m.IsChecked == true));
        foreach (var radio in themes)
        {
            var selection = (System.Windows.Automation.Provider.ISelectionItemProvider)UIElementAutomationPeer.CreatePeerForElement(radio).GetPattern(PatternInterface.SelectionItem);
            Assert.Equal(radio.IsChecked == true, selection.IsSelected);
        }
    });

    [Fact]
    public void ChoosingAThemeSavesItAndTheWindowShowsTheNewSelection() => RunOnSta(() =>
    {
        using var rig = new Rig();
        var light = Descendants<RadioButton>(rig.Named<ItemsControl>("ThemeList")).Single(r => NameOf(r) == "Light");

        light.Command.Execute(null);
        Pump();
        rig.Window.UpdateLayout();

        Assert.Equal(ThemePreference.Light, rig.Repository.Stored.Appearance.Theme);
        var after = Descendants<RadioButton>(rig.Named<ItemsControl>("ThemeList")).ToList();
        Assert.Equal([false, false, true], after.Select(t => t.IsChecked == true));
    });

    [Fact]
    public void TheOffsetButtonsMoveItInStepsAndTheValueIsShown() => RunOnSta(() =>
    {
        using var rig = new Rig();
        Assert.Equal("8 px", rig.Named<TextBlock>("OffsetValue").Text);

        rig.Named<Button>("OffsetUpButton").Command.Execute(null);
        Pump();

        Assert.Equal("12 px", rig.Named<TextBlock>("OffsetValue").Text);
        Assert.Equal(12, rig.Repository.Stored.Island.TopOffsetDip);
    });

    [Fact]
    public void TheFolderRowShowsTheDefaultOrTheSavedFolderAndTheClearButtonFollows() => RunOnSta(() =>
    {
        using var rig = new Rig();
        Assert.Equal(SettingsText.FolderDefault, rig.Named<TextBlock>("FolderText").Text);
        Assert.False(rig.Named<Button>("ClearFolderButton").IsEnabled);

        using var withFolder = new Rig(r => r.Stored = LunarSettings.Defaults with { Conversion = new ConversionSettings { OutputDirectory = @"D:\Converted" } });
        Assert.Equal(@"D:\Converted", withFolder.Named<TextBlock>("FolderText").Text);
        Assert.True(withFolder.Named<Button>("ClearFolderButton").IsEnabled);
    });

    [Fact]
    public void ResetAsksInTheWindowAndOnlyConfirmingResetsEverything() => RunOnSta(() =>
    {
        using var rig = new Rig(r => r.Stored = LunarSettings.Defaults with { Usage = new UsageSettings { Enabled = true }, Appearance = new AppearanceSettings { ReduceMotion = true } });
        var confirm = rig.Named<StackPanel>("ResetConfirm");
        Assert.Equal(Visibility.Collapsed, confirm.Visibility);

        rig.Named<Button>("AskResetButton").Command.Execute(null);
        rig.Window.UpdateLayout();
        Assert.Equal(Visibility.Visible, confirm.Visibility);
        Assert.Equal(SettingsText.ResetQuestion, Descendants<TextBlock>(confirm).First().Text);
        Assert.True(rig.Repository.Stored.Usage.Enabled);

        rig.Named<Button>("CancelResetButton").Command.Execute(null);
        rig.Window.UpdateLayout();
        Assert.Equal(Visibility.Collapsed, confirm.Visibility);
        Assert.True(rig.Repository.Stored.Usage.Enabled);

        rig.Named<Button>("AskResetButton").Command.Execute(null);
        rig.Named<Button>("ConfirmResetButton").Command.Execute(null);
        Pump();

        Assert.Equal(LunarSettings.Defaults, rig.Repository.Stored);
    });

    [Fact]
    public void ARepairNoticeShowsOnlyWhenSomethingWasRepairedAndCanBeDismissed() => RunOnSta(() =>
    {
        using var quiet = new Rig();
        Assert.Equal(Visibility.Collapsed, quiet.Named<Border>("NoticeCard").Visibility);

        using var repaired = new Rig(r => r.Recovery = SettingsRecovery.RestoredFromBackup);
        var card = repaired.Named<Border>("NoticeCard");
        Assert.Equal(Visibility.Visible, card.Visibility);
        Assert.Contains("last good copy", repaired.Named<TextBlock>("NoticeText").Text, StringComparison.Ordinal);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(card));

        repaired.Named<Button>("DismissNoticeButton").Command.Execute(null);
        repaired.Window.UpdateLayout();
        Assert.Equal(Visibility.Collapsed, card.Visibility);
    });

    [Fact]
    public void ANewerFileMakesEveryControlUnavailableAndSaysWhy() => RunOnSta(() =>
    {
        using var rig = new Rig(r =>
        {
            r.Recovery = SettingsRecovery.NewerVersionIgnored;
            r.FailSaveWith = ErrorCode.Unsupported;
        });

        Assert.Equal(SettingsText.ReadOnly, rig.Named<TextBlock>("NoticeText").Text);
        foreach (var name in new[] { "ReduceMotionSwitch", "EyesSwitch", "DiagnosticsSwitch", "OffsetUpButton", "OffsetDownButton", "ChooseFolderButton", "AskResetButton" })
            Assert.False(rig.Named<Button>(name).IsEnabled, name);
        Assert.All(Descendants<RadioButton>(rig.Window), r => Assert.False(r.IsEnabled));
    });

    [Fact]
    public void DiagnosticsSaysItAppliesFromTheNextStartOnlyWhileTheSavedChoiceIsNotInUse() => RunOnSta(() =>
    {
        using var rig = new Rig();
        var note = rig.Named<TextBlock>("DiagnosticsRestart");
        Assert.NotEqual(Visibility.Visible, note.Visibility);

        rig.Named<Button>("DiagnosticsSwitch").Command.Execute(null);
        Pump();
        rig.Window.UpdateLayout();

        Assert.Equal(Visibility.Visible, note.Visibility);
        Assert.Equal(SettingsText.DiagnosticsRestart, note.Text);
        Assert.Contains("never contain file names", rig.Named<TextBlock>("DiagnosticsHint").Text, StringComparison.Ordinal);
    });

    [Fact]
    public void EveryUsableControlIsReachableByTheKeyboard() => RunOnSta(() =>
    {
        using var rig = new Rig();

        var controls = Descendants<ButtonBase>(rig.Window).Where(c => c is not RepeatButton && Shown(c) && c.IsEnabled).ToList();

        Assert.All(controls, c =>
        {
            Assert.True(c.Focusable, NameOf(c));
            Assert.True(c.IsTabStop, NameOf(c));
        });
    });

    [Fact]
    public void EscapeClosesTheWindow() => RunOnSta(() =>
    {
        using var rig = new Rig();
        var closed = false;
        rig.Window.Closed += (_, _) => closed = true;

        var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(rig.Window)!, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        rig.Window.RaiseEvent(escape);

        Assert.True(closed);
        Assert.True(escape.Handled);
    });

    [Theory]
    [InlineData("Colors.Dark.xaml", 480, 680)]
    [InlineData("Colors.Light.xaml", 480, 680)]
    [InlineData("Colors.HighContrast.xaml", 480, 680)]
    [InlineData("Colors.Dark.xaml", 380, 520)]
    public void NothingIsCutOffSidewaysAndTheContentScrollsVertically(string palette, double width, double height) => RunOnSta(() =>
    {
        using var rig = new Rig(palette: palette, width: width, height: height);
        var scroll = Descendants<ScrollViewer>(rig.Window).First();

        Assert.True(scroll.ExtentWidth <= scroll.ViewportWidth + 0.5, $"Content is {scroll.ExtentWidth} wide in a {scroll.ViewportWidth} viewport.");
        foreach (var text in Descendants<TextBlock>(rig.Window).Where(Shown))
        {
            if (text.TextWrapping == TextWrapping.NoWrap && text.ActualWidth > 0)
                Assert.True(text.ActualWidth <= scroll.ViewportWidth, $"'{text.Text}' is wider than the window.");
        }
    });

    [Theory]
    [InlineData("Colors.Dark.xaml")]
    [InlineData("Colors.Light.xaml")]
    [InlineData("Colors.HighContrast.xaml")]
    public void TextIsReadableOnItsBackgroundInEveryPalette(string palette) => RunOnSta(() =>
    {
        using var rig = new Rig(palette: palette);

        var background = ((SolidColorBrush)rig.Window.FindResource("Lunar.Brush.Background")).Color;
        var surface = ((SolidColorBrush)rig.Window.FindResource("Lunar.Brush.Surface")).Color;
        var primary = ((SolidColorBrush)rig.Window.FindResource("Lunar.Brush.TextPrimary")).Color;
        var secondary = ((SolidColorBrush)rig.Window.FindResource("Lunar.Brush.TextSecondary")).Color;

        Assert.True(Contrast(primary, background) >= 4.5, "title text on the window");
        Assert.True(Contrast(primary, surface) >= 4.5, "body text on a card");
        Assert.True(Contrast(secondary, surface) >= 4.5, "hint text on a card");
    });

    [Fact]
    public void TheLauncherOpensOneWindowAndAskingAgainReusesItAndClosingReleasesIt() => RunOnSta(() =>
    {
        var repository = new Repository();
        using var service = new SettingsService(repository);
        using var launcher = new SettingsWindowLauncher(service, new Environment(), new Folders(), work => work(), diagnosticsInUse: false, log: null, interactive: false);
        Assert.False(launcher.IsOpen);

        launcher.Show();
        var first = launcher.Window;
        launcher.Show();

        Assert.True(launcher.IsOpen);
        Assert.NotNull(first);
        Assert.Same(first, launcher.Window);
        Assert.IsType<SettingsViewModel>(first.DataContext);

        first.Close();
        Assert.False(launcher.IsOpen);
        Assert.Null(launcher.Window);

        launcher.Show();
        Assert.NotSame(first, launcher.Window);
        Assert.True(launcher.IsOpen);
    });

    [Fact]
    public void ClosingTheWindowStopsItsViewModelFollowingTheSettings() => RunOnSta(() =>
    {
        var repository = new Repository();
        using var service = new SettingsService(repository);
        using var launcher = new SettingsWindowLauncher(service, new Environment(), new Folders(), work => work(), diagnosticsInUse: false, log: null, interactive: false);
        launcher.Show();
        var model = (SettingsViewModel)launcher.Window!.DataContext;
        var raised = 0;
        model.PropertyChanged += (_, _) => raised++;

        launcher.Window.Close();
        service.UpdateAsync(s => s with { LaunchAtStartup = true }, CancellationToken.None).GetAwaiter().GetResult();

        Assert.Equal(0, raised);
    });

    [Fact]
    public void DisposingTheLauncherClosesTheOpenWindow() => RunOnSta(() =>
    {
        var repository = new Repository();
        using var service = new SettingsService(repository);
        var launcher = new SettingsWindowLauncher(service, new Environment(), new Folders(), work => work(), diagnosticsInUse: false, log: null, interactive: false);
        launcher.Show();
        var window = launcher.Window!;

        launcher.Dispose();
        launcher.Show(); // after dispose nothing opens

        Assert.False(window.IsVisible);
        Assert.False(launcher.IsOpen);
    });

    private static double Contrast(Color a, Color b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        var (hi, lo) = la > lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255d;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }

    // FE-24: names, tab stops and tab order of the real page, in English and in Indonesian (the longer words must fit and be named too).
    [Theory]
    [InlineData("en")]
    [InlineData("id")]
    public void TheSettingsWindowIsNamedAndReachableByKeyboard(string language) => RunOnSta(() =>
    {
        Lunar.Desktop.Resources.Strings.Use(language);
        try
        {
            using var rig = new Rig();
            Lunar.Desktop.UITests.Accessibility.AutomationAudit.AssertAccessible(rig.Window);
        }
        finally
        {
            Lunar.Desktop.Resources.Strings.Use("en");
        }
    });

    // FE-24, ADR-0017: the language is a choice like the theme; it is saved at once and applies at the next start, and the window says so.
    [Fact]
    public void ChoosingALanguageSavesItAndSaysItAppliesAtTheNextStart() => RunOnSta(() =>
    {
        using var rig = new Rig(r => r.Stored = LunarSettings.Defaults with { Locale = "en" });
        var choices = () => Descendants<RadioButton>(rig.Named<ItemsControl>("LanguageList")).ToList();

        Assert.Equal(["Match Windows", "English", "Bahasa Indonesia"], choices().Select(NameOf));
        Assert.Equal([false, true, false], choices().Select(c => c.IsChecked == true));
        Assert.Equal(Visibility.Collapsed, rig.Named<TextBlock>("LanguageRestart").Visibility);

        choices().Single(c => NameOf(c) == "Bahasa Indonesia").Command.Execute(null);
        Pump();
        rig.Window.UpdateLayout();

        Assert.Equal("id", rig.Repository.Stored.Locale);
        Assert.Equal([false, false, true], choices().Select(c => c.IsChecked == true));
        Assert.Equal(Visibility.Visible, rig.Named<TextBlock>("LanguageRestart").Visibility);
    });

    [Fact]
    public void FollowingWindowsSavesNoLanguageAtAll() => RunOnSta(() =>
    {
        using var rig = new Rig(r => r.Stored = LunarSettings.Defaults with { Locale = "id" });

        Descendants<RadioButton>(rig.Named<ItemsControl>("LanguageList")).Single(c => NameOf(c) == "Match Windows").Command.Execute(null);
        Pump();

        Assert.Null(rig.Repository.Stored.Locale);
    });

    // FE-24: Indonesian words are longer; the page must still fit the island (and nothing may be cut off).
    [Fact]
    public void TheSettingsWindowStillFitsItsSmallestSizeInIndonesian() => RunOnSta(() =>
    {
        Lunar.Desktop.Resources.Strings.Use("id");
        try
        {
            foreach (var palette in new[] { "Colors.Dark.xaml", "Colors.Light.xaml", "Colors.HighContrast.xaml" })
            {
                using var rig = new Rig(palette: palette, width: 380, height: 520);
                var scroll = Descendants<ScrollViewer>(rig.Window).First();
                Assert.True(scroll.ExtentWidth <= scroll.ViewportWidth + 0.5, $"Content is {scroll.ExtentWidth} wide in a {scroll.ViewportWidth} viewport.");
            }
        }
        finally
        {
            Lunar.Desktop.Resources.Strings.Use("en");
        }
    });
}
