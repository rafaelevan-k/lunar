using Lunar.Application.Abstractions.System;
using Lunar.Domain.Shared;

namespace Lunar.Application.System.UseCases;

/// <summary>
/// Writes brightness while a slider is dragged (ADR-0021): the first value goes out at once, then at most one write per
/// <see cref="Interval"/>, and the last value always arrives (latest wins). A request replaced by a newer one before it was written completes
/// as soon as the newer one does, with the newer one's result, so every caller learns what finally happened. Thread-safe; the writes run off the
/// calling thread.
/// </summary>
public sealed class BrightnessWriter : IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(100);

    private readonly IBrightnessService _service;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Pending? _pending;
    private bool _pumping;
    private long _lastWriteTicks = long.MinValue;
    private bool _disposed;

    public BrightnessWriter(IBrightnessService service, TimeProvider? time = null, TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
        _time = time ?? TimeProvider.System;
        Interval = interval ?? DefaultInterval;
    }

    public TimeSpan Interval { get; }

    /// <summary>Asks for <paramref name="percent"/> on the display; completes with the result of the write that carried it (or replaced it).</summary>
    public Task<Result> RequestAsync(string? displayId, int percent, CancellationToken ct = default)
    {
        if (percent is < 0 or > 100)
            return Task.FromResult(Result.Failure(new OperationError(ErrorCode.InvalidInput, "Brightness must be between 0 and 100.")));

        var waiter = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => waiter.TrySetResult(Result.Cancelled()));
        bool start;
        lock (_gate)
        {
            if (_disposed)
                return Task.FromResult(Result.Cancelled());
            var waiters = _pending?.Waiters ?? [];
            waiters.Add(waiter);
            _pending = new Pending(displayId, percent, waiters);
            start = !_pumping;
            _pumping = true;
        }

        if (start)
            _ = Task.Run(PumpAsync);
        return waiter.Task;
    }

    public void Dispose()
    {
        List<TaskCompletionSource<Result>> abandoned;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            abandoned = _pending?.Waiters ?? [];
            _pending = null;
        }

        _lifetime.Cancel();
        foreach (var waiter in abandoned)
            waiter.TrySetResult(Result.Cancelled());
    }

    // Never faults: every waiter gets a result, and an unexpected exception becomes an Unexpected failure for the waiters of that write.
    private async Task PumpAsync()
    {
        while (true)
        {
            Pending next;
            TimeSpan wait;
            lock (_gate)
            {
                if (_pending is null || _disposed)
                {
                    _pumping = false;
                    return;
                }

                next = _pending;
                _pending = null;
                var since = _lastWriteTicks == long.MinValue ? TimeSpan.MaxValue : _time.GetElapsedTime(_lastWriteTicks);
                wait = since >= Interval ? TimeSpan.Zero : Interval - since;
            }

            Result result;
            try
            {
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, _time, _lifetime.Token).ConfigureAwait(false);
                    lock (_gate)
                    {
                        // A newer value arrived while waiting: it replaces this one and carries its waiters.
                        if (_pending is not null)
                        {
                            _pending.Waiters.AddRange(next.Waiters);
                            continue;
                        }
                    }
                }

                lock (_gate)
                    _lastWriteTicks = _time.GetTimestamp();
                result = await _service.SetAsync(next.DisplayId, next.Percent, _lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = Result.Cancelled();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                result = Result.Failure(new OperationError(ErrorCode.Unexpected, "The brightness could not be changed."));
            }

            foreach (var waiter in next.Waiters)
                waiter.TrySetResult(result);
        }
    }

    private sealed record Pending(string? DisplayId, int Percent, List<TaskCompletionSource<Result>> Waiters);
}
