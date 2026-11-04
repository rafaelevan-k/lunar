using Lunar.Application.Abstractions.System;
using Lunar.Application.Reactive;
using Lunar.Application.System.Models;
using Lunar.Domain.Shared;

namespace Lunar.Application.System.UseCases;

/// <summary>
/// The volume use cases (BE-11): set the volume, set or toggle mute, and a stream of snapshots, on top of
/// <see cref="IVolumeService"/>. It exists so a slider that fires dozens of changes per second, and Windows reporting each
/// of them back, cannot turn into a feedback loop.
///
/// Rules:
/// - Levels are clamped to 0..1 (infinities become the bound); NaN is <see cref="ErrorCode.InvalidInput"/>.
/// - Coalescing: at most one write is in flight. Requests that arrive meanwhile replace each other, so only the newest
///   level (and the newest mute) is sent next. A request that was replaced completes as success (a newer request took
///   over); the one that is sent completes with the outcome of the write.
/// - Echo: each write registers the state it expects back before it is sent (Windows can report before the call
///   returns). A report that matches (within <see cref="VolumeControllerOptions.EchoTolerance"/> and inside the echo
///   window) is delivered as <see cref="VolumeChangeSource.Self"/>, anything else as <see cref="VolumeChangeSource.External"/>.
///   The controller never writes in response to a report, so there is no loop; a UI ignores Self while the user is dragging.
/// - A failed write is followed by an authoritative read, delivered as <see cref="VolumeChangeSource.Corrected"/>, so an
///   optimistic UI can roll back. The failure is returned to the caller whose write failed.
/// - Mute toggles flip the latest requested value, so two quick toggles end where they started.
/// - Cancelling a request that has not been sent yet removes it; one already sent cannot be taken back.
/// Disposal cancels what is waiting, unsubscribes from the service and completes observers; it does not dispose the
/// service, which the container owns. Thread-safe; snapshots are delivered serialized on an arbitrary thread.
/// </summary>
public sealed class VolumeController : IAsyncDisposable
{
    private readonly IVolumeService _service;
    private readonly IClock _clock;
    private readonly VolumeControllerOptions _options;
    private readonly StateBroadcaster<VolumeSnapshot> _snapshots = new();
    private readonly object _lock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Expectation> _expectations = [];

    private IDisposable? _subscription;
    private VolumeState? _current;
    private bool _disposed;

    private PendingWrite<float>? _pendingVolume;
    private PendingWrite<bool>? _pendingMute;
    private bool? _desiredMute;
    private Task _drain = Task.CompletedTask;
    private bool _draining;

    public VolumeController(IVolumeService service, IClock clock, VolumeControllerOptions? options = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? VolumeControllerOptions.Default;
        _options.Validate();
    }

    /// <summary>The latest known state, or null before the first reading.</summary>
    public VolumeState? Current
    {
        get
        {
            lock (_lock)
                return _current;
        }
    }

    /// <summary>Snapshots after <see cref="InitializeAsync"/>. No replay: read <see cref="Current"/> for the starting value.</summary>
    public IObservable<VolumeSnapshot> Snapshots => _snapshots;

    /// <summary>
    /// Starts listening to the service and reads the first state, which is also delivered as an
    /// <see cref="VolumeChangeSource.Initial"/> snapshot. Calling it again returns the latest known state.
    /// </summary>
    public async Task<Result<VolumeState>> InitializeAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Result.Cancelled<VolumeState>();

        lock (_lock)
        {
            if (_disposed)
                return Result.Failure<VolumeState>(Closed);
            _subscription ??= _service.Changes.Subscribe(new Listener(this)); // before the read, so no change is missed
        }

        var read = await _service.GetAsync(ct).ConfigureAwait(false);
        if (read.IsFailure)
            return read;

        lock (_lock)
        {
            if (_disposed)
                return Result.Failure<VolumeState>(Closed);
            _current = read.Value;
        }

