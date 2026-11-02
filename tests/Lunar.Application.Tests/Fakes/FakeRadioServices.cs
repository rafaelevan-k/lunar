using Lunar.Application.Abstractions.System;
using Lunar.Application.System.Models;
using Lunar.Domain.Shared;

namespace Lunar.Application.Tests.Fakes;

public sealed class FakeWifiService : IWifiService
{
    private readonly TestObservable<WifiState> _changes = new();

    public WifiState State { get; set; } =
        new(CapabilityStatus.Supported, RadioState.Enabled, WifiConnectivity.Disconnected, CanToggle: true);

    public bool IsDisposed { get; private set; }

    public IObservable<WifiState> Changes => _changes;

    public Task<Result<WifiState>> GetAsync(CancellationToken ct) =>
        Task.FromResult(ct.IsCancellationRequested ? Result.Cancelled<WifiState>() : Result.Success(State));

    public Task<Result> SetEnabledAsync(bool enabled, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Task.FromResult(Result.Cancelled());
        if (!State.CanToggle)
            return Task.FromResult(Result.Failure(new OperationError(ErrorCode.Unsupported, "Wi-Fi cannot be toggled here.")));

        State = State with { Radio = enabled ? RadioState.Enabled : RadioState.Disabled };
        _changes.Publish(State);
        return Task.FromResult(Result.Success());
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        _changes.Complete();
        return ValueTask.CompletedTask;
    }
}

public sealed class FakeBluetoothService : IBluetoothService
{
    private readonly TestObservable<BluetoothState> _changes = new();

    public BluetoothState State { get; set; } = new(CapabilityStatus.Supported, RadioState.Enabled, CanToggle: true);

    public bool IsDisposed { get; private set; }

    public IObservable<BluetoothState> Changes => _changes;

    public Task<Result<BluetoothState>> GetAsync(CancellationToken ct) =>
        Task.FromResult(ct.IsCancellationRequested ? Result.Cancelled<BluetoothState>() : Result.Success(State));

    public Task<Result> SetEnabledAsync(bool enabled, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Task.FromResult(Result.Cancelled());
        if (!State.CanToggle)
            return Task.FromResult(Result.Failure(new OperationError(ErrorCode.AccessDenied, "Bluetooth cannot be toggled here.")));

        State = State with { Radio = enabled ? RadioState.Enabled : RadioState.Disabled };
        _changes.Publish(State);
        return Task.FromResult(Result.Success());
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        _changes.Complete();
        return ValueTask.CompletedTask;
    }
}
