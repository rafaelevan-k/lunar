using Lunar.Application.System.Models;
using Lunar.Domain.Shared;

namespace Lunar.Application.Abstractions.System;

/// <summary>
/// Master volume of the default output endpoint. See ADR-0006 for the shared semantics: expected failures
/// (including cancellation) are returned as <see cref="Result"/> failures; a successful command means the OS
/// confirmed it.
/// </summary>
public interface IVolumeService : IAsyncDisposable
{
    Task<Result<VolumeState>> GetAsync(CancellationToken ct);

    /// <param name="scalar">0..1; out-of-range values fail with <see cref="ErrorCode.InvalidInput"/>.</param>
    Task<Result> SetVolumeAsync(float scalar, CancellationToken ct);

    Task<Result> SetMuteAsync(bool muted, CancellationToken ct);

    /// <summary>
    /// No replay (call <see cref="GetAsync"/> for the initial value). Delivered serialized on an arbitrary
    /// thread, so ViewModels marshal to the Dispatcher. <c>OnError</c> is never called; <c>OnCompleted</c> is
    /// called when the service is disposed. Disposing a subscription is idempotent and thread-safe.
    /// </summary>
    IObservable<VolumeState> Changes { get; }
}
