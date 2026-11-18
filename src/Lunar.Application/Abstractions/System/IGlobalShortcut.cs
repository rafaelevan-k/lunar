using Lunar.Application.Translate;
using Lunar.Domain.Shared;

namespace Lunar.Application.Abstractions.System;

/// <summary>
/// One system-wide keyboard shortcut (ADR-0028 §1: screen translation). Registering replaces the previous shortcut. A shortcut another app already
/// holds fails with <see cref="ErrorCode.AccessDenied"/>, so Settings can say so and let the user pick another.
/// </summary>
public interface IGlobalShortcut : IDisposable
{
    /// <summary>The shortcut that is registered now, or null.</summary>
    Shortcut? Current { get; }

    Task<Result> RegisterAsync(Shortcut shortcut, CancellationToken ct);

    /// <summary>Releases the shortcut; harmless when none is registered.</summary>
    void Unregister();

    /// <summary>The user pressed the shortcut. Raised on a background thread; handlers must marshal to the UI.</summary>
    event Action? Pressed;
}
