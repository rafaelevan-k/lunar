using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Shell;
using Lunar.Application.Shell.Models;
using Lunar.Domain.Shared;
using Lunar.Windows.Interop;

namespace Lunar.Windows.Environment;

/// <summary>
/// Display enumeration and change notification (ADR-0010). Enumeration runs under a temporary per-monitor-v2 DPI
/// context so values are physical pixels whatever the host process's DPI awareness. Notification uses a hidden,
/// never-shown top-level window on one dedicated thread; only the message ids are inspected, never their data.
/// </summary>
public sealed class MonitorInfoProvider : IWindowEnvironment
{
    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(2);

    private readonly IDiagnosticLog? _log;
    private readonly TimeSpan _debounce;
    private readonly object _gate = new();
    private readonly TaskCompletionSource _listenerDone = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private NativeMethods.WndProc? _wndProc;
    private Timer? _timer;
    private IntPtr _window;
    private bool _started;
    private bool _disposed;

    public MonitorInfoProvider(IDiagnosticLog? log = null)
        : this(DefaultDebounce, log)
    {
    }

    internal MonitorInfoProvider(TimeSpan debounce, IDiagnosticLog? log = null)
    {
        _debounce = debounce;
        _log = log;
    }

    public event Action? DisplaysChanged;

    /// <summary>Handle of the hidden notification window; lets tests post real window messages to it.</summary>
    internal IntPtr WindowHandle => _window;

    /// <summary>Completes after the listener thread has destroyed its window and ended.</summary>
    internal Task ListenerCompletion => _listenerDone.Task;

    public Result<IReadOnlyList<DisplayInfo>> GetDisplays()
    {
        var displays = new List<DisplayInfo>();
        var skipped = 0;

        var previous = NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.PerMonitorAwareV2);
        try
        {
            var enumerated = NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
            {
                // A callback must not throw into native code.
                try
                {
                    var display = Read(monitor);
                    if (display is null)
                        skipped++;
                    else
                        displays.Add(display);
                }
                catch (ArgumentException)
                {
                    skipped++;
                }

                return true;
            }, IntPtr.Zero);

