using Lunar.Application.Settings.Models;
using Lunar.Domain.Shared;

namespace Lunar.Application.Abstractions.Settings;

/// <summary>
/// Persistent user settings (ADR-0011). Calls are serialized by the implementation; there are no change events,
/// consumers reload through <see cref="LoadAsync"/>.
/// </summary>
public interface ISettingsRepository
{
    /// <summary>
    /// Always yields usable settings for any file content: missing, corrupt or invalid content is repaired and
    /// reported in <see cref="SettingsLoadResult.Recovery"/>. Fails only with <c>Cancelled</c> or when the file
    /// cannot be read at all (<c>AccessDenied</c>, <c>IoFailure</c>); the file is then left untouched.
    /// </summary>
    Task<Result<SettingsLoadResult>> LoadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Normalizes and atomically writes the settings, keeping the previous good file as a backup. Success means the
    /// data is on disk. Fails with <c>Unsupported</c> when the stored file comes from a newer Lunar.
    /// </summary>
    Task<Result> SaveAsync(LunarSettings settings, CancellationToken cancellationToken);
}
