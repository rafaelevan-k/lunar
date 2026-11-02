using Lunar.Domain.Shared;

namespace Lunar.Application.Abstractions.Shell;

/// <summary>
/// Single-instance policy per Windows session (ADR-0008). Expected failures are <see cref="Result"/> failures
/// (<see cref="ErrorCode.IoFailure"/>, <see cref="ErrorCode.Timeout"/>, <see cref="ErrorCode.Cancelled"/>).
/// </summary>
public interface IInstanceCoordinator : IDisposable
{
    /// <summary>
    /// Claims or detects the primary instance. Call once; a second call fails with
    /// <see cref="ErrorCode.InvalidInput"/>. Synchronous and fast.
    /// </summary>
    Result<InstanceRole> Start();

    /// <summary>
    /// Asks the primary instance to activate itself. Only valid for <see cref="InstanceRole.Secondary"/>
    /// (otherwise <see cref="ErrorCode.InvalidInput"/>); waits a bounded time for the primary.
    /// </summary>
    Task<Result> RequestPrimaryActivationAsync(CancellationToken ct);

    /// <summary>
    /// Raised on a thread-pool thread, only for the primary, once per valid request. Handlers must marshal to
    /// the Dispatcher and must not throw; a throwing handler is isolated and does not stop the listener.
    /// </summary>
    event Action? ActivationRequested;
}
