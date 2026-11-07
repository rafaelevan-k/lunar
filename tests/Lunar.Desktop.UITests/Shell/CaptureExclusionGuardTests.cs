using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Lunar.Application.Abstractions.Shell;
using Lunar.Desktop.Composition;
using Lunar.Desktop.Shell;
using Lunar.Domain.Shared;
using Lunar.Windows.Shell;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Lunar.Desktop.UITests.Shell;

// FE-30, ADR-0019: every Lunar window is hidden from screen sharing as soon as it has a handle, once per handle, and always: there is no
// setting. The fake port records when it was asked; the last tests use the real Windows adapter on the real island and tray menu. Whether
// meeting apps, Snipping Tool, Magnifier and Remote Desktop show Lunar is a manual check on the user's desktop (FE-30 evidence).
public sealed class CaptureExclusionGuardTests
{
    internal sealed record Call(nint Handle, bool WasVisible);

    internal sealed class FakeExclusion : ICaptureExclusion
    {
        public bool Refuse { get; init; }

        public bool RefuseWatch { get; init; }

        public List<Call> Excluded { get; } = [];

        public int Watches { get; private set; }

        public int WatchesStopped { get; private set; }

        public Result Exclude(nint windowHandle)
        {
            Excluded.Add(new Call(windowHandle, IsWindowVisible(windowHandle)));
            return Refuse ? Result.Failure(new OperationError(ErrorCode.AccessDenied, "Refused.")) : Result.Success();
        }

        public Result<IDisposable> ExcludeEveryNewWindow()
        {
            Watches++;
            return RefuseWatch
                ? Result.Failure<IDisposable>(new OperationError(ErrorCode.IoFailure, "No hook."))
                : Result.Success<IDisposable>(new Stop(this));
        }

        private sealed class Stop(FakeExclusion owner) : IDisposable
        {
            public void Dispose() => owner.WatchesStopped++;
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
            throw new InvalidOperationException("Capture exclusion check failed.", failure);
    }

    private static void Pump(int milliseconds = 0)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(Math.Max(1, milliseconds)) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static Window OffScreen() => new()
    {
        Width = 120,
        Height = 40,
        Left = -20000,
        Top = -20000,
        ShowActivated = false,
        ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.Manual,
    };

    [Fact]
    public void AWindowAttachedBeforeItHasAHandleIsHiddenWhenTheHandleIsCreatedBeforeItIsShownAndOnlyOnce() => RunOnSta(() =>
    {
        var exclusion = new FakeExclusion();
        using var guard = new CaptureExclusionGuard(exclusion);
        var window = OffScreen();

        guard.Attach(window);
        Assert.Empty(exclusion.Excluded);
        window.Show();
        window.Hide();
        window.Show();
        guard.Attach(window);

        var call = Assert.Single(exclusion.Excluded);
        Assert.Equal(new WindowInteropHelper(window).Handle, call.Handle);
        Assert.False(call.WasVisible);
        Assert.False(guard.IsRefused);
        window.Close();
    });

    [Fact]
    public void AWindowThatAlreadyHasAHandleIsHiddenAtOnce() => RunOnSta(() =>
    {
        var exclusion = new FakeExclusion();
        using var guard = new CaptureExclusionGuard(exclusion);
        var window = OffScreen();
        var handle = new WindowInteropHelper(window).EnsureHandle();

        guard.Attach(window);

        Assert.Equal(handle, Assert.Single(exclusion.Excluded).Handle);
        window.Close();
    });

    [Fact]
    public void ARefusalIsRememberedAndAnnouncedOnce() => RunOnSta(() =>
    {
        using var guard = new CaptureExclusionGuard(new FakeExclusion { Refuse = true });
        var announced = 0;
        guard.Changed += () => announced++;
        var first = OffScreen();
        var second = OffScreen();

        guard.Attach(first);
        guard.Attach(second);
        _ = new WindowInteropHelper(first).EnsureHandle();
        _ = new WindowInteropHelper(second).EnsureHandle();

        Assert.True(guard.IsRefused);
        Assert.Equal(1, announced);
        first.Close();
        second.Close();
    });

