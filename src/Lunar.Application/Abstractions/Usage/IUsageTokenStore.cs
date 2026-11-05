using Lunar.Application.Usage.Models;
using Lunar.Domain.Shared;

namespace Lunar.Application.Abstractions.Usage;

/// <summary>
/// Keeps the token the user pasted for the Usage page (ADR-0014 amendment, BE-33) and hands it to <see cref="IUsageSource"/> through
/// <see cref="IUsageCredentialSource.GetAsync"/>. It is the one deliberate exception to "passwords only in memory": the user chose to paste
/// the token once instead of Lunar reading another program's login. What it promises:
/// - The token is stored only on this PC, only for the signed-in Windows user, and only encrypted; it is never written in clear text, never
///   logged, never shown again, never part of settings or diagnostics, and sent nowhere except by <see cref="IUsageSource"/> to Claude.
/// - <see cref="SaveAsync"/> checks the text with <see cref="UsageTokenRules"/> first and replaces any earlier token as a whole (all or nothing).
/// - <see cref="RemoveAsync"/> forgets it for good and succeeds when there is none.
/// - Messages are fixed text: they hold no token, no path and no exception text. A missing token and a token that can no longer be read are
///   both <see cref="ErrorCode.AccessDenied"/> with advice to paste it again.
/// Thread-safe; calls are serialized.
/// </summary>
public interface IUsageTokenStore : IUsageCredentialSource
{
    /// <summary>Whether a token is saved. It does not read or decrypt it.</summary>
    Task<Result<bool>> HasTokenAsync(CancellationToken ct);

    /// <summary>Checks and saves the pasted token, replacing an earlier one. An invalid text is <see cref="ErrorCode.InvalidInput"/>.</summary>
    Task<Result> SaveAsync(string pastedToken, CancellationToken ct);

    /// <summary>Deletes the saved token.</summary>
    Task<Result> RemoveAsync(CancellationToken ct);
}
