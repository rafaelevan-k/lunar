using Lunar.Domain.Shared;

namespace Lunar.Application.Abstractions.System;

/// <summary>
/// Remembers on disk that Lunar turned Do Not Disturb on, and what it was before, so an unexpected stop can be repaired at the next start (ADR-0023).
/// Expected failures are typed. A damaged marker reads as none.
/// </summary>
public interface IFocusSessionMarker
{
    /// <summary>The restore token saved earlier, or null when there is none.</summary>
    Task<Result<string?>> ReadAsync(CancellationToken ct);

    Task<Result> SaveAsync(string restoreToken, CancellationToken ct);

    Task<Result> ClearAsync(CancellationToken ct);
}