    [Fact]
    public void AWatcherThatCannotStartCountsAsARefusalAndAStartedOneStopsOnDispose()
    {
        var refused = new FakeExclusion { RefuseWatch = true };
        using (var guard = new CaptureExclusionGuard(refused))
        {
            guard.StartWatching();
            Assert.True(guard.IsRefused);
        }

        var exclusion = new FakeExclusion();
        var started = new CaptureExclusionGuard(exclusion);
        started.StartWatching();
        started.StartWatching();
        Assert.False(started.IsRefused);
        Assert.Equal(1, exclusion.Watches);

        started.Dispose();
        started.Dispose();
        Assert.Equal(1, exclusion.WatchesStopped);
    }

    [Fact]
    public void NothingInTheSettingsCanTurnItOff()
    {
        static IEnumerable<string> Names(Type type, int depth = 0) => depth > 3
            ? []
            : type.GetProperties().SelectMany(p => new[] { p.Name }.Concat(
                p.PropertyType.Namespace?.StartsWith("Lunar.", StringComparison.Ordinal) == true ? Names(p.PropertyType, depth + 1) : []));

        var settings = Names(typeof(Lunar.Application.Settings.Models.LunarSettings)).ToList();
        var model = typeof(Lunar.Desktop.Features.Settings.SettingsViewModel).GetProperties();

        Assert.DoesNotContain(settings, n => n.Contains("Capture", StringComparison.OrdinalIgnoreCase) || n.Contains("Sharing", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(model, p => (p.Name.Contains("Capture", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("ScreenSharing", StringComparison.Ordinal))
            && (p.CanWrite || typeof(System.Windows.Input.ICommand).IsAssignableFrom(p.PropertyType)));
        Assert.DoesNotContain(typeof(ICaptureExclusion).GetMethods(), m => m.Name.Contains("Include", StringComparison.Ordinal)
            || m.Name.Contains("Allow", StringComparison.Ordinal) || m.Name.Contains("Disable", StringComparison.Ordinal));
    }

    [Fact]
    public void TheContainerUsesTheRealWindowsAdapter()
    {
        using var services = ServiceRegistration.BuildServiceProvider();

        Assert.IsType<WindowsCaptureExclusion>(services.GetRequiredService<ICaptureExclusion>());
    }

    // ---- the real adapter --------------------------------------------------------------------------------------

    private const uint WdaExcludeFromCapture = 0x11;

    [Fact]
    public void TheRealIslandWindowIsHiddenBeforeItIsFirstShown() => RunOnSta(() =>
    {
        using var guard = new CaptureExclusionGuard(new WindowsCaptureExclusion());
        var island = new IslandWindow { Left = -20000, Top = -20000 };
        guard.Attach(island);
        var handle = new WindowInteropHelper(island).EnsureHandle();

        Assert.False(IsWindowVisible(handle));
        Assert.True(GetWindowDisplayAffinity(handle, out var before));
        island.Show();
        Assert.True(GetWindowDisplayAffinity(handle, out var after));

        Assert.Equal(WdaExcludeFromCapture, before);
        Assert.Equal(WdaExcludeFromCapture, after);
        Assert.False(guard.IsRefused);
        island.Close();
    });

    [Fact]
    public void UnderTheRealWatcherTheTrayMenuAndATooltipAreHidden() => RunOnSta(() =>
    {
        using var guard = new CaptureExclusionGuard(new WindowsCaptureExclusion());
        guard.StartWatching();
        Assert.False(guard.IsRefused);

        using var tray = new TrayController(() => { }, () => { }, visible: false);
        tray.Menu.Show(-20000, -20000);
        var menu = tray.Menu.Handle;
        uint menuAffinity = 0;
        for (var i = 0; i < 40 && menuAffinity != WdaExcludeFromCapture; i++)
        {
            Pump(25);
            GetWindowDisplayAffinity(menu, out menuAffinity);
        }

        tray.Menu.Close();

        var owner = OffScreen();
        owner.Show();
        var tip = new System.Windows.Controls.ToolTip { Content = "tip", PlacementTarget = owner, IsOpen = true };
        Pump(50);
        var tipHandle = ((HwndSource)PresentationSource.FromVisual(tip)).Handle;
        uint tipAffinity = 0;
        for (var i = 0; i < 40 && tipAffinity != WdaExcludeFromCapture; i++)
        {
            Pump(25);
            GetWindowDisplayAffinity(tipHandle, out tipAffinity);
        }

        tip.IsOpen = false;
        owner.Close();

        Assert.Equal(WdaExcludeFromCapture, menuAffinity);
        Assert.Equal(WdaExcludeFromCapture, tipAffinity);
    });

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);
}
