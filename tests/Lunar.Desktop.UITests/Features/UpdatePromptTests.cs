using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Lunar.Application.Abstractions.Shell;
using Lunar.Application.Abstractions.Updates;
using Lunar.Application.Settings;
using Lunar.Application.Settings.Models;
using Lunar.Application.Shell.Models;
using Lunar.Application.Updates;
using Lunar.Desktop.Features.Settings;
using Lunar.Desktop.Features.Updates;
using Lunar.Desktop.Resources;
using Lunar.Desktop.Shell;
using Lunar.Desktop.UITests.Themes;
using Lunar.Domain.Shared;
using Lunar.Domain.Updates;
using Xunit;

namespace Lunar.Desktop.UITests.Features;

// FE-38, ADR-0025: the new-version prompt and the update line in Settings over the real UpdateService, with a stand-in feed and installer.
// A real release downloaded and installed on this PC, and Narrator, are manual checks (FE-38 evidence).
public sealed class UpdatePromptTests
{
    private static readonly ReleaseInfo Newer = new(new AppVersion(1, 0, 6), new Uri("https://github.com/rafaelevan-k/lunar/releases/download/v1.0.6/Lunar.msi"), new string('a', 64), 1000);

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
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
            throw new InvalidOperationException("WPF update prompt check failed.", failure);
    }

    private static void Pump(Func<bool>? done = null, int seconds = 10)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        do
        {
            for (var i = 0; i < 5; i++)
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
            if (done is null || done())
                return;
            Thread.Sleep(2);
        }
        while (DateTime.UtcNow < end);

        Assert.True(done(), "The prompt did not reach the expected state in time.");
    }

    private static ResourceDictionary Load(params string[] relative)
    {
        var path = Path.Combine([ThemeTokenTests.DesktopDirectory, .. relative]);
        var text = File.ReadAllText(path).Replace("\"clr-namespace:Lunar.Desktop.Controls\"", "\"clr-namespace:Lunar.Desktop.Controls;assembly=Lunar.Desktop\"")
            .Replace("\"clr-namespace:Lunar.Desktop.Shell\"", "\"clr-namespace:Lunar.Desktop.Shell;assembly=Lunar.Desktop\"");
        return (ResourceDictionary)XamlReader.Parse(text);
    }

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            Settings = new SettingsService(new RecordsPageTests.Repository());
            Service = new UpdateService(Feed, Installer, Settings, new AppVersion(1, 0, 5));
            Model = new UpdatePromptViewModel(Service, work =>
            {
                if (dispatcher.CheckAccess())
                    work();
                else
                    dispatcher.BeginInvoke(work);
            }, () => Busy, () => Exits++);
        }

        public FakeFeed Feed { get; } = new();

        public FakeInstaller Installer { get; } = new();

        public SettingsService Settings { get; }

        public UpdateService Service { get; }

        public UpdatePromptViewModel Model { get; }

        public bool Busy { get; set; }

        public int Exits { get; private set; }

        public void Offer()
        {
            Feed.Next = Result.Success<ReleaseInfo?>(Newer);
            var check = Service.CheckAsync(CancellationToken.None);
            Pump(() => check.IsCompleted && Model.Phase == UpdatePhase.Available);
        }

        public void Install()
        {
            Model.InstallCommand.Execute(null);
            Pump(() => Model.InstallCommand is AsyncRelayCommand { IsRunning: false });
        }

        public void Dispose()
        {
            Model.Dispose();
            Service.Dispose();
            Settings.Dispose();
        }
    }

    // ---- the model ------------------------------------------------------------------------------------------------------

    [Fact]
    public void NothingIsShownUntilANewerVersionIsKnown() => RunOnSta(() =>
    {
        using var rig = new Rig();
        Assert.False(rig.Model.IsPromptVisible);
        Assert.Equal(Strings.Format("Update.UpToDate", "1.0.5"), rig.Model.StatusText);

        rig.Offer();

        Assert.True(rig.Model.IsPromptVisible);
        Assert.True(rig.Model.CanInstall);
        Assert.Equal(Strings.Format("Update.Available", "1.0.6", "1.0.5"), rig.Model.Message);
        Assert.Equal(Strings.Get("Update.InstallNow"), rig.Model.InstallText);
    });

    [Fact]
    public void AFocusSessionOrRecordingHoldsThePromptBackUntilItEnds() => RunOnSta(() =>
    {
        using var rig = new Rig { Busy = true };
        rig.Offer();

        Assert.False(rig.Model.IsPromptVisible);
        Assert.False(rig.Model.CanInstall); // not even from Settings: an install closes Lunar
        Assert.Equal(Strings.Format("Update.AvailableBusy", "1.0.6"), rig.Model.StatusText);

        rig.Busy = false;
        rig.Model.Refresh();

        Assert.True(rig.Model.IsPromptVisible);
        Assert.True(rig.Model.CanInstall);
    });

    [Fact]
    public void LaterHidesThePromptButSettingsStillOffersTheInstall() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.Offer();

        rig.Model.LaterCommand.Execute(null);
        Pump(() => !rig.Model.IsPromptVisible);

        Assert.False(rig.Model.IsPromptVisible);
        Assert.True(rig.Model.CanInstall);
        Assert.Equal(Strings.Format("Update.Available", "1.0.6", "1.0.5"), rig.Model.StatusText);
    });

    [Fact]
    public void InstallNowHandsOverAndThenClosesLunar() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.Offer();

        rig.Install();

        Assert.Equal(1, rig.Installer.Starts);
        Assert.Equal(UpdatePhase.Installing, rig.Model.Phase);
        Assert.Equal(Strings.Get("Update.Installing"), rig.Model.Message);
        Assert.Equal(1, rig.Exits);
    });

    [Fact]
    public void AFailureSaysWhyOffersTryAgainAndDoesNotCloseLunar() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.Offer();
        rig.Installer.DownloadResult = Result.Failure<VerifiedInstaller>(new OperationError(ErrorCode.InvalidInput, "mismatch", retryable: true));

        rig.Install();

        Assert.True(rig.Model.HasFailed);
        Assert.True(rig.Model.IsPromptVisible);
        Assert.Equal(Strings.Get("Update.FailedMismatch"), rig.Model.Message);
        Assert.Equal(Strings.Get("Update.TryAgain"), rig.Model.InstallText);
        Assert.Equal(0, rig.Exits);

        rig.Installer.DownloadResult = null;
        rig.Install();
        Assert.Equal(1, rig.Exits);
    });

    [Theory]
    [InlineData(ErrorCode.InvalidInput, "Update.FailedMismatch")]
    [InlineData(ErrorCode.DeviceUnavailable, "Update.FailedNetwork")]
    [InlineData(ErrorCode.Timeout, "Update.FailedNetwork")]
    [InlineData(ErrorCode.IoFailure, "Update.FailedSave")]
    [InlineData(ErrorCode.DependencyMissing, "Update.FailedStart")]
    public void EveryFailureHasPlainLocalWords(ErrorCode code, string key) =>
        Assert.Equal(Strings.Get(key), UpdatePromptViewModel.Failure(new OperationError(code, "English text from the adapter", retryable: true)));

    [Fact]
    public void CancelStopsTheDownloadAndGoesBackToTheOffer() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.Offer();
        rig.Installer.Hold = true;

        rig.Model.InstallCommand.Execute(null);
        Pump(() => rig.Model.IsDownloading);
        Assert.True(rig.Model.CancelCommand.CanExecute(null));
        rig.Model.CancelCommand.Execute(null);
        Pump(() => rig.Model.Phase == UpdatePhase.Available);

        Assert.True(rig.Model.IsPromptVisible);
        Assert.Equal(0, rig.Installer.Starts);
        Assert.Equal(0, rig.Exits);
    });

    [Fact]
    public void TheSettingsLineFollowsTheSwitch() => RunOnSta(() =>
    {
        using var rig = new Rig();
        var turnOff = rig.Settings.UpdateAsync(s => s with { Updates = new UpdateSettings { Check = false } }, CancellationToken.None);
        Pump(() => turnOff.IsCompleted);
        rig.Model.Refresh();

        Assert.Equal(Strings.Format("Update.Off", "1.0.5"), rig.Model.StatusText);
    });

    // ---- the window -----------------------------------------------------------------------------------------------------

    [Fact]
    public void ThePresenterShowsANonActivatingWindowInTheCornerAndClosesItOnLater() => RunOnSta(() =>
    {
        using var rig = new Rig();
        var dpi = WindowDpi();
        var display = new DisplayInfo("m", true, new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), dpi, dpi);
        using var presenter = new UpdatePromptPresenter(rig.Model, new FakeEnvironment(display), new IslandSettings(), reduceMotion: true);
        Assert.Null(presenter.Window);

        rig.Offer();
        Pump(() => presenter.Window is not null);
        var window = presenter.Window!;
        Pump();

        Assert.True(window.Topmost);
        Assert.False(window.ShowActivated);
        Assert.False(window.ShowInTaskbar);
        Assert.True(NonActivatingWindowStyle.IsApplied(new WindowInteropHelper(window).Handle));
        Assert.Equal(1.0, window.Opacity); // reduced motion: no fade
        var scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        Assert.True((window.Left + window.ActualWidth) * scale <= 1920 + 1);
        Assert.True((window.Top + window.ActualHeight) * scale <= 1040 + 1);
        Assert.Equal(Strings.Get("Update.Title"), AutomationProperties.GetName(window));
        Assert.True(((Button)window.FindName("InstallButton")).IsVisible);
        Assert.True(((Button)window.FindName("LaterButton")).IsVisible);
        Assert.False(((Button)window.FindName("CancelButton")).IsVisible);

        ((Button)window.FindName("LaterButton")).Command.Execute(null);
        Pump(() => presenter.Window is null);
        Assert.False(window.IsVisible);
    });

    [Fact]
    public void WhileDownloadingOnlyCancelAndTheProgressShow() => RunOnSta(() =>
    {
        using var rig = new Rig();
        var dpi = WindowDpi();
        var display = new DisplayInfo("m", true, new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), dpi, dpi);
        using var presenter = new UpdatePromptPresenter(rig.Model, new FakeEnvironment(display), new IslandSettings(), reduceMotion: true);
        rig.Offer();
        rig.Installer.Hold = true;

        rig.Model.InstallCommand.Execute(null);
        Pump(() => rig.Model.IsDownloading && presenter.Window is not null);
        var window = presenter.Window!;
        Pump();

        Assert.True(((Button)window.FindName("CancelButton")).IsVisible);
        Assert.False(((Button)window.FindName("InstallButton")).IsVisible);
        Assert.False(((Button)window.FindName("LaterButton")).IsVisible);
        Assert.True(((ProgressBar)window.FindName("DownloadBar")).IsVisible);
        Assert.Equal(Strings.Get("Update.ProgressName"), AutomationProperties.GetName((ProgressBar)window.FindName("DownloadBar")));

        rig.Busy = true; // a focus session starting now does not hide a download under way
        rig.Model.Refresh();
        Assert.NotNull(presenter.Window);

        rig.Model.CancelCommand.Execute(null);
        Pump(() => rig.Model.Phase == UpdatePhase.Available);
    });

    [Fact]
    public void TheWindowUsesLunarsOwnColoursInEveryPalette() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.Offer();
        foreach (var palette in new[] { "Colors.Dark.xaml", "Colors.Light.xaml", "Colors.HighContrast.xaml" })
        {
            var window = new UpdatePromptWindow { DataContext = rig.Model, Left = -20000, Top = -20000 };
            var colours = Load("Themes", palette);
            foreach (var dictionary in new[] { colours, Load("Themes", "Typography.xaml"), Load("Themes", "Spacing.xaml"), Load("Themes", "Components.xaml"), Load("Controls", "ControlStyles.xaml") })
                window.Resources.MergedDictionaries.Add(dictionary);
            window.Show();
            Pump();
            try
            {
                var card = (Border)window.FindName("Card");
                Assert.Equal((Color)colours["Lunar.Color.Surface"], ((SolidColorBrush)card.Background).Color);
                Assert.Equal((Color)colours["Lunar.Color.Accent"], ((SolidColorBrush)card.BorderBrush).Color);
                Assert.Equal((Color)colours["Lunar.Color.TextPrimary"], ((SolidColorBrush)((TextBlock)window.FindName("TitleText")).Foreground).Color);
                foreach (var name in new[] { "InstallButton", "LaterButton" })
                    Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName((Button)window.FindName(name))));
            }
            finally
            {
                window.Close();
            }
        }
    });

    [Fact]
    public void SettingsHasTheSwitchAndTheSameInstall() => RunOnSta(() =>
    {
        using var rig = new Rig();
        using var settings = new SettingsViewModel(rig.Settings, new FakeEnvironment(DisplayInfo.Fallback), new NoFolders(), work => work(), diagnosticsInUse: false, updates: rig.Model);
        Assert.True(settings.UpdatesEnabled);
        Assert.Same(rig.Model, settings.Updates);

        settings.ToggleUpdatesCommand.Execute(null);
        Pump(() => !rig.Settings.Current.Updates.Check);

        Assert.False(settings.UpdatesEnabled);
        Assert.False(rig.Settings.Current.Updates.Check);
    });

    // ---- stand-ins ------------------------------------------------------------------------------------------------------

    private static int WindowDpi()
    {
        var probe = new Window { Width = 10, Height = 10, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, Left = -20000, Top = -20000 };
        probe.Show();
        var dpi = (int)Math.Round(VisualTreeHelper.GetDpi(probe).DpiScaleX * 96);
        probe.Close();
        return dpi;
    }

    internal sealed class FakeFeed : IUpdateFeed
    {
        public Result<ReleaseInfo?> Next { get; set; } = Result.Success<ReleaseInfo?>(null);

        public Task<Result<ReleaseInfo?>> GetLatestAsync(CancellationToken ct) => Task.FromResult(Next);
    }

    internal sealed class FakeInstaller : IUpdateInstaller
    {
        public Result<VerifiedInstaller>? DownloadResult { get; set; }

        /// <summary>The download waits until cancelled.</summary>
        public bool Hold { get; set; }

        public int Starts { get; private set; }

        public async Task<Result<VerifiedInstaller>> DownloadAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken ct)
        {
            progress?.Report(0.4);
            if (Hold)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    return Result.Cancelled<VerifiedInstaller>();
                }
            }

            return DownloadResult ?? Result.Success(new VerifiedInstaller(@"C:\Lunar\Updates\x\Lunar.msi", release.Version));
        }

        public Result StartInstall(VerifiedInstaller installer)
        {
            Starts++;
            return Result.Success();
        }

        public void RemoveLeftovers()
        {
        }
    }

    private sealed class FakeEnvironment(DisplayInfo display) : IWindowEnvironment
    {
        public event Action? DisplaysChanged
        {
            add { }
            remove { }
        }

        public Result Start() => Result.Success();

        public Result<IReadOnlyList<DisplayInfo>> GetDisplays() => Result.Success<IReadOnlyList<DisplayInfo>>([display]);

        public void Dispose()
        {
        }
    }

    private sealed class NoFolders : Lunar.Desktop.Features.Converter.IFolderPicker
    {
        public string? PickFolder() => null;
    }
}
