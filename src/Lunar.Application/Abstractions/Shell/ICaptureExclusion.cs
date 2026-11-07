using Lunar.Domain.Shared;

namespace Lunar.Application.Abstractions.Shell;

/// <summary>
/// Keeps one of Lunar's own top-level windows out of screen sharing, screenshots and recordings while it stays
/// visible on the user's display (ADR-0019). Always on: there is no way to turn it back off through this port.
/// The handle is the window's HWND as a number; no native type crosses this port. Expected failures are
/// <see cref="Result"/> failures, never exceptions.
/// </summary>
public interface ICaptureExclusion
{
    /// <summary>
    /// Excludes the window from capture and confirms it by reading the applied setting back. Safe to call again
    /// for the same window. Fails with:
    /// <list type="bullet">
    /// <item><see cref="ErrorCode.InvalidInput"/> when the handle is zero or not a live window;</item>
    /// <item><see cref="ErrorCode.Unsupported"/> when this Windows build cannot exclude windows from capture, or
    /// the setting read back is not the one asked for;</item>
    /// <item><see cref="ErrorCode.AccessDenied"/> when Windows refuses, for example for a window of another
    /// process or a child window.</item>
    /// </list>
    /// </summary>
    Result Exclude(nint windowHandle);
}
