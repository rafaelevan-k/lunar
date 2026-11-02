using Lunar.Application.Abstractions.System;
using Lunar.Application.System.Models;
using Lunar.Domain.Shared;

namespace Lunar.Application.Tests.Fakes;

public sealed class FakeVolumeService : IVolumeService
{
    private readonly TestObservable<VolumeState> _changes = new();

    public VolumeState State { get; private set; } = new(0.5f, false, CapabilityStatus.Supported);

    public bool IsDisposed { get; private set; }

    public int ObserverCount => _changes.ObserverCount;

    public IObservable<VolumeState> Changes => _changes;

    public Task<Result<VolumeState>> GetAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Task.FromResult(Result.Cancelled<VolumeState>());
        if (IsDisposed)
            return Task.FromResult(Result.Failure<VolumeState>(Disposed));
        return Task.FromResult(Result.Success(State));
    }

    public Task<Result> SetVolumeAsync(float scalar, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Task.FromResult(Result.Cancelled());
        if (IsDisposed)
            return Task.FromResult(Result.Failure(Disposed));
        if (float.IsNaN(scalar) || scalar < 0f || scalar > 1f)
            return Task.FromResult(Result.Failure(new OperationError(ErrorCode.InvalidInput, "Volume must be between 0 and 1.")));

        Update(new VolumeState(scalar, State.IsMuted, State.Capability));
        return Task.FromResult(Result.Success());
    }

    public Task<Result> SetMuteAsync(bool muted, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Task.FromResult(Result.Cancelled());
        if (IsDisposed)
            return Task.FromResult(Result.Failure(Disposed));

        Update(new VolumeState(State.Scalar, muted, State.Capability));
        return Task.FromResult(Result.Success());
    }

    /// <summary>Simulates an external (OS-originated) change.</summary>
    public void RaiseExternalChange(VolumeState state) => Update(state);

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        _changes.Complete();
        return ValueTask.CompletedTask;
    }

    private static OperationError Disposed => new(ErrorCode.DeviceUnavailable, "The volume service is closed.");

    private void Update(VolumeState state)
    {
        State = state;
        _changes.Publish(state);
    }
}
