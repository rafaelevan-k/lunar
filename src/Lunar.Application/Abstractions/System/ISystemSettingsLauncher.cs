using Lunar.Domain.Shared;

namespace Lunar.Application.Abstractions.System;

public interface ISystemSettingsLauncher
{
    /// <summary>A launch failure is a failed <see cref="Result"/>, never an exception that crashes the app.</summary>
    Task<Result> OpenAsync(SettingsPage page, CancellationToken ct);
}
