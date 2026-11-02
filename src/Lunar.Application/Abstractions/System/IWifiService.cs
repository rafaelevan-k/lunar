using Lunar.Application.System.Models;
using Lunar.Domain.Shared;

namespace Lunar.Application.Abstractions.System;

/// <summary>
/// Wi-Fi adapter/radio/connectivity state. Never claims connected merely because the radio is enabled; the
/// toggle succeeds only when the OS confirms it. Observable semantics are those documented on
/// <see cref="IVolumeService.Changes"/>.
/// </summary>
public interface IWifiService : IAsyncDisposable
{
    Task<Result<WifiState>> GetAsync(CancellationToken ct);

    Task<Result> SetEnabledAsync(bool enabled, CancellationToken ct);

    IObservable<WifiState> Changes { get; }
}
