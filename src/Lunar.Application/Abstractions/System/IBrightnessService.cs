using Lunar.Application.System.Models;
using Lunar.Domain.Shared;

namespace Lunar.Application.Abstractions.System;

/// <summary>
/// Brightness of one display (ADR-0021): a built-in panel through Windows, an external monitor through DDC/CI. A display is named by
/// its DisplayInfo.Id, the Windows device name such as DISPLAY1 (null means the primary display). No elevation is ever needed. Every call runs off the
/// calling thread and calls are serialized. Expected failures, cancellation included, are <see cref="Result"/> failures; a display that
/// cannot be changed is a successful read whose <see cref="BrightnessState.Capability"/> says so.
/// </summary>
public interface IBrightnessService : IAsyncDisposable
{
    /// <summary>
    /// Reads the display's brightness and how it can be changed. Fails with <see cref="ErrorCode.IoFailure"/> when Windows or the monitor
    /// does not answer, and <see cref="ErrorCode.AccessDenied"/> when Windows refuses.
    /// </summary>
    Task<Result<BrightnessState>> GetAsync(string? displayId, CancellationToken ct);

    /// <summary>
    /// Sets the display's brightness. <paramref name="percent"/> outside 0–100 fails with <see cref="ErrorCode.InvalidInput"/>; a display that
    /// cannot be changed fails with <see cref="ErrorCode.Unsupported"/>. Success means Windows or the monitor accepted the value; a panel with
    /// fewer steps may settle on the nearest one, so callers read it back.
    /// </summary>
    Task<Result> SetAsync(string? displayId, int percent, CancellationToken ct);

    /// <summary>
    /// A built-in panel's brightness changed outside Lunar (keys, Windows Settings, power saving). Carries the display's id; read it again with
    /// <see cref="GetAsync"/>. Raised on a background thread; handlers marshal to the UI and must not throw (a throwing handler is isolated).
    /// External monitors send no such news: a caller that shows them reads again now and then, only while it is visible.
    /// </summary>
    event Action<string>? Changed;
}
