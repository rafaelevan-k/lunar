using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Lunar.Application.Abstractions.Shell;
using Lunar.Application.Settings.Models;
using Lunar.Application.Shell.Models;
using Lunar.Application.Usage;
using Lunar.Application.Usage.Models;
using Lunar.Desktop.Features.Usage;
using Lunar.Desktop.Shell;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Desktop.UITests.Features;

// FE-28: the "limit reset" pop-up. The words, the corner it goes to and when it comes and goes are tested without a window; the real window is
// tested on an STA thread. How it looks on a real desktop, whether Narrator reads it, a second monitor and 200% scaling are manual checks (NOT_RUN).
public sealed class ResetToastTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private static UsageLimitReset Reset(UsageWindowKind kind, int? previous = 92, int lateSeconds = 0) =>
        new(kind, At, At.AddSeconds(lateSeconds), previous);

    // ---- words ------------------------------------------------------------------------------------------------------

    [Fact]
    public void OneLimitNamesItAndHowFullItWas()
    {
        var five = ResetToastContent.For([Reset(UsageWindowKind.FiveHour, 92)]);
        var week = ResetToastContent.For([Reset(UsageWindowKind.Weekly, 100)]);

        Assert.Equal("Claude limit reset", five.Title);
        Assert.Equal("Your 5-hour limit has reset. It was at 92%.", five.Message);
        Assert.Equal("Your weekly limit has reset. It was at 100%.", week.Message);
        Assert.Equal("Claude: Your 5-hour limit has reset. It was at 92%.", five.AutomationName);
    }

    [Fact]
    public void AnUnknownEarlierShareIsLeftOutNotInvented() =>
        Assert.Equal("Your 5-hour limit has reset.", ResetToastContent.For([Reset(UsageWindowKind.FiveHour, previous: null)]).Message);

    [Fact]
    public void BothLimitsAreOneMessageInAnyOrderAndALimitNamedTwiceCountsOnce()
    {
        var a = ResetToastContent.For([Reset(UsageWindowKind.FiveHour), Reset(UsageWindowKind.Weekly)]);
        var b = ResetToastContent.For([Reset(UsageWindowKind.Weekly), Reset(UsageWindowKind.FiveHour), Reset(UsageWindowKind.FiveHour)]);
        var twice = ResetToastContent.For([Reset(UsageWindowKind.FiveHour, 50), Reset(UsageWindowKind.FiveHour, 60)]);

        Assert.Equal("Your 5-hour and weekly limits have reset.", a.Message);
        Assert.Equal(a.Message, b.Message);
        Assert.Equal("Your 5-hour limit has reset. It was at 60%.", twice.Message); // the latest wins
    }

    [Fact]
    public void NothingToSayIsRefused() =>
        Assert.Throws<ArgumentException>(() => ResetToastContent.For([]));

    // ---- placement --------------------------------------------------------------------------------------------------

    private static DisplayInfo Display(int x, int y, int width, int height, int workX, int workY, int workWidth, int workHeight, int dpi = 96) =>
        new("d", true, new PixelRect(x, y, width, height), new PixelRect(workX, workY, workWidth, workHeight), dpi, dpi);

    [Fact]
    public void ItGoesToTheBottomRightOfTheWorkAreaAboveATaskbarAtTheBottom()
    {
        var display = Display(0, 0, 1920, 1080, 0, 0, 1920, 1040); // 40 px taskbar at the bottom

        var rect = ResetToastPlacement.BottomRight(display, 320, 80, 12);

        Assert.Equal(new PixelRect(1920 - 320 - 12, 1040 - 80 - 12, 320, 80), rect);
    }

    [Fact]
    public void ItFollowsTheWorkAreaWhenTheTaskbarIsAtTheRightOrTheTop()
    {
        var right = ResetToastPlacement.BottomRight(Display(0, 0, 1920, 1080, 0, 0, 1860, 1080), 320, 80, 12);
        var top = ResetToastPlacement.BottomRight(Display(0, 0, 1920, 1080, 0, 40, 1920, 1040), 320, 80, 12);

        Assert.Equal(1860 - 320 - 12, right.X);
        Assert.Equal(1080 - 80 - 12, right.Y);
        Assert.Equal(1920 - 320 - 12, top.X);
        Assert.Equal(1080 - 80 - 12, top.Y); // the work area's bottom is still the screen's bottom
    }

    [Fact]
    public void SizeAndMarginScaleWithTheDisplay()
    {
        var display = Display(0, 0, 3840, 2160, 0, 0, 3840, 2080, dpi: 192); // 200%

        var rect = ResetToastPlacement.BottomRight(display, 320, 80, 12);

        Assert.Equal(640, rect.Width);
        Assert.Equal(160, rect.Height);
        Assert.Equal(3840 - 640 - 24, rect.X);
        Assert.Equal(2080 - 160 - 24, rect.Y);
    }

    [Fact]
    public void ASecondMonitorWithANegativeOriginIsHandled()
    {
        var display = Display(-1920, 0, 1920, 1080, -1920, 0, 1920, 1040);

        var rect = ResetToastPlacement.BottomRight(display, 320, 80, 12);

        Assert.Equal(-12 - 320, rect.X);
        Assert.Equal(1040 - 80 - 12, rect.Y);
    }

    [Fact]
    public void ItNeverLeavesTheWorkAreaEvenWhenTheAreaIsTinyOrTheMarginHuge()
    {
        var tiny = Display(0, 0, 200, 100, 0, 0, 200, 100);

        var rect = ResetToastPlacement.BottomRight(tiny, 320, 80, 500);

        Assert.True(tiny.WorkArea.Contains(rect), "The pop-up stays inside the work area.");
    }

    [Theory]
    [InlineData(0, 80, 12)]
    [InlineData(320, 0, 12)]
    [InlineData(double.NaN, 80, 12)]
    [InlineData(320, 80, -1)]
    public void ImpossibleSizesAreRefused(double width, double height, double margin) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ResetToastPlacement.BottomRight(Display(0, 0, 100, 100, 0, 0, 100, 100), width, height, margin));

    // ---- timing and decisions ---------------------------------------------------------------------------------------

    private sealed class FakePresenter : IResetToastPresenter
    {
        public event Action? CloseRequested;

        public event Action? PointerEntered;

        public event Action? PointerLeft;

        public List<ResetToastContent> Shown { get; } = [];

        public int Hides { get; private set; }

        public bool Disposed { get; private set; }

        public void Show(ResetToastContent content) => Shown.Add(content);

        public void Hide() => Hides++;

        public void Dispose() => Disposed = true;

        public void Click() => CloseRequested?.Invoke();

        public void Enter() => PointerEntered?.Invoke();

        public void Leave() => PointerLeft?.Invoke();

        public int Subscribers => (CloseRequested?.GetInvocationList().Length ?? 0) + (PointerEntered?.GetInvocationList().Length ?? 0) + (PointerLeft?.GetInvocationList().Length ?? 0);
    }

    private sealed class SilentSource : Lunar.Application.Abstractions.Usage.IUsageSource
    {
        public Task<Result<UsageSnapshot>> FetchAsync(CancellationToken ct) => Task.FromResult(Result.Failure<UsageSnapshot>(new OperationError(ErrorCode.DeviceUnavailable, "x")));
    }

    private readonly FakePresenter _presenter = new();
    private readonly EyeAnimationControllerTests.FakeTimer _collect = new();
    private readonly EyeAnimationControllerTests.FakeTimer _show = new();
    private readonly UsageMonitor _monitor = new(new SilentSource());
    private readonly UsageResetNotifier _notifier;

    public ResetToastTests()
    {
        _notifier = new UsageResetNotifier(_monitor, _presenter, _collect, _show, work => work());
    }

    [Fact]
    public void AResetIsShownAfterAShortWaitAndGoesByItselfAfterTenSeconds()
    {
        _notifier.OnReset(Reset(UsageWindowKind.FiveHour));
        Assert.Empty(_presenter.Shown); // collecting
        Assert.Equal(UsageResetNotifier.CollectDelay, _collect.Pending);

        _collect.Fire();

        Assert.Equal("Your 5-hour limit has reset. It was at 92%.", Assert.Single(_presenter.Shown).Message);
        Assert.True(_notifier.IsVisible);
        Assert.Equal(TimeSpan.FromSeconds(10), _show.Pending);

        _show.Fire();

        Assert.False(_notifier.IsVisible);
        Assert.Equal(1, _presenter.Hides);
    }

    [Fact]
    public void ResetsInOneBurstBecomeOnePopUp()
    {
        _notifier.OnReset(Reset(UsageWindowKind.FiveHour));
        _notifier.OnReset(Reset(UsageWindowKind.Weekly));
        _collect.Fire();

        Assert.Equal("Your 5-hour and weekly limits have reset.", Assert.Single(_presenter.Shown).Message);
    }

    [Fact]
    public void ALaterBurstReplacesThePopUpStillOnScreenAndRestartsItsTime()
    {
        _notifier.OnReset(Reset(UsageWindowKind.FiveHour));
        _collect.Fire();
        _notifier.OnReset(Reset(UsageWindowKind.Weekly, 100));
        _collect.Fire();

        Assert.Equal(2, _presenter.Shown.Count);
        Assert.Equal("Your weekly limit has reset. It was at 100%.", _presenter.Shown[1].Message);
        Assert.Equal(UsageResetNotifier.ShowTime, _show.Pending);
        Assert.True(_notifier.IsVisible);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(600, true)] // exactly ten minutes late is still news
    [InlineData(601, false)] // later than that is noise
    [InlineData(7200, false)]
    public void AResetNoticedTooLateIsDropped(int lateSeconds, bool shown)
    {
        _notifier.OnReset(Reset(UsageWindowKind.FiveHour, lateSeconds: lateSeconds));

        Assert.Equal(shown, _collect.Pending is not null);
    }

    [Fact]
    public void WhileThePointerIsOverItItStaysAndLeavesAfterFourSecondsOfThePointerGoing()
    {
        _notifier.OnReset(Reset(UsageWindowKind.FiveHour));
        _collect.Fire();

        _presenter.Enter();
        Assert.Null(_show.Pending); // no countdown while hovered
        Assert.True(_notifier.IsVisible);

        _presenter.Leave();
        Assert.Equal(UsageResetNotifier.AfterPointerTime, _show.Pending);
        _show.Fire();
        Assert.False(_notifier.IsVisible);
    }

    [Fact]
    public void AClickClosesItAndAPointerEventWithNothingOnScreenDoesNothing()
    {
        _presenter.Enter();
        _presenter.Leave();
        Assert.Null(_show.Pending);

        _notifier.OnReset(Reset(UsageWindowKind.FiveHour));
        _collect.Fire();
        _presenter.Click();

        Assert.False(_notifier.IsVisible);
        Assert.Null(_show.Pending);
        Assert.Equal(1, _presenter.Hides);
    }

    [Fact]
    public void TheKeyboardClosesItAndSaysWhetherThereWasAnythingToClose()
    {
        Assert.False(_notifier.Dismiss()); // nothing on screen: the key press is not used

        _notifier.OnReset(Reset(UsageWindowKind.FiveHour));
        _collect.Fire();

        Assert.True(_notifier.Dismiss());
        Assert.False(_notifier.IsVisible);
        Assert.False(_notifier.Dismiss());
    }

    [Fact]
    public void DismissAlsoForgetsAResetStillBeingCollected()
    {
        _notifier.OnReset(Reset(UsageWindowKind.FiveHour));

        _notifier.Dismiss();

        Assert.Null(_collect.Pending);
        Assert.Empty(_presenter.Shown);
    }

    [Fact]
    public void TurningThePageOffHidesItAndForgetsWhatWasPending()
    {
        _notifier.OnReset(Reset(UsageWindowKind.FiveHour));
        _collect.Fire();
        _notifier.OnReset(Reset(UsageWindowKind.Weekly));

        _notifier.OnState(UsageState.Off);

        Assert.False(_notifier.IsVisible);
        Assert.Null(_collect.Pending);
        Assert.Null(_show.Pending);
        Assert.Equal(1, _presenter.Hides);
    }

    [Fact]
    public void OtherStatesLeaveItAlone()
    {
        _notifier.OnReset(Reset(UsageWindowKind.FiveHour));
        _collect.Fire();

        _notifier.OnState(new UsageState(UsageStatus.Unavailable));
        _notifier.OnState(new UsageState(UsageStatus.Loading));

        Assert.True(_notifier.IsVisible);
    }

    [Fact]
    public void DisposeClosesItStopsTheTimersAndLetsGo()
    {
        _notifier.OnReset(Reset(UsageWindowKind.FiveHour));
        _collect.Fire();

        _notifier.Dispose();
        _notifier.Dispose();
        _notifier.OnReset(Reset(UsageWindowKind.Weekly));

        Assert.True(_presenter.Disposed);
        Assert.Equal(0, _presenter.Subscribers);
        Assert.Null(_collect.Pending);
        Assert.Null(_show.Pending);
        Assert.Single(_presenter.Shown);
    }

    [Fact]
    public async Task ARealResetFromTheMonitorReachesThePopUp()
    {
        var source = new UsagePageViewModelTests.Source();
        using var monitor = new UsageMonitor(source, new UsageMonitorOptions { Delay = (_, ct) => Task.Delay(Timeout.Infinite, ct), RefreshGap = TimeSpan.Zero });
        var presenter = new FakePresenter();
        var collect = new EyeAnimationControllerTests.FakeTimer();
        using var notifier = new UsageResetNotifier(monitor, presenter, collect, new EyeAnimationControllerTests.FakeTimer(), work => work());
        var now = DateTimeOffset.UtcNow;
        UsageSnapshot Reading(double used, DateTimeOffset resets) => new(new UsageWindow(UsageWindowKind.FiveHour, used, resets), null, DateTimeOffset.UtcNow);
        source.Next = () => Result.Success(Reading(0.9, now.AddHours(3)));
        monitor.Start();
        await WaitFor(() => monitor.Current.Status == UsageStatus.Ready);

        source.Next = () => Result.Success(Reading(0.02, now.AddHours(8))); // a new period: the reset time moved far later
        await monitor.RefreshAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => collect.Pending is not null);
        collect.Fire();

        Assert.Equal("Your 5-hour limit has reset. It was at 90%.", Assert.Single(presenter.Shown).Message);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var end = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < end)
            await Task.Delay(5, TestContext.Current.CancellationToken);
        Assert.True(condition(), "The expected state did not arrive in time.");
    }

    // ---- the real window --------------------------------------------------------------------------------------------

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
            throw new InvalidOperationException("WPF reset pop-up check failed.", failure);
    }

    private static void Pump()
    {
        for (var i = 0; i < 5; i++)
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
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

    // The window's own DPI scale, so the fake display matches it (the island makes the same assumption).
    private static int WindowDpi()
    {
        var probe = new Window { Width = 10, Height = 10, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
        probe.Show();
        var dpi = (int)Math.Round(VisualTreeHelper.GetDpi(probe).DpiScaleX * 96);
        probe.Close();
        return dpi;
    }

    [Fact]
    public void TheRealWindowIsTopmostNeverActivatesAndShowsTheWordsWithAnAccessibleName() => RunOnSta(() =>
    {
        var window = new ResetToastWindow { DataContext = ResetToastContent.For([Reset(UsageWindowKind.FiveHour, 92)]) };
        window.Show();
        Pump();
        try
        {
            Assert.True(window.Topmost);
            Assert.False(window.ShowInTaskbar);
            Assert.False(window.ShowActivated);
            Assert.True(NonActivatingWindowStyle.IsApplied(new WindowInteropHelper(window).Handle));
            Assert.Equal("Claude: Your 5-hour limit has reset. It was at 92%.", AutomationProperties.GetName(window));
            var texts = Descendants<TextBlock>(window).Select(t => t.Text).ToList();
            Assert.Contains("Claude limit reset", texts);
            Assert.Contains("Your 5-hour limit has reset. It was at 92%.", texts);
            Assert.Equal("Close", AutomationProperties.GetName((Button)window.FindName("CloseButton")));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void TheWindowUsesTheClaudeCardColorsAndTheOrangeAccent() => RunOnSta(() =>
    {
        var window = new ResetToastWindow { DataContext = ResetToastContent.For([Reset(UsageWindowKind.FiveHour)]) };
        window.Show();
        Pump();
        try
        {
            var card = (Border)window.FindName("Card");
            Assert.Equal(Color.FromRgb(0xFA, 0xF9, 0xF5), ((SolidColorBrush)card.Background).Color);
            Assert.Equal(Color.FromRgb(0xD9, 0x77, 0x57), ((SolidColorBrush)card.BorderBrush).Color);
            Assert.Equal(Color.FromRgb(0x14, 0x14, 0x13), ((SolidColorBrush)((TextBlock)window.FindName("MessageText")).Foreground).Color);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void AClickOnTheCardOrTheCloseButtonAsksToClose() => RunOnSta(() =>
    {
        var window = new ResetToastWindow { DataContext = ResetToastContent.For([Reset(UsageWindowKind.FiveHour)]) };
        var requests = 0;
        window.CloseRequested += () => requests++;
        window.Show();
        Pump();
        try
        {
            ((Button)window.FindName("CloseButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(1, requests);

            window.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonUpEvent,
                Source = window
            });
            Assert.Equal(2, requests);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void ThePresenterPutsItAtTheBottomRightOfTheWorkAreaAndClosesIt() => RunOnSta(() =>
    {
        var dpi = WindowDpi();
        var display = new DisplayInfo("m", true, new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), dpi, dpi);
        using var presenter = new ResetToastPresenter(new FakeEnvironment(display), new IslandSettings(), reduceMotion: true);

        presenter.Show(ResetToastContent.For([Reset(UsageWindowKind.FiveHour)]));
        Pump();
        var window = presenter.Window!;
        var scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        var right = (window.Left + window.ActualWidth) * scale;
        var bottom = (window.Top + window.ActualHeight) * scale;

        Assert.True(right <= 1920 + 1, $"Right edge {right} is inside the work area.");
        Assert.True(bottom <= 1040 + 1, $"Bottom edge {bottom} is above the taskbar.");
        Assert.True(right >= 1920 - (14 * scale), $"Right edge {right} is close to the corner.");
        Assert.True(bottom >= 1040 - (14 * scale), $"Bottom edge {bottom} is close to the corner.");
        Assert.Equal(1.0, window.Opacity); // reduced motion: no fade

        presenter.Hide();
        Pump();
        Assert.Null(presenter.Window);
        Assert.False(window.IsVisible);
    });

    [Fact]
    public void ChangingTheMotionSettingAppliesToTheNextPopUp() => RunOnSta(() =>
    {
        var dpi = WindowDpi();
        var display = new DisplayInfo("m", true, new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), dpi, dpi);
        using var presenter = new ResetToastPresenter(new FakeEnvironment(display), new IslandSettings(), reduceMotion: false);

        presenter.Update(new IslandSettings { TopOffsetDip = 20 }, reduceMotion: true);
        presenter.Show(ResetToastContent.For([Reset(UsageWindowKind.FiveHour)]));
        Pump();

        Assert.Equal(1.0, presenter.Window!.Opacity); // reduced motion now: no fade-in
    });

    [Fact]
    public void AReplacementReusesTheWindowAndUpdatesTheWords() => RunOnSta(() =>
    {
        var dpi = WindowDpi();
        var display = new DisplayInfo("m", true, new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), dpi, dpi);
        using var presenter = new ResetToastPresenter(new FakeEnvironment(display), new IslandSettings(), reduceMotion: true);

        presenter.Show(ResetToastContent.For([Reset(UsageWindowKind.FiveHour)]));
        presenter.Show(ResetToastContent.For([Reset(UsageWindowKind.Weekly, 100)]));
        Pump();

        var window = presenter.Window!;
        Assert.Contains("Your weekly limit has reset. It was at 100%.", Descendants<TextBlock>(window).Select(t => t.Text));
    });

}
