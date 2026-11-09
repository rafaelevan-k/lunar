using Lunar.Domain.Shared;

namespace Lunar.Application.Abstractions.System;

/// <summary>
/// Whether Windows' Do Not Disturb is on. <see cref="RestoreToken"/> is opaque: hand it back to <see cref="IFocusModeControl.RestoreAsync"/> to put the
/// state back exactly as it was. It may be saved between runs. It holds no personal data.
/// </summary>
public sealed record FocusModeState(bool IsOn, string RestoreToken);

/// <summary>
/// Windows' Do Not Disturb (ADR-0023). Windows offers no public way to set it, so this may stop working after a Windows update; every failure is typed, and
/// success is never claimed without Windows confirming it. Calls run off the calling thread and are serialized.
/// Failures: <see cref="ErrorCode.Unsupported"/> (this Windows has no such control), <see cref="ErrorCode.AccessDenied"/> (Windows refused),
/// <see cref="ErrorCode.Timeout"/> (no answer, or not confirmed), <see cref="ErrorCode.Unexpected"/> (retryable), <see cref="ErrorCode.Cancelled"/>.
/// </summary>
public interface IFocusModeControl
{
    Task<Result<FocusModeState>> ReadAsync(CancellationToken ct);

    /// <summary>
    /// Holds back notifications other than alarms. Success means Windows itself reports the change; if it does not, the earlier state is put back and
    /// the call fails with <see cref="ErrorCode.Timeout"/>.
    /// </summary>
    Task<Result> TurnOnAsync(CancellationToken ct);

    /// <summary>Puts back a state read earlier. A token this control did not produce is <see cref="ErrorCode.InvalidInput"/>.</summary>
    Task<Result> RestoreAsync(string restoreToken, CancellationToken ct);
}