        _snapshots.Publish(new VolumeSnapshot(read.Value, VolumeChangeSource.Initial));
        return read;
    }

    /// <summary>Sets the level (0..1, clamped). Coalesced: see the class remarks.</summary>
    public Task<Result> SetVolumeAsync(float scalar, CancellationToken ct)
    {
        if (float.IsNaN(scalar))
            return Task.FromResult(Result.Failure(new OperationError(ErrorCode.InvalidInput, "Volume must be a number.")));
        return EnqueueVolume(Math.Clamp(scalar, 0f, 1f), ct);
    }

    /// <summary>Sets mute on or off. Coalesced like the level.</summary>
    public Task<Result> SetMuteAsync(bool muted, CancellationToken ct) => EnqueueMute(muted, ct);

    /// <summary>
    /// Flips mute relative to the latest requested value, so two quick toggles cancel out. Reads the state first when
    /// none is known yet.
    /// </summary>
    public async Task<Result> ToggleMuteAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Result.Cancelled();

        bool? known;
        lock (_lock)
            known = _desiredMute ?? _current?.IsMuted;

        if (known is null)
        {
            var read = await InitializeAsync(ct).ConfigureAwait(false);
            if (read.IsFailure)
                return read.IsCancelled ? Result.Cancelled() : Result.Failure(read.Error!);
        }

        // Decide and enqueue under one lock so two concurrent toggles cannot both flip from the same value.
        Task<Result> queued;
        lock (_lock)
        {
            if (_disposed)
                return Result.Failure(Closed);
            var next = !(_desiredMute ?? _current!.IsMuted);
            queued = EnqueueMuteLocked(next, ct);
        }

        return await queued.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Task drain;
        IDisposable? subscription;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            subscription = _subscription;
            _subscription = null;
            drain = _drain;
            _pendingVolume?.Complete(Result.Cancelled());
            _pendingMute?.Complete(Result.Cancelled());
            _pendingVolume = null;
            _pendingMute = null;
        }

        subscription?.Dispose();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await drain.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The write that was running was cancelled by disposal.
        }

        _snapshots.Complete();
        _lifetime.Dispose();
    }

    private static OperationError Closed => new(ErrorCode.DeviceUnavailable, "The volume controller is closed.");

    // ---- requests ---------------------------------------------------------------------------------------------------

    private Task<Result> EnqueueVolume(float scalar, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Task.FromResult(Result.Cancelled());

        lock (_lock)
        {
            if (_disposed)
                return Task.FromResult(Result.Failure(Closed));
            return EnqueueVolumeLocked(scalar, ct);
        }
    }

    private Task<Result> EnqueueMute(bool muted, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Task.FromResult(Result.Cancelled());

        lock (_lock)
        {
            if (_disposed)
                return Task.FromResult(Result.Failure(Closed));
            return EnqueueMuteLocked(muted, ct);
        }
    }

    // The Enqueue*Locked methods are called holding _lock, after checking that the controller is open.
    private Task<Result> EnqueueVolumeLocked(float scalar, CancellationToken ct)
    {
        var previous = _pendingVolume;
        var pending = new PendingWrite<float>(scalar);
        _pendingVolume = pending;
        previous?.Complete(Result.Success()); // replaced by a newer level
        Watch(pending, ct, () => ReferenceEquals(_pendingVolume, pending), () => _pendingVolume = null);
        StartDrainLocked();
        return pending.Task;
    }

    private Task<Result> EnqueueMuteLocked(bool muted, CancellationToken ct)
    {
        var previous = _pendingMute;
        var pending = new PendingWrite<bool>(muted);
        _pendingMute = pending;
        _desiredMute = muted;
        previous?.Complete(Result.Success());
        Watch(pending, ct, () => ReferenceEquals(_pendingMute, pending), () =>
        {
            _pendingMute = null;
            _desiredMute = null;
        });
        StartDrainLocked();
        return pending.Task;
    }

    // Cancelling a request that is still waiting removes it; once it has been taken for sending it can no longer be cancelled.
    private void Watch<T>(PendingWrite<T> pending, CancellationToken ct, Func<bool> stillQueued, Action remove)
    {
        if (!ct.CanBeCanceled)
            return;

        var registration = ct.Register(() =>
        {
            lock (_lock)
            {
                if (!stillQueued())
                    return;
                remove();
            }

            pending.Complete(Result.Cancelled());
        });
        pending.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
    }

    private void StartDrainLocked()
    {
        if (_draining)
            return;
        _draining = true;
        _drain = DrainAsync();
    }

    // The single writer: sends the newest level, then the newest mute, until nothing is waiting.
    private async Task DrainAsync()
    {
        await Task.Yield(); // never hold the caller's thread or _lock while talking to the service
        while (true)
        {
            PendingWrite<float>? volume;
            PendingWrite<bool>? mute;
            lock (_lock)
            {
                volume = _pendingVolume;
                mute = _pendingMute;
                _pendingVolume = null;
                _pendingMute = null;
                if (volume is null && mute is null)
                {
                    _draining = false;
                    _desiredMute = null;
                    return;
                }
            }

            if (volume is not null)
                await SendAsync(volume, expected: new Expectation(volume.Value, null, _clock.LocalNow), s => s.SetVolumeAsync(volume.Value, _lifetime.Token)).ConfigureAwait(false);
            if (mute is not null)
                await SendAsync(mute, expected: new Expectation(null, mute.Value, _clock.LocalNow), s => s.SetMuteAsync(mute.Value, _lifetime.Token)).ConfigureAwait(false);
        }
    }

    private async Task SendAsync<T>(PendingWrite<T> write, Expectation expected, Func<IVolumeService, Task<Result>> send)
    {
        lock (_lock)
        {
            PurgeExpired();
            _expectations.Add(expected); // before the call: Windows can report before the call returns
        }

        Result result;
        try
        {
            result = await send(_service).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = Result.Cancelled();
        }

        if (result.IsSuccess)
        {
            write.Complete(result);
            return;
        }

        lock (_lock)
            _expectations.Remove(expected);

        // The correction is published before the failure is returned, so a caller that reacts to the error always finds
        // the Corrected snapshot already delivered.
        try
        {
            if (!result.IsCancelled)
                await RollbackAsync().ConfigureAwait(false);
        }
        finally
        {
            write.Complete(result);
        }
    }

    // After a failed write: read what Windows really has and tell the UI, so it can undo its optimistic change.
    private async Task RollbackAsync()
    {
        try
        {
            var read = await _service.GetAsync(_lifetime.Token).ConfigureAwait(false);
            if (read.IsFailure)
                return;

            lock (_lock)
            {
                if (_disposed)
                    return;
                _current = read.Value;
                _expectations.Clear();
            }

            _snapshots.Publish(new VolumeSnapshot(read.Value, VolumeChangeSource.Corrected));
        }
        catch (OperationCanceledException)
        {
            // Disposed while correcting: nobody is listening any more.
        }
    }

    // ---- reports from the service ---------------------------------------------------------------------------------

    private void OnState(VolumeState state)
    {
        VolumeChangeSource source;
        lock (_lock)
        {
            if (_disposed)
                return;
            PurgeExpired();
            var match = _expectations.FindIndex(e => e.Matches(state, _options.EchoTolerance));
            if (match >= 0)
            {
                _expectations.RemoveAt(match);
                source = VolumeChangeSource.Self;
            }
            else
            {
                source = VolumeChangeSource.External;
            }

            _current = state;
        }

        _snapshots.Publish(new VolumeSnapshot(state, source));
    }

    // Caller holds _lock.
    private void PurgeExpired()
    {
        var now = _clock.LocalNow;
        _expectations.RemoveAll(e => now - e.At > _options.EchoWindow);
    }

    private sealed record Expectation(float? Scalar, bool? Muted, DateTimeOffset At)
    {
        public bool Matches(VolumeState state, float tolerance) =>
            (Scalar is null || Math.Abs(state.Scalar - Scalar.Value) <= tolerance)
            && (Muted is null || state.IsMuted == Muted.Value);
    }

    private sealed class PendingWrite<T>(T value)
    {
        private readonly TaskCompletionSource<Result> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public T Value { get; } = value;

        public Task<Result> Task => _done.Task;

        public void Complete(Result result) => _done.TrySetResult(result);
    }

    private sealed class Listener(VolumeController owner) : IObserver<VolumeState>
    {
        public void OnNext(VolumeState value) => owner.OnState(value);

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }
}
