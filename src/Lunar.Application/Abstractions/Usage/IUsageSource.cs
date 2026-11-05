using Lunar.Application.Usage.Models;
using Lunar.Domain.Shared;

namespace Lunar.Application.Abstractions.Usage;

/// <summary>
/// Reads the user's own Claude usage limits (ADR-0014, BE-31): how much of the five-hour and the weekly limit is used and when each resets.
/// This is the one place Lunar talks to the network, and only for this and only after the user turned the Usage page on (the caller
/// decides that; this port never starts by itself). One call is one request; it keeps no state, no cache and no schedule (polling is BE-32).
///
/// Failures are typed and carry a message that holds no token, URL or response text:
/// <see cref="ErrorCode.AccessDenied"/> (no login, or the service refused it: sign in again), <see cref="ErrorCode.DeviceUnavailable"/>
/// (no network or the service is down; retryable), <see cref="ErrorCode.Timeout"/> (retryable), <see cref="ErrorCode.Unexpected"/> (the
/// reply is not in the shape this version understands, so nothing is shown), <see cref="ErrorCode.Cancelled"/>. A service that asks to slow
/// down is <see cref="ErrorCode.DeviceUnavailable"/> and retryable. Thread-safe.
/// </summary>
public interface IUsageSource
{
    Task<Result<UsageSnapshot>> FetchAsync(CancellationToken ct);
}

/// <summary>
/// Gives the user's own login for <see cref="IUsageSource"/>. It reads a stored login only when asked and only the one the user allowed
/// (ADR-0014); it never asks for a password and never writes anything. No login is <see cref="ErrorCode.AccessDenied"/> with a message that says
/// to sign in to Claude.
/// </summary>
public interface IUsageCredentialSource
{
    Task<Result<UsageCredential>> GetAsync(CancellationToken ct);
}
