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
using Lunar.Application.Focus;
using Lunar.Application.Recording;
using Lunar.Application.Settings;
using Lunar.Application.Settings.Models;
using Lunar.Application.Shell.Models;
using Lunar.Desktop.Behaviors;
using Lunar.Desktop.Features.Eyes;
using Lunar.Desktop.Features.Pomodoro;
using Lunar.Desktop.Features.Recorder;
using Lunar.Desktop.Navigation;
using Lunar.Desktop.Shell;
using Lunar.Desktop.UITests.Accessibility;
using Lunar.Desktop.UITests.Features;
using Lunar.Desktop.UITests.Themes;
using Lunar.Domain.Focus;
using Lunar.Domain.Recording;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Desktop.UITests.Shell;

// FE-35 and FE-37: the real island window, shell, transition and position controllers over the real RecordingService and PomodoroService. Whether the buttons leave the user's app in the
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
            FocusService = new PomodoroService(new PomodoroPageTests.FakeFocus(), new PomodoroPageTests.Marker(), Settings, FocusClock, null, FocusTime);
            Focus = new PomodoroPageViewModel(FocusService, new RecordsPageTests.FakeLauncher(), post, FocusTime);
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
            FocusPill = new FocusPillView { DataContext = Focus, ExpandCommand = Island.ToggleCommand };
            _ = new WindowInteropHelper(Window).EnsureHandle();
            var scale = VisualTreeHelper.GetDpi(Window).DpiScaleX;
            var dpi = (int)Math.Round(scale * 96);
            var display = new DisplayInfo("DISPLAY1", true, new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1020), dpi, dpi);
            Position = new IslandPositionController(Window, Island, new Environment(display), new Log(), new IslandSettings { TopOffsetDip = 300 });
            Transition = new IslandTransitionController(Window, Island, reduceMotion);
            Activity = new IslandActivityController(Island, Shell, Records, Pill, Focus, FocusPill);
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

        public PomodoroPageTests.MovableClock FocusClock { get; } = new();

        /// <summary>The focus clock. Its 250 ms timers are the page ticker, so <c>TimerRunning</c> says whether the time is being counted.</summary>
        public RecordsPageTests.ManualTime FocusTime { get; } = new();

        public PomodoroService FocusService { get; }

        public PomodoroPageViewModel Focus { get; }

        public FocusPillView FocusPill { get; }

        public TextBlock Countdown => (TextBlock)FocusPill.FindName("CountdownText");

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

        public void StartFocus(int minutes = 25)
        {
            var started = FocusService.StartAsync(minutes, CancellationToken.None);
            Pump(() => started.IsCompleted && Focus.IsActive);
            Settle();
        }

        public void RunFocus(Func<CancellationToken, Task> action, Func<bool> done)
        {
            var task = action(CancellationToken.None);
            Pump(() => task.IsCompleted && done());
            Settle();
        }

        /// <summary>Moves the clock on and lets the page ticker read the new time.</summary>
        public void Pass(TimeSpan time)
        {
            FocusClock.LocalNow += time;
            FocusTime.FireTicks(PomodoroPageViewModel.TickInterval);
            Settle();
        }

        public void UseStyle(Lunar.Application.Settings.Models.IslandStyle style)
        {
            IslandStyleApplier.Apply(style, Island, Shell, Window, Position);
            Settle();
        }

        public void WaitForPillSize(double width, double height)
        {
            Pump(() => Math.Abs(PillBorder.ActualWidth - width) < 0.5 && Math.Abs(PillBorder.ActualHeight - height) < 0.5 && !PillBorder.HasAnimatedProperties);
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
            Focus.Dispose();
            FocusService.Dispose();
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

    [Fact]
    public void TheCountdownReplacesTheEyesInTheNormalPill() => RunOnSta(() =>
    {
        using var rig = new Rig();
        var left = rig.Window.Left;
        Assert.False(rig.FocusTime.TimerRunning); // nothing ticks before a session

        rig.StartFocus();
        rig.WaitForPillWidth(160);

        Assert.Equal(IslandActivity.FocusCountdown, rig.Activity.Current);
        Assert.False(rig.Eyes.IsVisible); // its timers stop with it
        Assert.True(rig.FocusPill.IsVisible);
        Assert.Equal("25:00", rig.Countdown.Text);
        Assert.Equal(left, rig.Window.Left);

        rig.Pass(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(15));
        Assert.Equal("21:45", rig.Countdown.Text);
        var middle = rig.Countdown.TranslatePoint(new Point(rig.Countdown.ActualWidth / 2, 0), rig.FocusPill).X;
        Assert.InRange(middle, (rig.FocusPill.ActualWidth / 2) - 1, (rig.FocusPill.ActualWidth / 2) + 1); // centred in the pill, within layout rounding (CI saw 78.5 against 79)
    });

    [Fact]
    public void TheDigitsKeepTheirWidthAsTheyCount() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.StartFocus();
        rig.WaitForPillWidth(160);
        Assert.Equal(FontNumeralAlignment.Tabular, System.Windows.Documents.Typography.GetNumeralAlignment(rig.Countdown));

        rig.Pass(TimeSpan.FromMinutes(5)); // 20:00
        var wide = rig.Countdown.ActualWidth;
        rig.Pass(TimeSpan.FromMinutes(8) + TimeSpan.FromSeconds(49)); // 11:11, narrow digits in a proportional font

        Assert.Equal("11:11", rig.Countdown.Text);
        Assert.Equal(wide, rig.Countdown.ActualWidth, 1);
    });

    [Fact]
    public void FromOneHourTheCountdownShowsHours() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.StartFocus(90);
        rig.WaitForPillWidth(160);

        Assert.Equal("1:30:00", rig.Countdown.Text);
        Assert.True(rig.Countdown.TranslatePoint(new Point(rig.Countdown.ActualWidth, 0), rig.FocusPill).X <= rig.FocusPill.ActualWidth);
    });

    [Fact]
    public void PausedDigitsAreDimmedAndMarkedWithAPauseSymbol() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.StartFocus();
        rig.WaitForPillWidth(160);
        var glyph = (TextBlock)rig.FocusPill.FindName("PausedGlyph");
        var primary = ((SolidColorBrush)rig.Countdown.Foreground).Color;
        Assert.False(glyph.IsVisible);

        rig.RunFocus(rig.FocusService.PauseAsync, () => rig.Focus.IsPaused);

        Assert.True(glyph.IsVisible); // not by colour alone
        Assert.NotEqual(primary, ((SolidColorBrush)rig.Countdown.Foreground).Color);
        Assert.Equal(((SolidColorBrush)rig.Window.FindResource("Lunar.Brush.TextSecondary")).Color, ((SolidColorBrush)rig.Countdown.Foreground).Color);
        Assert.False(rig.FocusTime.TimerRunning); // a paused time needs no ticker
        Assert.True(rig.FocusPill.IsVisible);

        rig.RunFocus(rig.FocusService.ResumeAsync, () => !rig.Focus.IsPaused);

        Assert.False(glyph.IsVisible);
        Assert.Equal(primary, ((SolidColorBrush)rig.Countdown.Foreground).Color);
    });

    [Fact]
    public void ARecordingTakesThePillAndTheCountdownComesBackAfterIt() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.StartFocus();
        rig.StartRecording();
        rig.WaitForPillWidth(232);

        Assert.Equal(IslandActivity.Recording, rig.Activity.Current);
        Assert.True(rig.Pill.IsVisible);
        Assert.False(rig.FocusPill.IsVisible);

        rig.StopRecording();
        rig.WaitForPillWidth(160);

        Assert.Equal(IslandActivity.FocusCountdown, rig.Activity.Current);
        Assert.True(rig.FocusPill.IsVisible);
        Assert.Equal(PomodoroPhase.Running, rig.FocusService.Current.Phase); // the countdown kept running underneath
    });

    [Fact]
    public void ExpandingBringsTheEyesBackAndAClickOnTheCountdownExpands() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.StartFocus();
        rig.WaitForPillWidth(160);

        RaiseMouseUp(rig.Countdown);
        Assert.Equal(IslandVisualState.Expanding, rig.Island.State);
        Pump(() => rig.Island.State == IslandVisualState.Expanded);
        rig.Settle();
        Assert.True(rig.Eyes.IsVisible);
        Assert.False(rig.FocusPill.IsVisible);

        rig.Collapse();
        Assert.True(rig.FocusPill.IsVisible);
        Assert.False(rig.Eyes.IsVisible);
    });

    [Fact]
    public void WhenTheTimeIsOverTheEyesComeBackAndCelebrate() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.StartFocus(1);
        rig.WaitForPillWidth(160);
        var cheek = (TranslateTransform)rig.Eyes.FindName("LeftCheekOffset");

        rig.FocusClock.LocalNow += TimeSpan.FromMinutes(1);
        rig.FocusTime.FireTicks(TimeSpan.FromSeconds(1)); // the service ticks once a second
        Pump(() => rig.FocusService.Current.Phase == PomodoroPhase.Finished);
        rig.Settle();

        Assert.Equal(IslandActivity.Eyes, rig.Activity.Current);
        Assert.True(rig.Eyes.IsVisible);
        Assert.False(rig.FocusPill.IsVisible);
        Assert.True(cheek.HasAnimatedProperties); // the smile
        Assert.False(rig.FocusTime.TimerRunning);
    });

    [Fact]
    public void StoppingByHandBringsTheEyesBackWithoutACelebration() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.StartFocus();
        rig.WaitForPillWidth(160);
        var cheek = (TranslateTransform)rig.Eyes.FindName("LeftCheekOffset");

        rig.RunFocus(_ => rig.FocusService.StopAsync(), () => !rig.Focus.IsActive);

        Assert.True(rig.Eyes.IsVisible);
        Assert.False(rig.FocusPill.IsVisible);
        Assert.False(cheek.HasAnimatedProperties);
        Assert.False(rig.FocusTime.TimerRunning);
    });

    [Fact]
    public void TheCountdownPillIsNamedAndNeverTakesFocus() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.StartFocus();
        rig.WaitForPillWidth(160);

        // A label, not a control: nothing in it can take focus, so the island keeps its own keyboard handling.
        Assert.Empty(AutomationAudit.UsableControls(rig.FocusPill));
        Assert.False(rig.FocusPill.Focusable);
        Assert.Equal(Lunar.Desktop.Resources.Strings.Get("Pomodoro.PillName"), AutomationAudit.NameOf(rig.FocusPill));
        Assert.Equal(Lunar.Desktop.Resources.Strings.Format("Pomodoro.TimeLeftName", "25:00"), AutomationAudit.NameOf(rig.Countdown));
    });

    [Fact]
    public void HighContrastColoursTheDigitsWithItsOwnBrushes() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.Window.Resources.MergedDictionaries[0] = Load("Themes", "Colors.HighContrast.xaml");
        rig.StartFocus();
        rig.WaitForPillWidth(160);

        Assert.Equal(((SolidColorBrush)rig.Window.FindResource("Lunar.Brush.TextPrimary")).Color, ((SolidColorBrush)rig.Countdown.Foreground).Color);
        rig.RunFocus(rig.FocusService.PauseAsync, () => rig.Focus.IsPaused);
        Assert.Equal(((SolidColorBrush)rig.Window.FindResource("Lunar.Brush.TextSecondary")).Color, ((SolidColorBrush)rig.Countdown.Foreground).Color);
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

    // ---- FE-40 (ADR-0027): the Notch style ------------------------------------------------------------------------------

    [Fact]
    public void TheNotchOutlineIsFlatOnTopWithShouldersAndRoundedBelow()
    {
        var outline = IslandOutline.Notch(160, 44, shoulders: true)!;
        Assert.True(outline.FillContains(new Point(4, 0.5)));         // the shoulder flares out along the top edge, into the screen's edge
        Assert.True(outline.FillContains(new Point(156, 0.5)));
        Assert.False(outline.FillContains(new Point(0.5, 4)));       // and thins to nothing at the very corner
        Assert.False(outline.FillContains(new Point(1, 20)));         // below the shoulder the side is set in
        Assert.False(outline.FillContains(new Point(159, 20)));
        Assert.True(outline.FillContains(new Point(80, 43)));
        Assert.False(outline.FillContains(new Point(8.5, 43.5)));     // the bottom corners are round
        Assert.True(outline.FillContains(new Point(10, 20)));         // the content area starts right at the shoulder
        Assert.Null(IslandOutline.Notch(0, 44, true));
        Assert.Equal(new CornerRadius(0, 0, 13.2, 13.2), (CornerRadius)new NotchRadiusConverter().Convert(44d, typeof(CornerRadius), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(IslandOutline.MaxBottomRadius, IslandOutline.NotchBottomRadius(320));
    }

    [Fact]
    public void TheNotchIsWiderAndShorterAtTheTopEdgeAndThePillGoesBack() => RunOnSta(() =>
    {
        using var rig = new Rig();
        var floating = rig.Window.Top;
        Assert.True(floating > 100); // the rig's offset is 300 DIP

        rig.UseStyle(Lunar.Application.Settings.Models.IslandStyle.Notch);
        Assert.InRange(rig.Window.Top, -1, 1);
        rig.WaitForPillSize(IslandLayout.NotchIdleWidth, IslandLayout.NotchIdleHeight);
        Assert.NotNull(rig.PillBorder.Clip);
        Assert.Equal(new Thickness(0), rig.PillBorder.BorderThickness);
        Assert.Equal(0, rig.PillBorder.CornerRadius.TopLeft);
        Assert.True(rig.PillBorder.CornerRadius.BottomLeft > 0);
        Assert.True(rig.Eyes.ActualHeight <= IslandLayout.NotchIdleHeight); // the eyes sit inside the shorter strip

        rig.UseStyle(Lunar.Application.Settings.Models.IslandStyle.Pill);
        Assert.InRange(rig.Window.Top, floating - 0.5, floating + 0.5); // one physical pixel of rounding at most
        rig.WaitForPillSize(IslandLayout.IdleWidth, IslandLayout.IdleHeight);
        Assert.Null(rig.PillBorder.Clip);
        Assert.Equal(22, rig.PillBorder.CornerRadius.TopLeft);
    });

    [Fact]
    public void TheOpenNotchIsAWidePanelWithTabsInsteadOfArrowsAndDots() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.UseStyle(Lunar.Application.Settings.Models.IslandStyle.Notch);
        rig.Expand();
        Assert.True(rig.Window.ActualWidth >= IslandLayout.NotchExpandedMinWidth - 1);
        Assert.True(rig.Shell.Swiper.ShowsTabs);
        Assert.False(((FrameworkElement)rig.Shell.Swiper.FindName("PreviousButton")).IsVisible);
        Assert.False(((FrameworkElement)rig.Shell.Swiper.FindName("NextButton")).IsVisible);
        Assert.False(((FrameworkElement)rig.Shell.Swiper.FindName("Dots")).IsVisible);
        Assert.Equal(IslandOutline.MaxBottomRadius, rig.PillBorder.CornerRadius.BottomLeft);

        // A tab changes the page, like a dot did.
        var tabs = (Panel)rig.Shell.Swiper.FindName("Tabs");
        ((System.Windows.Controls.Primitives.ToggleButton)tabs.Children[2]).IsChecked = true;
        rig.Settle();
        Assert.Equal(2, rig.Shell.Swiper.SelectedIndex);

        rig.Collapse();
        rig.UseStyle(Lunar.Application.Settings.Models.IslandStyle.Pill);
        rig.Expand();
        Assert.False(rig.Shell.Swiper.ShowsTabs);
        Assert.True(((FrameworkElement)rig.Shell.Swiper.FindName("NextButton")).IsVisible);
    });

    [Fact]
    public void EveryActivityFitsTheNotchAndFastCyclesStayInside() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.UseStyle(Lunar.Application.Settings.Models.IslandStyle.Notch);

        rig.StartRecording();
        rig.WaitForPillSize(IslandLayout.NotchRecordingIdleWidth, IslandLayout.NotchIdleHeight);
        var pause = (FrameworkElement)rig.Pill.FindName("PauseButton");
        var at = pause.TranslatePoint(new Point(0, 0), rig.PillBorder);
        Assert.True(at.X >= IslandOutline.Shoulder, $"Pause starts at {at.X}");
        Assert.True(at.Y >= 0 && at.Y + pause.ActualHeight <= IslandLayout.NotchIdleHeight, $"Pause is {pause.ActualHeight} high at {at.Y}");
        var time = (FrameworkElement)rig.Pill.FindName("ElapsedText");
        var timeAt = time.TranslatePoint(new Point(time.ActualWidth, 0), rig.PillBorder);
        Assert.True(timeAt.X <= rig.PillBorder.ActualWidth - IslandOutline.Shoulder, $"The time ends at {timeAt.X}");
        rig.StopRecording();
        rig.WaitForPillSize(IslandLayout.NotchIdleWidth, IslandLayout.NotchIdleHeight);

        rig.StartFocus();
        rig.WaitForPillSize(IslandLayout.NotchIdleWidth, IslandLayout.NotchIdleHeight);
        var digits = rig.Countdown.TranslatePoint(new Point(0, 0), rig.PillBorder);
        Assert.InRange(digits.Y + (rig.Countdown.ActualHeight / 2), (IslandLayout.NotchIdleHeight / 2) - 2, (IslandLayout.NotchIdleHeight / 2) + 2); // centred
        rig.RunFocus(ct => rig.FocusService.StopAsync(), () => !rig.Focus.IsActive);

        rig.Expand();
        rig.Collapse();
        var left = rig.Window.Left; // after one full measure in this style
        for (var i = 0; i < 20; i++)
        {
            rig.Expand();
            Assert.Equal(left, rig.Window.Left);
            Assert.InRange(Math.Abs(rig.PillBorder.ActualWidth - rig.PillBorder.Clip!.Bounds.Width), 0, 1);
            rig.Collapse();
            Assert.InRange(Math.Abs(rig.PillBorder.ActualHeight - rig.PillBorder.Clip!.Bounds.Height), 0, 1);
        }

        Assert.Equal(left, rig.Window.Left); // never moves sideways (FE-33), in this style too
    });

    [Fact]
    public void RendersTheNotchStatesForReview()
    {
        var dir = System.Environment.GetEnvironmentVariable("LUNAR_RENDER_DIR");
        if (string.IsNullOrEmpty(dir))
            Assert.Skip("Renders PNGs for a person to look at; set LUNAR_RENDER_DIR to a folder to run it.");
        RunOnSta(() => Render(dir));
    }

    private static void Render(string dir)
    {
        using var rig = new Rig();
        void Shot(string name)
        {
            var until = DateTime.UtcNow.AddMilliseconds(500);
            while (DateTime.UtcNow < until)
            {
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
                Thread.Sleep(10);
            }

            rig.Settle();
            var w = (int)Math.Ceiling(rig.Window.ActualWidth);
            var h = (int)Math.Ceiling(rig.Window.ActualHeight);
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(w * 2, h * 2, 192, 192, System.Windows.Media.PixelFormats.Pbgra32);
            var bg = new System.Windows.Shapes.Rectangle { Width = w, Height = h, Fill = System.Windows.Media.Brushes.LightSteelBlue };
            bg.Measure(new Size(w, h));
            bg.Arrange(new Rect(0, 0, w, h));
            bmp.Render(bg);
            bmp.Render((System.Windows.Media.Visual)rig.Window.Content);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var f = File.Create(System.IO.Path.Combine(dir, name + ".png"));
            enc.Save(f);
        }

        rig.UseStyle(Lunar.Application.Settings.Models.IslandStyle.Notch);
        rig.WaitForPillSize(IslandLayout.NotchIdleWidth, IslandLayout.NotchIdleHeight);
        Shot("1-notch-eyes");
        rig.StartRecording();
        rig.WaitForPillSize(IslandLayout.NotchRecordingIdleWidth, IslandLayout.NotchIdleHeight);
        Shot("2-notch-recording");
        rig.StopRecording();
        rig.StartFocus();
        rig.WaitForPillSize(IslandLayout.NotchIdleWidth, IslandLayout.NotchIdleHeight);
        Shot("3-notch-focus");
        rig.RunFocus(ct => rig.FocusService.StopAsync(), () => !rig.Focus.IsActive);
        rig.Expand();
        Shot("4-notch-expanded");
        rig.Collapse();
        rig.UseStyle(Lunar.Application.Settings.Models.IslandStyle.Pill);
        rig.Expand();
        Shot("5-pill-expanded");
    }
}
