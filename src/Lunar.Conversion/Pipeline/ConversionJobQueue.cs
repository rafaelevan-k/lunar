using System.Threading.Channels;
using Lunar.Application.Abstractions.Conversion;
using Lunar.Application.Conversion.Models;
using Lunar.Application.Reactive;
using Lunar.Domain.Shared;

namespace Lunar.Conversion.Pipeline;

/// <summary>
/// The background queue of conversions (BE-21). Callers never wait: <see cref="Enqueue"/> only records the jobs and a small fixed set
/// of worker tasks, never more than <see cref="ConversionQueueOptions.MaxConcurrency"/>, take them in order and run the
/// <see cref="IConversionJobExecutor"/> on the thread pool, so a UI thread is never blocked and never does conversion work.
///
/// What the queue guarantees:
/// - A job cancelled while waiting never starts. A job cancelled while running gets its token cancelled and shows Cancelling; it is
///   final only after the executor has returned, so the executor's cleanup has finished by then. A job never ends Succeeded with
///   a half-written result: success needs the executor to say so.
/// - Each running job has its own time limit. A job over it is stopped and ends Failed with a retryable timeout, which is told
///   apart from a cancel the user asked for.
/// - A job that throws ends Failed with a fixed message; the exception text, which may hold a path, is never kept or shown.
/// - Progress is only what the executor reports, kept honest: a total below 1 means unknown, a count is held between 0 and the total
///   and never goes backwards. Nothing is invented.
/// - The queue remembers a bounded number of final jobs and refuses new ones past its limit (all or none).
/// - Disposing cancels everything, waits for the running jobs to stop and completes the observers.
/// A job whose executor ignores cancellation keeps its place and shows Cancelling until it returns; the queue does not abandon it,
/// because an abandoned job could still write files. Thread-safe.
/// </summary>
public sealed class ConversionJobQueue : IConversionQueue
{
    private readonly IConversionJobExecutor _executor;
    private readonly ConversionQueueOptions _options;
    private readonly TimeProvider _time;
    private readonly StateBroadcaster<ConversionJobSnapshot> _changes = new();
    private readonly Channel<Entry> _channel = Channel.CreateUnbounded<Entry>();
    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];
    private readonly Dictionary<int, Entry> _byId = [];
    private readonly List<Task> _workers = [];
    private readonly List<TaskCompletionSource> _idleWaiters = [];

    private int _nextId = 1;
    private int _active;
    private bool _disposed;
    private Task? _disposeTask;

    public ConversionJobQueue(IConversionJobExecutor executor, ConversionQueueOptions? options = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(executor);
        _executor = executor;
        _options = (options ?? new ConversionQueueOptions()).Validated();
        _time = time ?? TimeProvider.System;
    }

    public IObservable<ConversionJobSnapshot> Changes => _changes;

    public IReadOnlyList<ConversionJobSnapshot> Jobs
    {
        get
        {
            lock (_gate)
                return [.. _entries.Select(e => e.ToSnapshot())];
        }
    }

    // ---- submitting --------------------------------------------------------------------------------------------------

    public Result<IReadOnlyList<int>> Enqueue(IReadOnlyList<PlannedConversion> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        foreach (var job in jobs)
            ArgumentNullException.ThrowIfNull(job);
        if (jobs.Count == 0)
            return Result.Failure<IReadOnlyList<int>>(new OperationError(ErrorCode.InvalidInput, "There is nothing to convert."));

        lock (_gate)
        {
            if (_disposed)
                return Result.Failure<IReadOnlyList<int>>(Closed);
            if (_active + jobs.Count > _options.MaxQueued)
                return Result.Failure<IReadOnlyList<int>>(Full);

            var ids = new List<int>(jobs.Count);
            foreach (var job in jobs)
                ids.Add(AddLocked(job).Id);
            return Result.Success<IReadOnlyList<int>>(ids);
        }
    }

    public Result<int> Retry(int jobId)
    {
        lock (_gate)
        {
            if (_disposed)
                return Result.Failure<int>(Closed);
            if (!_byId.TryGetValue(jobId, out var entry))
                return Result.Failure<int>(Unknown);
            if (entry.State is not (ConversionJobState.Failed or ConversionJobState.Cancelled))
                return Result.Failure<int>(new OperationError(ErrorCode.InvalidInput, "Only a conversion that failed or was cancelled can be tried again."));
            if (_active + 1 > _options.MaxQueued)
                return Result.Failure<int>(Full);

            return Result.Success(AddLocked(entry.Job).Id);
        }
    }

    // Callers hold the gate. Records the job, tells the observers, hands it to a worker.
    private Entry AddLocked(PlannedConversion job)
    {
        var entry = new Entry(_nextId++, job);
        _entries.Add(entry);
        _byId[entry.Id] = entry;
        _active++;
        EnsureWorkersLocked();
        Publish(entry);
        _channel.Writer.TryWrite(entry);
        return entry;
    }

    private void EnsureWorkersLocked()
    {
        if (_workers.Count > 0)
            return;
        for (var i = 0; i < _options.MaxConcurrency; i++)
            _workers.Add(Task.Run(WorkerLoopAsync));
    }

    // ---- cancelling --------------------------------------------------------------------------------------------------

    public Result Cancel(int jobId)
    {
        Entry entry;
        var stopRunning = false;
        lock (_gate)
        {
            if (!_byId.TryGetValue(jobId, out entry!))
                return Result.Failure(Unknown);

            switch (entry.State)
            {
                case ConversionJobState.Queued:
                    // Never started: it is simply over. The worker that later takes it from the line sees this and skips it.
                    FinishLocked(entry, ConversionJobState.Cancelled, error: null, outputs: []);
                    break;
                case ConversionJobState.Running:
                    entry.State = ConversionJobState.Cancelling;
                    Publish(entry);
                    stopRunning = true;
                    break;
            }
        }

        // Outside the gate: cancelling runs the executor's registered callbacks on this thread.
        if (stopRunning)
            entry.CancelSource.Cancel();
        return Result.Success();
    }

    public void CancelAll()
    {
        int[] ids;
        lock (_gate)
            ids = [.. _entries.Where(e => !e.IsFinal).Select(e => e.Id)];
        foreach (var id in ids)
            Cancel(id);
    }

    public int ClearFinished()
    {
        lock (_gate)
        {
            var finished = _entries.Where(e => e.IsFinal).ToList();
            foreach (var entry in finished)
            {
                _entries.Remove(entry);
                _byId.Remove(entry.Id);
            }

            return finished.Count;
        }
    }

    public Task WhenIdleAsync(CancellationToken ct)
    {
        TaskCompletionSource waiter;
        lock (_gate)
        {
            if (_active == 0)
                return Task.CompletedTask;
            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _idleWaiters.Add(waiter);
        }

        return waiter.Task.WaitAsync(ct);
    }

    // ---- running -----------------------------------------------------------------------------------------------------

    private async Task WorkerLoopAsync()
    {
        // RunAsync handles every failure of a job, so nothing can escape the loop and strand the queue.
        await foreach (var entry in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
            await RunAsync(entry).ConfigureAwait(false);
    }

    private async Task RunAsync(Entry entry)
    {
        lock (_gate)
        {
            if (entry.State != ConversionJobState.Queued)
                return; // cancelled while it waited
            entry.State = ConversionJobState.Running;
            Publish(entry);
        }

        Result<IReadOnlyList<string>> result;
        var timedOut = false;
        using var timeout = new CancellationTokenSource(_options.JobTimeout, _time);
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(entry.CancelSource.Token, timeout.Token))
        {
            try
            {
                result = await _executor.ExecuteAsync(entry.Job, new Reporter(this, entry), linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = Result.Cancelled<IReadOnlyList<string>>();
            }
            catch (Exception)
            {
                // The text of an exception may carry a path or content; only the fact is kept.
                result = Result.Failure<IReadOnlyList<string>>(new OperationError(ErrorCode.Unexpected, "The conversion stopped unexpectedly.", retryable: true));
            }

            timedOut = timeout.IsCancellationRequested && !entry.CancelSource.IsCancellationRequested;
        }

        lock (_gate)
        {
            if (result.IsSuccess)
            {
                FinishLocked(entry, ConversionJobState.Succeeded, error: null, outputs: [.. result.Value]);
            }
            else if (result.IsCancelled)
            {
                if (timedOut)
                {
                    FinishLocked(entry, ConversionJobState.Failed,
                        new OperationError(ErrorCode.Timeout, "This conversion took too long and was stopped.", retryable: true), outputs: []);
                }
                else
                {
                    FinishLocked(entry, ConversionJobState.Cancelled, error: null, outputs: []);
                }
            }
            else
            {
                FinishLocked(entry, ConversionJobState.Failed, result.Error, outputs: []);
            }
        }
    }

    // Callers hold the gate.
    private void FinishLocked(Entry entry, ConversionJobState state, OperationError? error, IReadOnlyList<string> outputs)
    {
        entry.State = state;
        entry.Error = error;
        entry.Outputs = outputs;
        _active--;
        Publish(entry);
        TrimFinishedLocked();
        if (_active == 0)
        {
            foreach (var waiter in _idleWaiters)
                waiter.TrySetResult();
            _idleWaiters.Clear();
        }
    }

    private void TrimFinishedLocked()
    {
        var excess = _entries.Count(e => e.IsFinal) - _options.MaxRetainedFinished;
        if (excess <= 0)
            return;

        foreach (var old in _entries.Where(e => e.IsFinal).Take(excess).ToList())
        {
            _entries.Remove(old);
            _byId.Remove(old.Id);
        }
    }

    private void Publish(Entry entry) => _changes.Publish(entry.ToSnapshot());

    // ---- progress ----------------------------------------------------------------------------------------------------

    private void OnProgress(Entry entry, JobProgress? reported)
    {
        // A report that is nothing, or a count below zero, is nonsense: keep what was known.
        if (reported is null || reported.PagesCompleted < 0)
            return;

        // A total below one is "unknown"; a count is held between zero and the total.
        JobProgress next;
        if (reported.PagesTotal is not > 0)
        {
            next = reported.PagesCompleted is null ? JobProgress.Unknown : new JobProgress(reported.PagesCompleted, null);
        }
        else
        {
            int? completed = reported.PagesCompleted is null ? null : Math.Min(reported.PagesCompleted.Value, reported.PagesTotal.Value);
            next = new JobProgress(completed, reported.PagesTotal);
        }

        lock (_gate)
        {
            if (entry.State is not (ConversionJobState.Running or ConversionJobState.Cancelling))
                return;

            // Progress never goes backwards while the total stays the same.
            if (entry.Progress.PagesTotal == next.PagesTotal && next.PagesCompleted < entry.Progress.PagesCompleted)
                return;
            if (next == entry.Progress)
                return;

            entry.Progress = next;
            Publish(entry);
        }
    }

    // ---- closing -----------------------------------------------------------------------------------------------------

    public ValueTask DisposeAsync()
    {
        lock (_gate)
            _disposeTask ??= DisposeCoreAsync();
        return new ValueTask(_disposeTask);
    }

    private async Task DisposeCoreAsync()
    {
        List<Entry> running = [];
        Task[] workers;
        lock (_gate)
        {
            _disposed = true;
            foreach (var entry in _entries.Where(e => !e.IsFinal).ToList())
            {
                if (entry.State == ConversionJobState.Queued)
                {
                    FinishLocked(entry, ConversionJobState.Cancelled, error: null, outputs: []);
                }
                else
                {
                    if (entry.State == ConversionJobState.Running)
                    {
                        entry.State = ConversionJobState.Cancelling;
                        Publish(entry);
                    }

                    running.Add(entry);
                }
            }

            _channel.Writer.TryComplete();
            workers = [.. _workers];
        }

        foreach (var entry in running)
            entry.CancelSource.Cancel();

        await Task.WhenAll(workers).ConfigureAwait(false);
        _changes.Complete();
    }

    private static OperationError Closed => new(ErrorCode.DeviceUnavailable, "The conversion queue is closed.");

    private static OperationError Full => new(ErrorCode.InvalidInput, "The conversion queue is full. Wait for some conversions to finish and try again.", retryable: true);

    private static OperationError Unknown => new(ErrorCode.InvalidInput, "That conversion is not in the list.");

    // ---- pieces ------------------------------------------------------------------------------------------------------

    private sealed class Entry(int id, PlannedConversion job)
    {
        public int Id { get; } = id;

        public PlannedConversion Job { get; } = job;

        public ConversionJobState State { get; set; } = ConversionJobState.Queued;

        public JobProgress Progress { get; set; } = JobProgress.Unknown;

        public OperationError? Error { get; set; }

        public IReadOnlyList<string> Outputs { get; set; } = [];

        // Not disposed on purpose: it holds no timer or handle, and a late Cancel must never meet a disposed source.
        public CancellationTokenSource CancelSource { get; } = new();

        public bool IsFinal => State is ConversionJobState.Succeeded or ConversionJobState.Failed or ConversionJobState.Cancelled;

        public ConversionJobSnapshot ToSnapshot() => new(Id, Job, State, Progress, Error, Outputs);
    }

    private sealed class Reporter(ConversionJobQueue owner, Entry entry) : IProgress<JobProgress>
    {
        public void Report(JobProgress value) => owner.OnProgress(entry, value);
    }
}
