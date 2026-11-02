using Lunar.Application.Shell.Models;
using Lunar.Domain.Shared;

namespace Lunar.Application.Abstractions.Shell;

/// <summary>
/// Display layout, work area and DPI of the current session (ADR-0010). Expected failures are
/// <see cref="Result"/> failures; no native type crosses this port.
/// </summary>
public interface IWindowEnvironment : IDisposable
{
    /// <summary>
    /// Begins change notification. Call once; a second call fails with <see cref="ErrorCode.InvalidInput"/>,
    /// and a listener that cannot start fails with <see cref="ErrorCode.IoFailure"/>. Waits at most a short,
    /// bounded time for the listener to be ready. <see cref="GetDisplays"/> works without it.
    /// </summary>
    Result Start();

    /// <summary>
    /// Current snapshot, primary display first. Synchronous and fast. Fails with
    /// <see cref="ErrorCode.DeviceUnavailable"/> when no display can be read (for example a locked or
    /// disconnected session); callers then use <see cref="DisplayInfo.Fallback"/>.
    /// </summary>
    Result<IReadOnlyList<DisplayInfo>> GetDisplays();

    /// <summary>
    /// Layout, work area (taskbar) or DPI may have changed; re-read with <see cref="GetDisplays"/>. Carries no
    /// data, is raised on a thread-pool thread, and a burst is coalesced into one event after a short quiet
    /// period. Handlers must marshal to the Dispatcher and must not throw; a throwing handler is isolated.
    /// </summary>
    event Action? DisplaysChanged;
}
