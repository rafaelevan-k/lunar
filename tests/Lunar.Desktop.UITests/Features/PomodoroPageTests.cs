using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;
using Lunar.Application.Abstractions.System;
using Lunar.Application.Focus;
using Lunar.Application.Settings;
using Lunar.Desktop.Features.Pomodoro;
using Lunar.Desktop.Resources;
using Lunar.Desktop.UITests.Accessibility;
using Lunar.Desktop.UITests.Themes;
using Lunar.Domain.Focus;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Desktop.UITests.Features;

// FE-36: the real page at the island's page size over the real PomodoroService, with stand-ins for Do Not Disturb, the marker and the clock. A real
// session with real notifications and Narrator are manual checks (FE-36 evidence).
public sealed class PomodoroPageTests
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
            throw new InvalidOperationException("WPF pomodoro page check failed.", failure);
    }

    private static void Pump(Func<bool> done, int seconds = 10)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!done() && DateTime.UtcNow < end)
        {
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
            Thread.Sleep(2);
        }

        Assert.True(done(), "The page did not reach the expected state in time.");
    }

    internal sealed class Rig : IDisposable
    {
        public Rig()
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
            Service = new PomodoroService(Focus, new Marker(), Settings, Clock, null, Time);
            Model = new PomodoroPageViewModel(Service, Launcher, post, Time);
            Page = new PomodoroPage { DataContext = Model };
            Window = new Window
            {
                Width = PageWidth,
                Height = PageHeight,
                Content = Page,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
                ShowInTaskbar = false,
                Left = -20000,
                Top = -20000,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            foreach (var dictionary in new[] { "Colors.Dark.xaml", "Typography.xaml", "Spacing.xaml", "Components.xaml" })
                Window.Resources.MergedDictionaries.Add(Load("Themes", dictionary));
            Window.Resources.MergedDictionaries.Add(Load("Controls", "ControlStyles.xaml"));
            Window.Show();
            Settle();
        }

        public FakeFocus Focus { get; } = new();

        public MovableClock Clock { get; } = new();

        public RecordsPageTests.ManualTime Time { get; } = new();

        public RecordsPageTests.FakeLauncher Launcher { get; } = new();

        public SettingsService Settings { get; }

        public PomodoroService Service { get; }

        public PomodoroPageViewModel Model { get; }

        public PomodoroPage Page { get; }

        public Window Window { get; }

        public T Named<T>(string name)
            where T : class =>
            (Page.FindName(name) as T) ?? throw new InvalidOperationException(name + " is missing.");

        public void Run(ICommand command)
        {
            command.Execute(null);
            Pump(() => command is not Lunar.Desktop.Shell.AsyncRelayCommand { IsRunning: true });
            Settle();
        }

        public void Settle()
        {
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(() => { }));
            Window.UpdateLayout();
        }

        public void Dispose()
        {
            Window.Close();
            Model.Dispose();
            Service.Dispose();
            Settings.Dispose();
        }
    }

    private static void AssertFits(Rig rig)
    {
        foreach (var control in AutomationAudit.UsableControls(rig.Page))
        {
            var corner = control.TranslatePoint(new Point(control.ActualWidth, control.ActualHeight), rig.Page);
            Assert.True(corner.X <= PageWidth + 0.5 && corner.Y <= PageHeight + 0.5, $"{AutomationAudit.NameOf(control)} ends at {corner}.");
        }

        var text = rig.Named<TextBlock>("FocusText");
        Assert.True(text.TranslatePoint(new Point(0, text.ActualHeight), rig.Page).Y <= PageHeight + 0.5, "The Do Not Disturb line runs off the page.");
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(4, 5)]
    [InlineData(5, 10)]
    [InlineData(23, 25)]
    [InlineData(175, 180)]
    [InlineData(180, 180)]
    public void PlusStepsByOneThenByFive(int from, int to) => Assert.Equal(to, PomodoroPageViewModel.StepUp(from));

    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 4)]
    [InlineData(10, 5)]
    [InlineData(23, 20)]
    [InlineData(180, 175)]
    public void MinusStepsByFiveThenByOne(int from, int to) => Assert.Equal(to, PomodoroPageViewModel.StepDown(from));

    [Theory]
    [InlineData(0.2, "00:01")]
    [InlineData(1500, "25:00")]
    [InlineData(3600, "1:00:00")]
    [InlineData(10800, "3:00:00")]
    public void TheTimeRoundsUpSoTheLastSecondStillShows(double seconds, string expected) =>
        Assert.Equal(expected, PomodoroPageViewModel.Format(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void ThePresetsChooseTheLengthAndTheChoiceShows() => RunOnSta(() =>
    {
        using var rig = new Rig();
        Assert.Equal("25:00", rig.Named<TextBlock>("TimeText").Text);
        Assert.Equal([15, 25, 45, 60], rig.Model.Presets.Select(p => p.Minutes));
        Assert.True(rig.Model.Presets.Single(p => p.Minutes == 25).IsSelected);

        rig.Model.Presets.Single(p => p.Minutes == 45).SelectCommand.Execute(null);
        rig.Settle();

        Assert.Equal("45:00", rig.Named<TextBlock>("TimeText").Text);
        Assert.True(rig.Model.Presets.Single(p => p.Minutes == 45).IsSelected);
        rig.Model.MoreCommand.Execute(null);
        Assert.Equal(50, rig.Model.Minutes);
        rig.Model.LessCommand.Execute(null);
        Assert.Equal(45, rig.Model.Minutes);
        rig.Model.LessCommand.Execute(null);
        Assert.Equal(40, rig.Model.Minutes);
    });

    [Fact]
    public void TheLengthCannotGoOutsideOneToOneHundredEightyMinutes() => RunOnSta(() =>
    {
        using var rig = new Rig();
        while (rig.Model.LessCommand.CanExecute(null))
            rig.Model.LessCommand.Execute(null);
        Assert.Equal(1, rig.Model.Minutes);
        while (rig.Model.MoreCommand.CanExecute(null))
            rig.Model.MoreCommand.Execute(null);
        Assert.Equal(180, rig.Model.Minutes);
        Assert.True(FocusTimerRules.IsValid(rig.Model.Minutes));
    });

    [Fact]
    public void StartCountsDownAndSaysDoNotDisturbIsOnOnlyOnceConfirmed() => RunOnSta(() =>
    {
        using var rig = new Rig();

        rig.Run(rig.Model.StartCommand);

        Assert.Equal(PomodoroPhase.Running, rig.Service.Current.Phase);
        Assert.Equal(Strings.Get("Pomodoro.On"), rig.Named<TextBlock>("FocusText").Text);
        Assert.Equal(Visibility.Collapsed, rig.Named<Button>("StartButton").Visibility);
        Assert.Equal(Visibility.Collapsed, rig.Named<ItemsControl>("Presets").Visibility); // the length is fixed during a session
        Assert.Equal(25, rig.Service.LastMinutes);

        rig.Clock.LocalNow += TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(30);
        rig.Time.FireTicks(PomodoroPageViewModel.TickInterval);
        rig.Settle();
        Assert.Equal("14:30", rig.Named<TextBlock>("TimeText").Text);
    });

    [Fact]
    public void PauseHoldsTheTimeAndStopEndsTheSession() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.Run(rig.Model.StartCommand);

        rig.Run(rig.Model.PauseResumeCommand);
        Assert.Equal(PomodoroPhase.Paused, rig.Service.Current.Phase);
        Assert.Equal(Strings.Get("Pomodoro.Resume"), rig.Named<Button>("PauseButton").Content);
        Assert.False(rig.Focus.IsOn); // off while paused (the owner's decision, 2026-11-11)
        Assert.Equal(Strings.Get("Pomodoro.PausedOff"), rig.Named<TextBlock>("FocusText").Text);
        Assert.False(rig.Named<Lunar.Desktop.Controls.PixelArtView>("Egg").IsAnimating);
        rig.Clock.LocalNow += TimeSpan.FromMinutes(5);
        rig.Settle();
        Assert.Equal("25:00", rig.Named<TextBlock>("TimeText").Text);

        rig.Run(rig.Model.StopCommand);

        Assert.Equal(PomodoroPhase.Idle, rig.Service.Current.Phase);
        Assert.Equal(Visibility.Visible, rig.Named<Button>("StartButton").Visibility);
        Assert.Equal(Strings.Get("Pomodoro.Hint"), rig.Named<TextBlock>("FocusText").Text);
        Assert.False(rig.Focus.IsOn);
    });

    [Fact]
    public void ARefusalIsSaidWithAWayToWindowsSettingsAndTheTimerStillRuns() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.Focus.TurnOnError = new OperationError(ErrorCode.Timeout, "not confirmed");

        rig.Run(rig.Model.StartCommand);

        Assert.Equal(PomodoroPhase.Running, rig.Service.Current.Phase);
        Assert.Equal(Strings.Get("Pomodoro.Unavailable"), rig.Named<TextBlock>("FocusText").Text);
        Assert.Equal(Visibility.Visible, rig.Named<Button>("OpenSettingsButton").Visibility);
        rig.Run(rig.Model.OpenSettingsCommand);
        Assert.Equal([SettingsPage.Focus], rig.Launcher.Opened);
        AssertFits(rig);
    });

    [Fact]
    public void AnUnsupportedWindowsIsSaidPlainly() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.Focus.ReadError = new OperationError(ErrorCode.Unsupported, "no service");

        rig.Run(rig.Model.StartCommand);

        Assert.Equal(Strings.Get("Pomodoro.Unsupported"), rig.Named<TextBlock>("FocusText").Text);
    });

    [Fact]
    public void AlreadyOnIsSaidAndLeftAlone() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.Focus.IsOn = true;

        rig.Run(rig.Model.StartCommand);

        Assert.Equal(Strings.Get("Pomodoro.AlreadyOn"), rig.Named<TextBlock>("FocusText").Text);
        Assert.Equal(0, rig.Focus.TurnOns);
    });

    [Fact]
    public void TheEndIsSaidAndNoWordsEverClaimEveryNotificationIsBlocked() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.Run(rig.Model.StartCommand);
        rig.Clock.LocalNow += TimeSpan.FromMinutes(25);

        rig.Time.FireTicks(TimeSpan.FromSeconds(1)); // the service ticks once a second
        Pump(() => rig.Service.Current.Phase == PomodoroPhase.Finished);
        rig.Settle();

        Assert.Equal(Strings.Get("Pomodoro.Finished"), rig.Named<TextBlock>("FocusText").Text);
        foreach (var tag in new[] { "en", "id" })
        {
            foreach (var (key, value) in Strings.All(tag).Where(p => p.Key.StartsWith("Pomodoro.", StringComparison.Ordinal)))
            {
                Assert.DoesNotContain("all notifications", value, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("semua notifikasi", value, StringComparison.OrdinalIgnoreCase);
            }
        }
    });

    [Fact]
    public void EveryStateFitsAndIsNamed() => RunOnSta(() =>
    {
        using var rig = new Rig();
        Assert.Empty(AutomationAudit.Problems(rig.Page));
        AssertFits(rig);

        rig.Model.Presets.Single(p => p.Minutes == 60).SelectCommand.Execute(null);
        for (var i = 0; i < 30; i++)
            rig.Model.MoreCommand.Execute(null);
        rig.Settle();
        Assert.Equal("3:00:00", rig.Named<TextBlock>("TimeText").Text);
        AssertFits(rig);

        rig.Run(rig.Model.StartCommand);
        Assert.Empty(AutomationAudit.Problems(rig.Page));
        AssertFits(rig);
    });

    [Fact]
    public void BothEggsAreRectangularPicturesOfKnownPixels()
    {
        foreach (var art in new[] { EggArt.Whole, EggArt.Sunny }.Concat(EggArt.Cracking))
        {
            Assert.All(art, row => Assert.Equal(art[0].Length, row.Length));
            Assert.All(art, row => Assert.Matches("^[.owshy]+$", row));
        }

        Assert.DoesNotContain(EggArt.Whole, row => row.Contains('y', StringComparison.Ordinal)); // no yolk in a whole egg
        Assert.Contains(EggArt.Sunny, row => row.Contains('y', StringComparison.Ordinal));
    }

    [Fact]
    public void TheEggHopsOnlyWhileTheTimeRunsAndIsSunnyAtTheEnd() => RunOnSta(() =>
    {
        using var rig = new Rig();
        var egg = rig.Named<Lunar.Desktop.Controls.PixelArtView>("Egg");
        Assert.Same(EggArt.Whole, egg.Rows);
        Assert.False(egg.IsAnimating);

        rig.Run(rig.Model.StartCommand);
        Assert.True(egg.IsAnimating);
        Assert.Same(EggArt.Whole, egg.Rows);

        rig.Clock.LocalNow += TimeSpan.FromMinutes(25);
        rig.Time.FireTicks(TimeSpan.FromSeconds(1));
        Pump(() => rig.Service.Current.Phase == PomodoroPhase.Finished);
        rig.Settle();

        Assert.False(egg.IsAnimating);
        Assert.Same(EggArt.Sunny, egg.Rows);
        Assert.True(egg.IsPlayingIntro); // it cracks first
        Pump(() => !egg.IsPlayingIntro, 3);
        Assert.True(egg.ActualWidth > 40 && egg.ActualHeight > 30);

        rig.Run(rig.Model.StartCommand); // a new session starts from a whole egg again, without cracking
        Assert.Same(EggArt.Whole, egg.Rows);
        Assert.False(egg.IsPlayingIntro);
    });

    [Fact]
    public void ReducedMotionKeepsTheEggStill() => RunOnSta(() =>
    {
        using var rig = new Rig();
        Lunar.Desktop.Behaviors.Motion.SetReduceMotion(rig.Page, true);

        rig.Run(rig.Model.StartCommand);
        var egg = rig.Named<Lunar.Desktop.Controls.PixelArtView>("Egg");
        Assert.False(egg.IsAnimating);

        rig.Clock.LocalNow += TimeSpan.FromMinutes(25);
        rig.Time.FireTicks(TimeSpan.FromSeconds(1));
        Pump(() => rig.Service.Current.Phase == PomodoroPhase.Finished);
        rig.Settle();
        Assert.False(egg.IsPlayingIntro); // no cracking either: straight to sunny-side up
        Assert.Same(EggArt.Sunny, egg.Rows);
    });

    [Fact]
    public void ButtonOutlinesAreThickerOnThisPage() => RunOnSta(() =>
    {
        using var rig = new Rig();

        Assert.Equal(new Thickness(2.4), (Thickness)rig.Page.FindResource("Lunar.Thickness.Outline"));
        Assert.Equal(new Thickness(1.6), (Thickness)rig.Window.FindResource("Lunar.Thickness.Outline")); // the rest of Lunar keeps the usual outline
    });

    internal sealed class MovableClock : IClock
    {
        public DateTimeOffset LocalNow { get; set; } = new(2026, 11, 11, 9, 0, 0, TimeSpan.FromHours(7));
    }

    internal sealed class Marker : IFocusSessionMarker
    {
        public Task<Result<string?>> ReadAsync(CancellationToken ct) => Task.FromResult(Result.Success<string?>(null));

        public Task<Result> SaveAsync(string restoreToken, CancellationToken ct) => Task.FromResult(Result.Success());

        public Task<Result> ClearAsync(CancellationToken ct) => Task.FromResult(Result.Success());
    }

    internal sealed class FakeFocus : IFocusModeControl
    {
        public bool IsOn { get; set; }

        public int TurnOns { get; private set; }

        public OperationError? ReadError { get; set; }

        public OperationError? TurnOnError { get; set; }

        public Task<Result<FocusModeState>> ReadAsync(CancellationToken ct) => Task.FromResult(ReadError is null
            ? Result.Success(new FocusModeState(IsOn, IsOn ? "on" : "off"))
            : Result.Failure<FocusModeState>(ReadError));

        public Task<Result> TurnOnAsync(CancellationToken ct)
        {
            if (TurnOnError is not null)
                return Task.FromResult(Result.Failure(TurnOnError));
            TurnOns++;
            IsOn = true;
            return Task.FromResult(Result.Success());
        }

        public Task<Result> RestoreAsync(string restoreToken, CancellationToken ct)
        {
            IsOn = restoreToken == "on";
            return Task.FromResult(Result.Success());
        }
    }
}