            if (!enumerated)
                return Fail("display.enumerate.failed");
        }
        finally
        {
            if (previous != IntPtr.Zero)
                NativeMethods.SetThreadDpiAwarenessContext(previous);
        }

        if (skipped > 0)
            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "display.enumerate.skipped", "Display", "Enumerate", ErrorCode.Unexpected));

        if (displays.Count == 0)
            return Fail("display.enumerate.empty");

        displays.Sort((a, b) => a.IsPrimary != b.IsPrimary
            ? (a.IsPrimary ? -1 : 1)
            : a.Bounds.X != b.Bounds.X ? a.Bounds.X.CompareTo(b.Bounds.X) : a.Bounds.Y.CompareTo(b.Bounds.Y));
        return Result.Success<IReadOnlyList<DisplayInfo>>(displays);
    }

    public Result Start()
    {
        lock (_gate)
        {
            if (_disposed)
                return Result.Failure(new OperationError(ErrorCode.InvalidInput, "The display environment is closed."));
            if (_started)
                return Result.Failure(new OperationError(ErrorCode.InvalidInput, "The display environment was already started."));
            _started = true;
        }

        // Not disposed on purpose: the listener thread signals it again when it ends, possibly after Start returned.
        var ready = new ManualResetEventSlim(false);
        var failed = false;
        var thread = new Thread(() => RunMessageLoop(ready, () => failed = true))
        {
            IsBackground = true,
            Name = "Lunar.DisplayWatcher"
        };
        thread.Start();

        // The only blocking wait: bounded, once, at startup, so the caller learns whether notifications work.
        if (!ready.Wait(StartTimeout) || failed)
        {
            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "display.listener.failed", "Display", "Start", ErrorCode.IoFailure));
            return Result.Failure(new OperationError(ErrorCode.IoFailure, "Display change notifications could not be started."));
        }

        return Result.Success();
    }

    public void Dispose()
    {
        IntPtr window;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            window = _window;
            _timer?.Dispose();
            _timer = null;
        }

        // The listener thread destroys the window and ends itself; Dispose never waits for it.
        if (window != IntPtr.Zero)
            NativeMethods.PostMessage(window, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        else
            _listenerDone.TrySetResult();
    }

    private Result<IReadOnlyList<DisplayInfo>> Fail(string eventId)
    {
        _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, eventId, "Display", "Enumerate", ErrorCode.DeviceUnavailable));
        return Result.Failure<IReadOnlyList<DisplayInfo>>(
            new OperationError(ErrorCode.DeviceUnavailable, "No display could be read.", retryable: true));
    }

    private static DisplayInfo? Read(IntPtr monitor)
    {
        var info = new NativeMethods.MonitorInfoEx { Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfoEx>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info) || string.IsNullOrWhiteSpace(info.Device))
            return null;

        uint dpiX = DisplayInfo.BaselineDpi, dpiY = DisplayInfo.BaselineDpi;
        if (NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var x, out var y) == 0 && x > 0 && y > 0)
        {
            dpiX = x;
            dpiY = y;
        }

        return new DisplayInfo(
            info.Device,
            (info.Flags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
            ToRect(info.Monitor),
            ToRect(info.Work),
            (int)dpiX,
            (int)dpiY);
    }

    private static PixelRect ToRect(NativeMethods.Rect r) =>
        new(r.Left, r.Top, Math.Max(0, r.Right - r.Left), Math.Max(0, r.Bottom - r.Top));

    private void RunMessageLoop(ManualResetEventSlim ready, Action markFailed)
    {
        var className = "Lunar.DisplayWatcher." + Guid.NewGuid().ToString("N");
        var instance = NativeMethods.GetModuleHandle(null);
        var registered = false;
        try
        {
            // A window only receives WM_DPICHANGED if it is per-monitor aware, whatever the host process declares.
            NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.PerMonitorAwareV2);
            _wndProc = HandleMessage;
            var windowClass = new NativeMethods.WndClassEx
            {
                Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WndClassEx>(),
                WndProc = _wndProc,
                Instance = instance,
                ClassName = className
            };
            registered = NativeMethods.RegisterClassEx(ref windowClass) != 0;
            var window = registered
                ? NativeMethods.CreateWindowEx(NativeMethods.WS_EX_TOOLWINDOW, className, null, NativeMethods.WS_POPUP,
                    0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero)
                : IntPtr.Zero;

            if (window == IntPtr.Zero)
            {
                markFailed();
                return;
            }

            bool closeNow;
            lock (_gate)
            {
                _window = window;
                closeNow = _disposed;
                if (!closeNow)
                    _timer = new Timer(_ => RaiseChanged(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }

            ready.Set();
            if (closeNow)
                NativeMethods.DestroyWindow(window);

            while (NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessage(ref message);
            }
        }
        finally
        {
            if (registered)
                NativeMethods.UnregisterClass(className, instance);
            _wndProc = null;
            ready.Set();
            _listenerDone.TrySetResult();
        }
    }

    /// <summary>Window procedure; internal so tests can feed it messages Windows refuses to deliver synthetically (WM_DPICHANGED).</summary>
    internal IntPtr HandleMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case NativeMethods.WM_DISPLAYCHANGE:
            case NativeMethods.WM_DPICHANGED:
                Schedule();
                return IntPtr.Zero;
            case NativeMethods.WM_SETTINGCHANGE when wParam.ToInt64() == NativeMethods.SPI_SETWORKAREA:
                Schedule();
                return IntPtr.Zero;
            case NativeMethods.WM_CLOSE:
                NativeMethods.DestroyWindow(hwnd);
                return IntPtr.Zero;
            case NativeMethods.WM_DESTROY:
                NativeMethods.PostQuitMessage(0);
                return IntPtr.Zero;
            default:
                return NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
        }
    }

    private void Schedule()
    {
        lock (_gate)
        {
            if (!_disposed)
                _timer?.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void RaiseChanged()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
        }

        try
        {
            DisplaysChanged?.Invoke();
        }
        catch (Exception ex)
        {
            // Contract: handlers must not throw. One bad handler must not stop later notifications.
            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Error, "display.handler.failed", "Display", "Notify",
                ErrorCode.Unexpected, ExceptionType: ex.GetType()));
        }
    }
}

