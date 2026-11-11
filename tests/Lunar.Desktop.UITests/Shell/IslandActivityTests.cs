using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Lunar.Application.Abstractions.Audio;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Shell;
using Lunar.Application.Recording;
using Lunar.Application.Settings;
using Lunar.Application.Settings.Models;
using Lunar.Application.Shell.Models;
using Lunar.Desktop.Behaviors;
using Lunar.Desktop.Features.Eyes;
using Lunar.Desktop.Features.Recorder;
using Lunar.Desktop.Navigation;
using Lunar.Desktop.Shell;
using Lunar.Desktop.UITests.Accessibility;
using Lunar.Desktop.UITests.Features;
using Lunar.Desktop.UITests.Themes;
using Lunar.Domain.Recording;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Desktop.UITests.Shell;

// FE-35: the real island window, shell, transition and position controllers over the real RecordingService. Whether the buttons leave the user's app in the
// foreground, a real microphone in the pill and Narrator are manual checks (FE-35 evidence).
public sealed class IslandActivityTests
{
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
            throw new InvalidOperationException("WPF island activity check failed.", failure);
    }

    private static void Pump(Func<bool> done, int seconds = 10)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!done() && DateTime.UtcNow < end)
        {
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
            Thread.Sleep(2);
        }

        Assert.True(done(), "The island did not reach the expected state in time.");
    }

    private static void RaiseMouseUp(UIElement element) =>
        element.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
        {
            RoutedEvent = System.Windows.Input.Mouse.MouseUpEvent,
            Source = element
        });

    private sealed class Rig : IDisposable
    {
        public Rig(bool reduceMotion = false)
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var dispatcher = Dispatcher.CurrentDispatcher;
            Action<Action> post = work =>
            {
                if (dispatcher.CheckAccess())
                    work();
                else
                    dispatcher.BeginInvoke(work);
            };
            Settings = new SettingsService(new RecordsPageTests.Repository());
            Service = new RecordingService(Engine, new RecordsPageTests.FakeCatalog(), new RecordsPageTests.FakeFiles(), Settings, new RecordsPageTests.Clock(), null, Time);
            Records = new RecordsViewModel(Service, new RecordsPageTests.FakeFolders(), new RecordsPageTests.FakeRevealer(), new RecordsPageTests.FakeLauncher(), post, Time);
            Island = new IslandViewModel();
            var navigation = new PageNavigationViewModel(["One", "Two", "Three", "Four", "Five", "Six"]);
            var swiper = new SwiperView();
            Eyes = new EyesView();
            Shell = new IslandShellView(Eyes, swiper, Island, reduceMotion);
            Window = new IslandWindow { DataContext = Island };
            foreach (var dictionary in new[] { "Colors.Dark.xaml", "Typography.xaml", "Spacing.xaml", "Components.xaml" })
                Window.Resources.MergedDictionaries.Add(Load("Themes", dictionary));
            Window.Resources.MergedDictionaries.Add(Load("Controls", "ControlStyles.xaml"));
            Window.ViewContent = Shell;
            swiper.Resources.MergedDictionaries.Clear();
            swiper.Initialize(navigation, IslandShellView.CreatePlaceholderPages(navigation.Titles), reduceMotion);
            Pill = new RecordingPillView { DataContext = Records, ExpandCommand = Island.ToggleCommand };
            _ = new WindowInteropHelper(Window).EnsureHandle();
            var scale = VisualTreeHelper.GetDpi(Window).DpiScaleX;
            var dpi = (int)Math.Round(scale * 96);
            var display = new DisplayInfo("DISPLAY1", true, new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1020), dpi, dpi);
            Position = new IslandPositionController(Window, Island, new Environment(display), new Log(), new IslandSettings { TopOffsetDip = 300 });
            Transition = new IslandTransitionController(Window, Island, reduceMotion);
            Activity = new IslandActivityController(Island, Shell, Records, Pill);
            Position.Start();
            Window.Show();
            Settle();
            PillBorder = Assert.IsType<Border>(Window.Content);
        }

        public RecordsPageTests.FakeEngine Engine { get; } = new();

        public RecordsPageTests.ManualTime Time { get; } = new();

        public SettingsService Settings { get; }

        public RecordingService Service { get; }

        public RecordsViewModel Records { get; }

        public IslandViewModel Island { get; }

        public EyesView Eyes { get; }

        public IslandShellView Shell { get; }

        public IslandWindow Window { get; }

        public RecordingPillView Pill { get; }

        public IslandPositionController Position { get; }

        public IslandTransitionController Transition { get; }

        public IslandActivityController Activity { get; }

        public Border PillBorder { get; }

        public void Settle()
        {
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(() => { }));
            Window.UpdateLayout();
        }

        public void StartRecording()
        {
            var started = Service.StartAsync(CancellationToken.None);
            Pump(() => started.IsCompleted && Records.ShowsInIsland);
            Settle();
        }

        public void StopRecording()
        {
            var stopped = Service.StopAsync();
            Pump(() => stopped.IsCompleted && !Records.ShowsInIsland);
            Settle();
        }

        public void WaitForPillWidth(double width)
        {
            Pump(() => Math.Abs(PillBorder.ActualWidth - width) < 0.5 && !PillBorder.HasAnimatedProperties);
            Settle();
        }

        public void Expand()
        {
            Island.Toggle();
            Pump(() => Island.State == IslandVisualState.Expanded);
            Settle();
        }

        public void Collapse()
        {
            Island.Toggle();
            Pump(() => Island.State == IslandVisualState.Idle);
            Settle();
        }

        public void Dispose()
        {
            Activity.Dispose();
            Transition.Dispose();
            Position.Dispose();
            Shell.Dispose();
            Window.Close();
            Records.Dispose();
            Service.Dispose();
            Settings.Dispose();
        }
    }

    [Theory]
    [InlineData(false, false, IslandActivity.Eyes)]
    [InlineData(true, false, IslandActivity.Recording)]
    [InlineData(false, true, IslandActivity.FocusCountdown)]
    [InlineData(true, true, IslandActivity.Recording)]
    public void ARecordingComesBeforeACountdownAndBothBeforeTheEyes(bool recording, bool focus, IslandActivity expected) =>
        Assert.Equal(expected, IslandActivitySelector.Choose(recording, focus));

    [Fact]
    public void OnlyTheRecordingPillIsWider()
    {
        Assert.Equal(232d, IslandLayout.IdleWidthFor(IslandActivity.Recording));
        Assert.Equal(160d, IslandLayout.IdleWidthFor(IslandActivity.Eyes));
        Assert.Equal(160d, IslandLayout.IdleWidthFor(IslandActivity.FocusCountdown));
    }

    [Fact]
    public void RecordingReplacesTheEyesWidensThePillAndNeverMovesTheWindow() => RunOnSta(() =>
    {
        using var rig = new Rig();
        Assert.True(rig.Eyes.IsVisible);
        var left = rig.Window.Left;
        var width = rig.Window.Width;

        rig.StartRecording();
        rig.WaitForPillWidth(232);

        Assert.False(rig.Eyes.IsVisible); // its timers stop with it
        Assert.True(rig.Pill.IsVisible);
        Assert.Equal(left, rig.Window.Left);
        Assert.Equal(width, rig.Window.Width);

        rig.StopRecording();
        rig.WaitForPillWidth(160);

        Assert.True(rig.Eyes.IsVisible);
        Assert.False(rig.Pill.IsVisible);
        Assert.Equal(left, rig.Window.Left);
    });

    [Fact]
    public void ExpandingBringsTheEyesBackAndCollapsingShowsTheRecordingAgainAtItsWidth() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.StartRecording();
        rig.WaitForPillWidth(232);

        rig.Island.Toggle();
        Assert.True(rig.Eyes.Visibility == Visibility.Visible); // at the start of the transition
        Pump(() => rig.Island.State == IslandVisualState.Expanded);
        rig.Settle();
        Assert.True(rig.Eyes.IsVisible);
        Assert.False(rig.Pill.IsVisible);

        rig.Collapse();
        rig.WaitForPillWidth(232);

        Assert.False(rig.Eyes.IsVisible);
        Assert.True(rig.Pill.IsVisible);
        Assert.Equal(RecordingPhase.Recording, rig.Service.Current.Phase);
    });

    [Fact]
    public void AWidthChangeWhileExpandedWaitsForTheCollapse() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.Expand();

        rig.StartRecording();
        Assert.False(rig.Pill.IsVisible);
        Assert.True(rig.PillBorder.ActualWidth > 300);

        rig.Collapse();
        rig.WaitForPillWidth(232);
        Assert.True(rig.Pill.IsVisible);
    });

    [Fact]
    public void PauseAndStopWorkWithoutOpeningTheIsland() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.StartRecording();
        rig.WaitForPillWidth(232);
        var pause = (ButtonBase)rig.Pill.FindName("PauseButton");
        var stop = (ButtonBase)rig.Pill.FindName("StopButton");

        // A release over a button stays with the button: the pill below never sees it.
        RaiseMouseUp(pause);
        RaiseMouseUp(stop);
        Assert.Equal(IslandVisualState.Idle, rig.Island.State);

        pause.Command.Execute(null);
        Pump(() => rig.Service.Current.Phase == RecordingPhase.Paused);
        rig.Settle();

        Assert.Equal(IslandVisualState.Idle, rig.Island.State);
        Assert.Equal("", ((Lunar.Desktop.Controls.IconButton)pause).Glyph); // now offers play
        Assert.Equal(Lunar.Desktop.Resources.Strings.Get("Records.ResumeName"), AutomationAudit.NameOf(pause));
        Assert.All(rig.Records.Levels ?? new float[VoiceLevelBands.Count], b => Assert.Equal(0f, b));

        stop.Command.Execute(null);
        Pump(() => rig.Service.Current.Phase == RecordingPhase.Idle);
        rig.Settle();

        Assert.Equal(IslandVisualState.Idle, rig.Island.State);
        Assert.NotNull(rig.Service.Current.LastSaved);
    });

    [Fact]
    public void AClickOutsideTheButtonsOpensTheIsland() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.StartRecording();
        rig.WaitForPillWidth(232);

        RaiseMouseUp((FrameworkElement)rig.Pill.FindName("Levels"));

        Assert.Equal(IslandVisualState.Expanding, rig.Island.State);
    });

    [Fact]
    public void TheTimeSitsAtTheFarRightAndTheLinesFollowTheMicrophone() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.StartRecording();
        rig.WaitForPillWidth(232);
        rig.Engine.Session!.Recorded = TimeSpan.FromSeconds(83);
        var loud = Enumerable.Repeat(0.8f, VoiceLevelBands.Count).ToArray();
        rig.Engine.Session.RaiseLevels(new VoiceLevels(loud));
        rig.Time.Fire();
        rig.Settle();

        var elapsed = (TextBlock)rig.Pill.FindName("ElapsedText");
        var levels = (FrameworkElement)rig.Pill.FindName("Levels");
        var stop = (FrameworkElement)rig.Pill.FindName("StopButton");
        Assert.Equal("01:23", elapsed.Text);
        Assert.Same(loud, ((Lunar.Desktop.Controls.VoiceLevelsView)levels).Levels);
        Assert.True(stop.TranslatePoint(new Point(stop.ActualWidth, 0), rig.Pill).X <= levels.TranslatePoint(default, rig.Pill).X);
        Assert.True(levels.TranslatePoint(new Point(levels.ActualWidth, 0), rig.Pill).X <= elapsed.TranslatePoint(default, rig.Pill).X);
        Assert.True(elapsed.TranslatePoint(new Point(elapsed.ActualWidth, 0), rig.Pill).X <= rig.Pill.ActualWidth);
    });

    [Fact]
    public void ALongRecordingsTimeStillFits() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.StartRecording();
        rig.WaitForPillWidth(232);
        rig.Engine.Session!.Recorded = TimeSpan.FromHours(11) + TimeSpan.FromSeconds(59);
        rig.Time.Fire();
        rig.Settle();

        var elapsed = (TextBlock)rig.Pill.FindName("ElapsedText");
        Assert.Equal("11:00:59", elapsed.Text);
        Assert.True(elapsed.TranslatePoint(new Point(elapsed.ActualWidth, 0), rig.Pill).X <= rig.Pill.ActualWidth);
        Assert.True(((FrameworkElement)rig.Pill.FindName("Levels")).ActualWidth > 60);
    });

    [Fact]
    public void ReducedMotionPlacesThePillAtOnceAndAsksForTheStillDot() => RunOnSta(() =>
    {
        using var rig = new Rig(reduceMotion: true);

        rig.StartRecording();

        Assert.False(rig.PillBorder.HasAnimatedProperties);
        Assert.Equal(232d, rig.PillBorder.Width);
        Assert.True(Motion.GetReduceMotion(rig.Pill)); // inherited by the lines, which then draw a still dot
    });

    [Fact]
    public void ThePillIsNamedAndReachableByKeyboard() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.StartRecording();
        rig.WaitForPillWidth(232);

        Assert.Empty(AutomationAudit.Problems(rig.Pill));
        Assert.True(((FrameworkElement)rig.Pill.FindName("PauseButton")).ActualWidth >= 24);
        Assert.True(((FrameworkElement)rig.Pill.FindName("StopButton")).ActualHeight >= 24);
    });

    [Fact]
    public void DisposingLetsGoOfTheRecorderAndGivesTheEyesBack() => RunOnSta(() =>
    {
        var rig = new Rig();
        rig.StartRecording();
        rig.WaitForPillWidth(232);

        rig.Activity.Dispose();

        Assert.True(rig.Eyes.IsVisible);
        rig.Dispose();
    });

    private sealed class Environment(DisplayInfo display) : IWindowEnvironment
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

    private sealed class Log : IDiagnosticLog
    {
        public void Write(DiagnosticEvent diagnosticEvent)
        {
        }
    }
}
