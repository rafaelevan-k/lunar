using System.Runtime.InteropServices;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Domain.Shared;
using Lunar.Windows.Environment;
using Xunit;

namespace Lunar.Windows.IntegrationTests.Environment;

// Real Win32 on the current machine. The posted messages simulate what Windows broadcasts on a display change;
// they prove the notification, coalescing and disposal path, NOT that Windows sends them for real hotplug or DPI
// changes (that needs hardware evidence, see ADR-0010).
public sealed class MonitorInfoProviderTests
{
    private const uint WM_DISPLAYCHANGE = 0x007E;
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint WM_DPICHANGED = 0x02E0;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(60);

    // Windows delivers these notifications synchronously and refuses to post them (error 1159), so tests send.
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    private static async Task<bool> WaitFor(Task task, TimeSpan timeout) =>
        await Task.WhenAny(task, Task.Delay(timeout)) == task;

    private sealed class CollectingLog : IDiagnosticLog
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

    [Fact]
    public void ReadsTheRealDisplaysWithConsistentGeometry()
    {
        using var provider = new MonitorInfoProvider(Debounce);

        var result = provider.GetDisplays();

        Assert.True(result.IsSuccess);
        var displays = result.Value;
        Assert.NotEmpty(displays);
        Assert.Single(displays, d => d.IsPrimary);
        Assert.True(displays[0].IsPrimary);
        Assert.Equal(displays.Count, displays.Select(d => d.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(displays, d =>
        {
            Assert.True(d.DpiX >= 96 && d.DpiY >= 96, "effective DPI is at least 100%");
            Assert.True(d.Bounds.Width > 0 && d.Bounds.Height > 0);
            Assert.True(d.Bounds.Contains(d.WorkArea));
        });
    }

    [Fact]
    public void PrimaryDisplaySizeMatchesAnIndependentMeasurementInPhysicalPixels()
    {
        using var provider = new MonitorInfoProvider(Debounce);
        var primary = provider.GetDisplays().Value[0];

        // Independent API (GetSystemMetrics) under the same per-monitor-v2 thread context the provider uses.
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            Assert.Equal(GetSystemMetrics(SM_CXSCREEN), primary.Bounds.Width);
            Assert.Equal(GetSystemMetrics(SM_CYSCREEN), primary.Bounds.Height);
        }
        finally
        {
            if (previous != IntPtr.Zero)
                SetThreadDpiAwarenessContext(previous);
        }
    }

    [Fact]
    public void GetDisplaysWorksWithoutStartAndRestoresTheCallerDpiContext()
    {
        using var provider = new MonitorInfoProvider(Debounce);
        var before = SetThreadDpiAwarenessContext(new IntPtr(-1)); // make this thread DPI-unaware
        try
        {
            // The call returns the context handle that was in place, so a second call yields the "unaware" handle.
            var unaware = SetThreadDpiAwarenessContext(new IntPtr(-1));
            Assert.NotEqual(IntPtr.Zero, unaware);

            Assert.True(provider.GetDisplays().IsSuccess);

            Assert.Equal(unaware, SetThreadDpiAwarenessContext(new IntPtr(-1)));
        }
        finally
        {
            if (before != IntPtr.Zero)
                SetThreadDpiAwarenessContext(before);
        }
    }

    [Fact]
    public void StartTwiceFailsAndStartAfterDisposeFails()
    {
        var provider = new MonitorInfoProvider(Debounce);

        Assert.True(provider.Start().IsSuccess);
        Assert.Equal(ErrorCode.InvalidInput, provider.Start().Error!.Code);

        provider.Dispose();
        Assert.Equal(ErrorCode.InvalidInput, provider.Start().Error!.Code);
    }

    [Theory]
    [InlineData(WM_DISPLAYCHANGE, 0)]
    [InlineData(WM_SETTINGCHANGE, 0x2F)] // SPI_SETWORKAREA
    public async Task RelevantWindowMessagesRaiseOneNotification(uint message, int wParam)
    {
        using var provider = new MonitorInfoProvider(Debounce);
        var raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.DisplaysChanged += () => raised.TrySetResult();
        Assert.True(provider.Start().IsSuccess);

        SendMessage(provider.WindowHandle, message, new IntPtr(wParam), IntPtr.Zero);

        Assert.True(await WaitFor(raised.Task, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task DpiChangedMessageRaisesANotification()
    {
        // Windows drops a WM_DPICHANGED sent by another process (observed: the window procedure is never called),
        // so a real one cannot be simulated. The window procedure is fed the message directly instead; whether
        // Windows delivers it on a real DPI change is hardware evidence (ADR-0010).
        using var provider = new MonitorInfoProvider(Debounce);
        var raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.DisplaysChanged += () => raised.TrySetResult();
        Assert.True(provider.Start().IsSuccess);

        provider.HandleMessage(provider.WindowHandle, WM_DPICHANGED, IntPtr.Zero, IntPtr.Zero);

        Assert.True(await WaitFor(raised.Task, TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(0x0000, 0)]
    [InlineData(WM_SETTINGCHANGE, 0x2A)] // another SPI action
    [InlineData(WM_SETTINGCHANGE, 0)]
    [InlineData(0x0111, 1)] // WM_COMMAND
    public async Task UnrelatedMessagesAreIgnored(uint message, int wParam)
    {
        using var provider = new MonitorInfoProvider(Debounce);
        var count = 0;
        provider.DisplaysChanged += () => Interlocked.Increment(ref count);
        Assert.True(provider.Start().IsSuccess);

        SendMessage(provider.WindowHandle, message, new IntPtr(wParam), IntPtr.Zero);
        await Task.Delay(400, TestContext.Current.CancellationToken);

        Assert.Equal(0, Volatile.Read(ref count));
    }

    [Fact]
    public async Task ABurstOfMessagesIsCoalescedIntoOneNotification()
    {
        using var provider = new MonitorInfoProvider(TimeSpan.FromMilliseconds(150));
        var count = 0;
        provider.DisplaysChanged += () => Interlocked.Increment(ref count);
        Assert.True(provider.Start().IsSuccess);

        for (var i = 0; i < 10; i++)
        {
            SendMessage(provider.WindowHandle, WM_DISPLAYCHANGE, IntPtr.Zero, IntPtr.Zero);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await Task.Delay(700, TestContext.Current.CancellationToken);
        Assert.Equal(1, Volatile.Read(ref count));

        SendMessage(provider.WindowHandle, WM_DISPLAYCHANGE, IntPtr.Zero, IntPtr.Zero);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.Equal(2, Volatile.Read(ref count));
    }

    [Fact]
    public async Task ThrowingHandlerIsIsolatedAndLoggedByTypeOnly()
    {
        var log = new CollectingLog();
        using var provider = new MonitorInfoProvider(Debounce, log);
        var calls = 0;
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.DisplaysChanged += () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new InvalidOperationException(@"C:\Users\alice\secret.docx");
            second.TrySetResult();
        };
        Assert.True(provider.Start().IsSuccess);

        SendMessage(provider.WindowHandle, WM_DISPLAYCHANGE, IntPtr.Zero, IntPtr.Zero);
        await Task.Delay(400, TestContext.Current.CancellationToken);
        SendMessage(provider.WindowHandle, WM_DISPLAYCHANGE, IntPtr.Zero, IntPtr.Zero);

        Assert.True(await WaitFor(second.Task, TimeSpan.FromSeconds(5)));
        var logged = Assert.Single(log.Events);
        Assert.Equal("display.handler.failed", logged.EventId);
        Assert.Equal(typeof(InvalidOperationException), logged.ExceptionType);
        Assert.DoesNotContain("alice", logged.ToString());
    }

    [Fact]
    public async Task DisposeDestroysTheWindowEndsTheThreadAndSilencesEvents()
    {
        var provider = new MonitorInfoProvider(Debounce);
        var count = 0;
        provider.DisplaysChanged += () => Interlocked.Increment(ref count);
        Assert.True(provider.Start().IsSuccess);
        var window = provider.WindowHandle;
        Assert.True(IsWindow(window));

        SendMessage(window, WM_DISPLAYCHANGE, IntPtr.Zero, IntPtr.Zero); // pending when disposed
        provider.Dispose();
        provider.Dispose();

        Assert.True(await WaitFor(provider.ListenerCompletion, TimeSpan.FromSeconds(5)));
        Assert.False(IsWindow(window));
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(0, Volatile.Read(ref count));
    }

    [Fact]
    public async Task DisposeWithoutStartCompletesImmediately()
    {
        var provider = new MonitorInfoProvider(Debounce);

        provider.Dispose();

        Assert.True(await WaitFor(provider.ListenerCompletion, TimeSpan.FromSeconds(2)));
    }
}


