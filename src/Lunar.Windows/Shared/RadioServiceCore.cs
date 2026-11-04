using Lunar.Application.Reactive;
using Lunar.Domain.Shared;

namespace Lunar.Windows.Shared;

/// <summary>
/// What every radio-state service of the Windows layer (Wi-Fi, Bluetooth) needs and must get the same way (ADR-0006):
/// one operation at a time behind a gate that honours cancellation, states published to observers without repeats,
/// bursts of OS notifications coalesced into one refresh, and a disposal that waits for a call already talking to
/// Windows, completes the observers and is idempotent. The services own the Windows calls and the mapping to a state;
/// this class owns none of that.
/// </summary>
internal sealed class RadioServiceCore<TState>
    where TState : class
{
    private readonly Func<Task> _refresh;
    private readonly Action _detach;
    private readonly Action _release;
    private readonly StateBroadcaster<TState> _changes = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly CancellationTokenSource _lifetime = new();

    private TState? _last;
    private bool _disposed;
    private int _refreshRequested;
    private int _refreshRunning;

    /// <param name="refresh">Reads the state and publishes it. Called holding the gate.</param>
    /// <param name="detach">Stops receiving notifications from Windows. Called first on disposal, without the gate.</param>
    /// <param name="release">Closes the Windows handles. Called holding the gate, once.</param>
    public RadioServiceCore(Func<Task> refresh, Action detach, Action release)
    {
        _refresh = refresh;
        _detach = detach;
        _release = release;
    }

    public IObservable<TState> Changes => _changes;

    public TState? Last
    {
        get
        {
            lock (_stateLock)
                return _last;
        }
    }

    public bool IsDisposed
    {
        get
        {
            lock (_stateLock)
                return _disposed;
        }
    }

    /// <summary>Waits for the gate. False means the caller should stop (cancelled, or the service is closing).</summary>
    public async Task<bool> EnterAsync(CancellationToken ct)
    {
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public void Exit() => _gate.Release();

    /// <summary>Delivers a state unless it equals the last one delivered, so observers see changes, not repeats.</summary>
    public void Publish(TState state)
    {
        lock (_stateLock)
        {
            if (_disposed || Equals(_last, state))
                return;
            _last = state;
        }

        _changes.Publish(state);
    }

    /// <summary>
    /// For the notification callback of Windows, which may run on any thread and must not call back into Windows: only asks
    /// for a refresh. One refresh runs at a time, and anything that arrives meanwhile asks for exactly one more.
    /// </summary>
    public void RequestRefresh()
    {
        if (IsDisposed)
            return;

        Interlocked.Exchange(ref _refreshRequested, 1);
        if (Interlocked.CompareExchange(ref _refreshRunning, 1, 0) != 0)
            return;

        _ = Task.Run(RefreshLoopAsync);
    }

    /// <summary>
    /// After Windows accepted a radio write: success is reported only once <paramref name="reached"/> says the radio is
    /// in the requested state, checked a bounded number of times. Not reaching it is <see cref="ErrorCode.Timeout"/>;
    /// cancelling stops the wait (the write may already have taken effect, the next read says).
    /// </summary>
    public static async Task<Result> ConfirmAsync(Func<Task<bool>> reached, RadioConfirmOptions options, string timeoutMessage, CancellationToken ct)
    {
        for (var attempt = 0; attempt < options.ConfirmAttempts; attempt++)
        {
            if (await reached().ConfigureAwait(false))
                return Result.Success();
            if (ct.IsCancellationRequested)
                return Result.Cancelled();

            try
            {
                await options.Delay(options.ConfirmInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Result.Cancelled();
            }
        }

        return Result.Failure(new OperationError(ErrorCode.Timeout, timeoutMessage, retryable: true));
    }

    public async ValueTask DisposeAsync()
    {
        if (!BeginDispose())
            return;

        await _lifetime.CancelAsync().ConfigureAwait(false);

        // Wait for a call that is already talking to Windows, then close under the same gate.
        await _gate.WaitAsync().ConfigureAwait(false);
        FinishDispose();
    }

    public void Dispose()
    {
        if (!BeginDispose())
            return;

        _lifetime.Cancel();
        _gate.Wait();
        FinishDispose();
    }

    private bool BeginDispose()
    {
        lock (_stateLock)
        {
            if (_disposed)
                return false;
            _disposed = true;
        }

        _detach();
        return true;
    }

    private void FinishDispose()
    {
        try
        {
            _release();
        }
        finally
        {
            _gate.Release();
        }

        _changes.Complete();
        _lifetime.Dispose();
    }

    private async Task RefreshLoopAsync()
    {
        try
        {
            while (Interlocked.Exchange(ref _refreshRequested, 0) == 1 && !IsDisposed)
            {
                try
                {
                    await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                try
                {
                    if (IsDisposed)
                        return;
                    await _refresh().ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Disposal won the race for the gate: nothing left to refresh.
        }
        finally
        {
            Interlocked.Exchange(ref _refreshRunning, 0);
            // A request that slipped in after the loop's last check starts another run.
            if (Volatile.Read(ref _refreshRequested) == 1 && !IsDisposed)
                RequestRefresh();
        }
    }
}
