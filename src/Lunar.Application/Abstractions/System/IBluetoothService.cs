using Lunar.Application.System.Models;
using Lunar.Domain.Shared;

namespace Lunar.Application.Abstractions.System;

/// <summary>
/// Bluetooth radio capability and state; unsupported, denied and no-radio stay distinguishable. Observable
/// semantics are those documented on <see cref="IVolumeService.Changes"/>.
/// </summary>
public interface IBluetoothService : IAsyncDisposable
{
    Task<Result<BluetoothState>> GetAsync(CancellationToken ct);

    Task<Result> SetEnabledAsync(bool enabled, CancellationToken ct);

    IObservable<BluetoothState> Changes { get; }
}
