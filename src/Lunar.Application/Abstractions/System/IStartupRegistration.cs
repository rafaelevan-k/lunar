using Lunar.Domain.Shared;

namespace Lunar.Application.Abstractions.System;

/// <summary>
/// Opt-in "Start with Windows" for the current user only (PRD §7, ADR-0004). Nothing is registered until
/// <see cref="Enable"/> is called; the installer never enables it. Expected failures are
/// <see cref="Result"/> failures (<see cref="ErrorCode.InvalidInput"/>, <see cref="ErrorCode.AccessDenied"/>,
/// <see cref="ErrorCode.IoFailure"/>, <see cref="ErrorCode.Unsupported"/>). Synchronous and fast.
/// </summary>
public interface IStartupRegistration
{
    /// <summary>True only when the registration points at the running executable.</summary>
    Result<bool> IsEnabled();

    /// <summary>Registers (or repairs a stale registration to) the running executable.</summary>
    Result Enable();

    /// <summary>Removes Lunar's registration; succeeds when there is none.</summary>
    Result Disable();
}
