using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Shell;
using Lunar.Application.Settings.Models;
using Lunar.Application.Shell;
using Lunar.Application.Shell.Models;
using Lunar.Desktop.Shell;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Desktop.UITests.Shell;

// Real WPF windows on an STA thread, driven by a fake display environment. Windows that are shown are closed
// before the test ends (ShowActivated=false: no focus). What these tests cannot show is that Windows really
// reports a hotplug or DPI change; that is recorded as manual evidence.
public sealed class IslandPositionControllerTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

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
            throw new InvalidOperationException("WPF window check failed.", failure);
    }

    private static Rect PixelsOf(IslandWindow window)
    {
        Assert.True(GetWindowRect(new WindowInteropHelper(window).Handle, out var rect));
        return rect;
    }

    // Lets queued dispatcher work (the marshaled DisplaysChanged) run.
    private static void Pump(IslandWindow window) =>
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

    // A display whose DPI equals the window's own DPI, so the expected window rectangle is exact.
    private static DisplayInfo DisplayLikeHost(IslandWindow window, string id, bool primary, PixelRect bounds, PixelRect workArea)
    {
        var dpi = (int)Math.Round(VisualTreeHelper.GetDpi(window).PixelsPerInchX);
        return new DisplayInfo(id, primary, bounds, workArea, dpi, dpi);
    }

    private static (IslandWindow Window, FakeEnvironment Environment, FakeLog Log) Create()
    {
        var window = new IslandWindow();
        _ = new WindowInteropHelper(window).EnsureHandle();
        return (window, new FakeEnvironment(), new FakeLog());
    }

    [Fact]
    public void StartPlacesTheWindowTopCenterOfThePrimaryWorkArea() => RunOnSta(() =>
    {
        var (window, environment, log) = Create();
        var display = DisplayLikeHost(window, "\\\\.\\DISPLAY1", true, new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040));
        environment.Displays = [display];
        using var controller = new IslandPositionController(window, new IslandViewModel(), environment, log, new IslandSettings());

        var started = controller.Start();
        window.Show();
        try
        {
            var expected = IslandPlacement.PlaceTopCenter(display, 160, 44, 8);
            var actual = PixelsOf(window);

            Assert.True(started.IsSuccess);
            Assert.Equal(1, environment.StartCalls);
            Assert.Equal(expected.X, actual.Left);
            Assert.Equal(expected.Y, actual.Top);
            Assert.Equal(expected.Width, actual.Right - actual.Left);
            Assert.Equal(expected.Height, actual.Bottom - actual.Top);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void UsesThePreferredDisplayAndItsOffsetEvenWithNegativeCoordinates() => RunOnSta(() =>
    {
        var (window, environment, log) = Create();
        var primary = DisplayLikeHost(window, "\\\\.\\DISPLAY1", true, new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040));
        var left = DisplayLikeHost(window, "\\\\.\\DISPLAY2", false, new PixelRect(-1600, -200, 1600, 900), new PixelRect(-1600, -200, 1600, 860));
        environment.Displays = [primary, left];
        using var controller = new IslandPositionController(window, new IslandViewModel(), environment, log,
            new IslandSettings { PreferredMonitorId = "\\\\.\\DISPLAY2", TopOffsetDip = 20 });

        controller.Start();
        window.Show();
        try
        {
            var expected = IslandPlacement.PlaceTopCenter(left, 160, 44, 20);
            var actual = PixelsOf(window);

            Assert.True(expected.X < 0);
            Assert.Equal(expected.X, actual.Left);
            Assert.Equal(expected.Y, actual.Top);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void ChangingTheSavedDisplayOrOffsetMovesTheIslandAtOnce() => RunOnSta(() =>
    {
        var (window, environment, log) = Create();
        var primary = DisplayLikeHost(window, "\\\\.\\DISPLAY1", true, new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040));
        var right = DisplayLikeHost(window, "\\\\.\\DISPLAY2", false, new PixelRect(1920, 0, 1600, 900), new PixelRect(1920, 0, 1600, 860));
        environment.Displays = [primary, right];
        using var controller = new IslandPositionController(window, new IslandViewModel(), environment, log, new IslandSettings());
        controller.Start();
        window.Show();
        try
        {
            controller.UpdatePreference(new IslandSettings { TopOffsetDip = 40 });
            var moved = PixelsOf(window);
            controller.UpdatePreference(new IslandSettings { PreferredMonitorId = "\\\\.\\DISPLAY2", TopOffsetDip = 40 });
            var elsewhere = PixelsOf(window);

            var onPrimary = IslandPlacement.PlaceTopCenter(primary, 160, 44, 40);
            var onRight = IslandPlacement.PlaceTopCenter(right, 160, 44, 40);
            Assert.Equal(onPrimary.Y, moved.Top);
            Assert.Equal(onPrimary.X, moved.Left);
            Assert.Equal(onRight.X, elsewhere.Left);
            Assert.Equal(onRight.Y, elsewhere.Top);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void AnUnchangedPreferenceDoesNotMoveAnythingAndALateOneBeforeStartOnlyRemembers() => RunOnSta(() =>
    {
        var (window, environment, log) = Create();
        var display = DisplayLikeHost(window, "\\\\.\\DISPLAY1", true, new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040));
        environment.Displays = [display];
        using var controller = new IslandPositionController(window, new IslandViewModel(), environment, log, new IslandSettings());

        controller.UpdatePreference(new IslandSettings { TopOffsetDip = 30 }); // not started: nothing is placed yet
        controller.Start();
        window.Show();
        try
        {
            var placed = PixelsOf(window);
            controller.UpdatePreference(new IslandSettings { TopOffsetDip = 30 });

            Assert.Equal(IslandPlacement.PlaceTopCenter(display, 160, 44, 30).Y, placed.Top);
            Assert.Equal(placed.Top, PixelsOf(window).Top);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void RemovingThePreferredMonitorMovesTheWindowToThePrimaryDisplay() => RunOnSta(() =>
    {
        var (window, environment, log) = Create();
        var primary = DisplayLikeHost(window, "\\\\.\\DISPLAY1", true, new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040));
        var second = DisplayLikeHost(window, "\\\\.\\DISPLAY2", false, new PixelRect(1920, 0, 2560, 1440), new PixelRect(1920, 0, 2560, 1400));
        environment.Displays = [primary, second];
        using var controller = new IslandPositionController(window, new IslandViewModel(), environment, log,
            new IslandSettings { PreferredMonitorId = "\\\\.\\DISPLAY2" });
        controller.Start();
        window.Show();
        try
        {
            Assert.Equal(IslandPlacement.PlaceTopCenter(second, 160, 44, 8).X, PixelsOf(window).Left);

            environment.Displays = [primary]; // unplugged
            Task.Run(environment.Raise).Wait(TestContext.Current.CancellationToken); // pool thread, as in production
            Pump(window);

            var expected = IslandPlacement.PlaceTopCenter(primary, 160, 44, 8);
            Assert.Equal(expected.X, PixelsOf(window).Left);
            Assert.Equal(expected.Y, PixelsOf(window).Top);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void TaskbarMovingToTheTopPushesTheWindowDown() => RunOnSta(() =>
    {
        var (window, environment, log) = Create();
        var bounds = new PixelRect(0, 0, 1920, 1080);
        environment.Displays = [DisplayLikeHost(window, "\\\\.\\DISPLAY1", true, bounds, new PixelRect(0, 0, 1920, 1040))];
        using var controller = new IslandPositionController(window, new IslandViewModel(), environment, log, new IslandSettings());
        controller.Start();
        window.Show();
        try
        {
            var before = PixelsOf(window).Top;

            var topTaskbar = DisplayLikeHost(window, "\\\\.\\DISPLAY1", true, bounds, new PixelRect(0, 48, 1920, 1032));
            environment.Displays = [topTaskbar];
            environment.Raise();
            Pump(window);

            Assert.Equal(before + 48, PixelsOf(window).Top);
            Assert.Equal(IslandPlacement.PlaceTopCenter(topTaskbar, 160, 44, 8).Y, PixelsOf(window).Top);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void UnreadableDisplaysFallBackToASafeVisiblePosition() => RunOnSta(() =>
    {
        var (window, environment, log) = Create();
        environment.Failure = new OperationError(ErrorCode.DeviceUnavailable, "No display is available.");
        using var controller = new IslandPositionController(window, new IslandViewModel(), environment, log, new IslandSettings());

        controller.Start();
        window.Show();
        try
        {
            var expected = IslandPlacement.PlaceTopCenter(DisplayInfo.Fallback, 160, 44, 8);
            var actual = PixelsOf(window);

            Assert.InRange(actual.Left, 0, DisplayInfo.Fallback.WorkArea.Right);
            Assert.InRange(actual.Top, 0, DisplayInfo.Fallback.WorkArea.Bottom);
            Assert.Equal(expected.Y, actual.Top);
            Assert.Contains(log.Events, e => e.EventId == "island.position.fallback" && e.Error == ErrorCode.DeviceUnavailable);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void EmptyDisplayListFallsBackToo() => RunOnSta(() =>
    {
        var (window, environment, log) = Create();
        environment.Displays = [];
        using var controller = new IslandPositionController(window, new IslandViewModel(), environment, log, new IslandSettings());

        controller.Start();

        Assert.Contains(log.Events, e => e.EventId == "island.position.fallback");
        window.Close();
    });

    [Fact]
    public void ListenerFailureIsReportedButTheWindowIsStillPlaced() => RunOnSta(() =>
    {
        var (window, environment, log) = Create();
        var display = DisplayLikeHost(window, "\\\\.\\DISPLAY1", true, new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040));
        environment.Displays = [display];
        environment.StartResult = Result.Failure(new OperationError(ErrorCode.IoFailure, "Listener failed."));
        using var controller = new IslandPositionController(window, new IslandViewModel(), environment, log, new IslandSettings());

        var started = controller.Start();
        window.Show();
        try
        {
            Assert.True(started.IsFailure);
            Assert.Equal(IslandPlacement.PlaceTopCenter(display, 160, 44, 8).X, PixelsOf(window).Left);
            Assert.Contains(log.Events, e => e.EventId == "island.position.listener.failed" && e.Error == ErrorCode.IoFailure);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void StartingTwiceIsRejected() => RunOnSta(() =>
    {
        var (window, environment, log) = Create();
        environment.Displays = [DisplayInfo.Fallback];
        using var controller = new IslandPositionController(window, new IslandViewModel(), environment, log, new IslandSettings());

        Assert.True(controller.Start().IsSuccess);
        var second = controller.Start();

        Assert.True(second.IsFailure);
        Assert.Equal(ErrorCode.InvalidInput, second.Error!.Code);
        Assert.Equal(1, environment.StartCalls);
        window.Close();
    });

    [Fact]
    public void DisposeStopsFollowingDisplayChanges() => RunOnSta(() =>
    {
        var (window, environment, log) = Create();
        var bounds = new PixelRect(0, 0, 1920, 1080);
        environment.Displays = [DisplayLikeHost(window, "\\\\.\\DISPLAY1", true, bounds, new PixelRect(0, 0, 1920, 1040))];
        var controller = new IslandPositionController(window, new IslandViewModel(), environment, log, new IslandSettings());
        controller.Start();
        window.Show();
        try
        {
            var before = PixelsOf(window);

            controller.Dispose();
            controller.Dispose(); // idempotent
            environment.Displays = [DisplayLikeHost(window, "\\\\.\\DISPLAY1", true, bounds, new PixelRect(0, 100, 1920, 940))];
            environment.Raise();
            Pump(window);

            Assert.Equal(0, environment.SubscriberCount);
            Assert.Equal(before.Top, PixelsOf(window).Top);
            Assert.Throws<ObjectDisposedException>(() => controller.Start());
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void ConstructorRejectsNulls() => RunOnSta(() =>
    {
        var (window, environment, log) = Create();
        var preference = new IslandSettings();

        Assert.Throws<ArgumentNullException>(() => new IslandPositionController(null!, new IslandViewModel(), environment, log, preference));
        Assert.Throws<ArgumentNullException>(() => new IslandPositionController(window, new IslandViewModel(), null!, log, preference));
        Assert.Throws<ArgumentNullException>(() => new IslandPositionController(window, null!, environment, log, preference));
        Assert.Throws<ArgumentNullException>(() => new IslandPositionController(window, new IslandViewModel(), environment, null!, preference));
        Assert.Throws<ArgumentNullException>(() => new IslandPositionController(window, new IslandViewModel(), environment, log, null!));
        window.Close();
    });

    [Theory]
    [InlineData(1.0, 0, 0)]
    [InlineData(1.25, 860, 10)]
    [InlineData(1.5, 1281, 7)]
    [InlineData(1.75, 333, 99)]
    [InlineData(2.0, 3199, 1)]
    [InlineData(3.0, -1920, -200)]
    [InlineData(1.25, -1, -1)]
    [InlineData(1.5, -3839, -1079)]
    public void ConversionLandsOnTheIntendedPixelWhetherWpfRoundsOrTruncates(double scale, int x, int y)
    {
        var (left, top) = IslandPositionController.ToWindowOrigin(new PixelRect(x, y, 200, 55), scale, scale);

        Assert.Equal(x, (int)Math.Round(left * scale, MidpointRounding.AwayFromZero));
        Assert.Equal(x, (int)(left * scale)); // truncation toward zero
        Assert.Equal(y, (int)Math.Round(top * scale, MidpointRounding.AwayFromZero));
        Assert.Equal(y, (int)(top * scale));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ConversionRejectsInvalidScales(double scale) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            IslandPositionController.ToWindowOrigin(new PixelRect(0, 0, 1, 1), scale, 1));

    private sealed class FakeEnvironment : IWindowEnvironment
    {
        private Action? _changed;

        public IReadOnlyList<DisplayInfo> Displays { get; set; } = [];

        public OperationError? Failure { get; set; }

        public Result StartResult { get; set; } = Result.Success();

        public int StartCalls { get; private set; }

        public int SubscriberCount => _changed?.GetInvocationList().Length ?? 0;

        public event Action? DisplaysChanged
        {
            add => _changed += value;
            remove => _changed -= value;
        }

        public Result Start()
        {
            StartCalls++;
            return StartResult;
        }

        public Result<IReadOnlyList<DisplayInfo>> GetDisplays() =>
            Failure is null ? Result.Success(Displays) : Result.Failure<IReadOnlyList<DisplayInfo>>(Failure);

        public void Raise() => _changed?.Invoke();

        public void Dispose()
        {
        }
    }

    private sealed class FakeLog : IDiagnosticLog
    {
        private readonly List<DiagnosticEvent> _events = [];

        public IReadOnlyList<DiagnosticEvent> Events
        {
            get
            {
                lock (_events)
                    return [.. _events];
            }
        }

        public void Write(DiagnosticEvent diagnosticEvent)
        {
            lock (_events)
                _events.Add(diagnosticEvent);
        }
    }
}
