using System.Runtime.InteropServices;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Shell;
using Lunar.Domain.Shared;

namespace Lunar.Windows.Shell;

/// <summary>
/// <see cref="ICaptureExclusion"/> with <c>SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)</c>, confirmed with
/// <c>GetWindowDisplayAffinity</c> (ADR-0019). <see cref="Exclude"/> is callable from any thread.
/// <see cref="ExcludeEveryNewWindow"/> adds an out-of-context WinEvent hook limited to this process, so every top-level
/// window it creates or shows is excluded too (FE-30). A failure is logged with a fixed event name and its error code only.
/// </summary>
public sealed class WindowsCaptureExclusion : ICaptureExclusion
{
    /// <summary>Windows 10 version 2004, the first build that knows WDA_EXCLUDEFROMCAPTURE.</summary>
    internal const int FirstSupportedBuild = 19041;

    internal const uint WdaExcludeFromCapture = 0x00000011;

    internal const int ErrorAccessDenied = 5;
    internal const int ErrorInvalidParameter = 87;
    internal const int ErrorInvalidWindowHandle = 1400;

    private readonly ICaptureAffinityNative _native;
    private readonly int _osBuild;
    private readonly IDiagnosticLog? _log;
    private readonly Lock _gate = new();
    private Watcher? _watcher;

    public WindowsCaptureExclusion(IDiagnosticLog? log = null)
        : this(new User32CaptureAffinity(), System.Environment.OSVersion.Version.Build, log)
    {
    }

    internal WindowsCaptureExclusion(ICaptureAffinityNative native, int osBuild, IDiagnosticLog? log = null)
    {
        _native = native;
        _osBuild = osBuild;
        _log = log;
    }

    public Result Exclude(nint windowHandle)
    {
        if (_osBuild < FirstSupportedBuild)
            return Fail(ErrorCode.Unsupported, "This version of Windows cannot hide Lunar from screen sharing.");

        if (windowHandle == 0 || !_native.IsWindow(windowHandle))
            return Fail(ErrorCode.InvalidInput, "That is not an open window.");

        if (!_native.SetAffinity(windowHandle, WdaExcludeFromCapture, out var setError))
            return FromWin32(setError);

        if (!_native.GetAffinity(windowHandle, out var applied, out var getError))
            return FromWin32(getError);

        return applied == WdaExcludeFromCapture
            ? Result.Success()
            : Fail(ErrorCode.Unsupported, "Windows did not hide this window from screen sharing.");
    }

    public Result<IDisposable> ExcludeEveryNewWindow()
    {
        if (_osBuild < FirstSupportedBuild)
            return Result.Failure<IDisposable>(Fail(ErrorCode.Unsupported, "This version of Windows cannot hide Lunar from screen sharing.").Error!);

        lock (_gate)
        {
            if (_watcher is not null)
                return Result.Failure<IDisposable>(new OperationError(ErrorCode.InvalidInput, "Lunar is already watching for new windows."));

            var watcher = new Watcher(this);
            if (!watcher.Start())
            {
                _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "capture.watch.failed", "Shell", "ExcludeEveryNewWindow", ErrorCode.IoFailure));
                return Result.Failure<IDisposable>(new OperationError(ErrorCode.IoFailure, "Lunar could not watch for new windows."));
            }

            _watcher = watcher;
            return Result.Success<IDisposable>(watcher);
        }
    }

    /// <summary>One WinEvent notification. Only a top-level window itself (not a child, not an accessible part) is handled.</summary>
    internal void OnWindowEvent(uint winEvent, nint hwnd, int objectId, int childId)
    {
        if (hwnd == 0 || objectId != ObjIdWindow || childId != 0 || _native.GetRoot(hwnd) != hwnd)
            return;
        if (_native.GetAffinity(hwnd, out var current, out _) && current == WdaExcludeFromCapture)
            return;

        if (winEvent == EventObjectShow)
            Exclude(hwnd); // a refusal is logged with its code
        else
            _native.SetAffinity(hwnd, WdaExcludeFromCapture, out _); // created hidden: try quietly, SHOW tries again with logging
    }

    internal const uint EventObjectCreate = 0x8000;
    internal const uint EventObjectShow = 0x8002;
    internal const int ObjIdWindow = 0;

    private void Stopped(Watcher watcher)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_watcher, watcher))
                _watcher = null;
        }
    }

    private sealed class Watcher(WindowsCaptureExclusion owner) : IDisposable
    {
        private WinEventCallback? _callback; // kept alive while the hook exists
        private nint _hook;

        internal bool Start()
        {
            _callback = (_, winEvent, hwnd, objectId, childId, _, _) => owner.OnWindowEvent(winEvent, hwnd, objectId, childId);
            _hook = owner._native.InstallHook(EventObjectCreate, EventObjectShow, _callback);
            return _hook != 0;
        }

        public void Dispose()
        {
            if (_hook == 0)
                return;
            owner._native.RemoveHook(_hook);
            _hook = 0;
            _callback = null;
            owner.Stopped(this);
        }
    }

    internal Result FromWin32(int error) => error switch
    {
        ErrorInvalidWindowHandle => Fail(ErrorCode.InvalidInput, "That is not an open window."),
        _ => Fail(ErrorCode.AccessDenied, "Windows refused to hide this window from screen sharing."),
    };

    private Result Fail(ErrorCode code, string message)
    {
        _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "capture.exclude.failed", "Shell", "ExcludeFromCapture", code));
        return Result.Failure(new OperationError(code, message));
    }
}

internal delegate void WinEventCallback(nint hook, uint winEvent, nint hwnd, int objectId, int childId, uint thread, uint time);

/// <summary>The user32 calls behind <see cref="WindowsCaptureExclusion"/>, so the result mapping can be tested.</summary>
internal interface ICaptureAffinityNative
{
    bool IsWindow(nint hwnd);

    /// <summary>GetAncestor(GA_ROOT): the window itself for a top-level window.</summary>
    nint GetRoot(nint hwnd);

    /// <summary>An out-of-context WinEvent hook for this process only, delivered on the calling thread; 0 when refused.</summary>
    nint InstallHook(uint firstEvent, uint lastEvent, WinEventCallback callback);

    void RemoveHook(nint hook);

    bool SetAffinity(nint hwnd, uint affinity, out int win32Error);

    bool GetAffinity(nint hwnd, out uint affinity, out int win32Error);
}

internal sealed class User32CaptureAffinity : ICaptureAffinityNative
{
    private const uint WinEventOutOfContext = 0;
    private const uint GaRoot = 2;

    public bool IsWindow(nint hwnd) => IsWindowNative(hwnd);

    public nint GetRoot(nint hwnd) => GetAncestor(hwnd, GaRoot);

    public nint InstallHook(uint firstEvent, uint lastEvent, WinEventCallback callback) =>
        SetWinEventHook(firstEvent, lastEvent, 0, callback, (uint)System.Environment.ProcessId, 0, WinEventOutOfContext);

    public void RemoveHook(nint hook) => UnhookWinEvent(hook);

    public bool SetAffinity(nint hwnd, uint affinity, out int win32Error)
    {
        var ok = SetWindowDisplayAffinity(hwnd, affinity);
        win32Error = ok ? 0 : Marshal.GetLastPInvokeError();
        return ok;
    }

    public bool GetAffinity(nint hwnd, out uint affinity, out int win32Error)
    {
        var ok = GetWindowDisplayAffinity(hwnd, out affinity);
        win32Error = ok ? 0 : Marshal.GetLastPInvokeError();
        return ok;
    }

    [DllImport("user32.dll", EntryPoint = "IsWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowNative(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventCallback callback,
        uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);
}
