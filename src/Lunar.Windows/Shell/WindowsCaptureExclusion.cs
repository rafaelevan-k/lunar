using System.Runtime.InteropServices;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Shell;
using Lunar.Domain.Shared;

namespace Lunar.Windows.Shell;

/// <summary>
/// <see cref="ICaptureExclusion"/> with <c>SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)</c>, confirmed with
/// <c>GetWindowDisplayAffinity</c> (ADR-0019). Stateless and callable from any thread. A failure is logged with a
/// fixed event name and its error code only.
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

/// <summary>The three user32 calls behind <see cref="WindowsCaptureExclusion"/>, so the result mapping can be tested.</summary>
internal interface ICaptureAffinityNative
{
    bool IsWindow(nint hwnd);

    bool SetAffinity(nint hwnd, uint affinity, out int win32Error);

    bool GetAffinity(nint hwnd, out uint affinity, out int win32Error);
}

internal sealed class User32CaptureAffinity : ICaptureAffinityNative
{
    public bool IsWindow(nint hwnd) => IsWindowNative(hwnd);

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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);
}
