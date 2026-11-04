using System.Collections.Concurrent;

namespace Lunar.Windows.Media;

/// <summary>
/// One dedicated background thread that every call into Windows' media-session objects runs on, including the code that
/// resumes after each <c>await</c> (the thread has its own synchronization context). The session objects belong to the
/// thread that first used them: touching them from another thread fails with "the application called an interface that was
/// marshalled for a different thread" (0x8001010E), which made a second read lose every session until this worker existed.
/// The thread is a background thread; after <see cref="Complete"/> it runs the cleanup once the calls already started have
/// finished, and exits.
/// </summary>
internal sealed class GsmtcWorker
{
    private readonly BlockingCollection<Action> _queue = [];
    private readonly Thread _thread;
    private readonly WorkerScheduler _scheduler;
    private readonly object _lock = new();
    private Action? _cleanup;
    private int _outstanding;
    private bool _completing;

    public GsmtcWorker()
    {
        _scheduler = new WorkerScheduler(this);
        _thread = new Thread(Run) { IsBackground = true, Name = "Lunar.MediaSessions" };
        _thread.Start();
    }

    /// <summary>Runs <paramref name="work"/> on the worker thread; everything it awaits resumes there too.</summary>
    public Task<T> RunAsync<T>(Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_lock)
        {
            if (_completing)
                throw new ObjectDisposedException(nameof(GsmtcWorker));
            _outstanding++;
        }

        var task = Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.None, _scheduler).Unwrap();
        // Counted from the pool, not the worker: the worker may be the one that is about to finish.
        _ = task.ContinueWith(_ => WorkEnded(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    /// <summary>
    /// Runs <paramref name="cleanup"/> on the worker thread once every call that was already started has finished (including
    /// what it is still awaiting), then lets the thread end. New calls are refused from now on. Does not wait.
    /// </summary>
    public void Complete(Action cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        lock (_lock)
        {
            if (_completing)
                return;
            _completing = true;
            _cleanup = cleanup;
            if (_outstanding > 0)
                return; // the last call to end runs it
        }

        Finish();
    }

    private void WorkEnded()
    {
        lock (_lock)
        {
            _outstanding--;
            if (!_completing || _outstanding > 0)
                return;
        }

        Finish();
    }

    // Queues the cleanup, then closes the queue so the thread ends after running it.
    private void Finish()
    {
        Action? cleanup;
        lock (_lock)
        {
            cleanup = _cleanup;
            _cleanup = null;
            if (cleanup is null)
                return;
        }

        try
        {
            _queue.Add(cleanup);
            _queue.CompleteAdding();
        }
        catch (InvalidOperationException)
        {
            // Already closed.
        }
    }

    private void Post(Action action)
    {
        try
        {
            _queue.Add(action);
        }
        catch (InvalidOperationException)
        {
            // The worker has finished: a late continuation has nowhere to run and is dropped.
        }
    }

    private void Run()
    {
        SynchronizationContext.SetSynchronizationContext(new WorkerContext(this));
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch (Exception)
            {
                // A task body reports its failure through its Task; nothing may stop the worker.
            }
        }
    }

    private sealed class WorkerContext(GsmtcWorker owner) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => owner.Post(() => d(state));

        public override void Send(SendOrPostCallback d, object? state) => d(state); // only used from the worker thread itself

        public override SynchronizationContext CreateCopy() => new WorkerContext(owner);
    }

    private sealed class WorkerScheduler(GsmtcWorker owner) : TaskScheduler
    {
        protected override void QueueTask(Task task) => owner.Post(() => TryExecuteTask(task));

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) =>
            System.Environment.CurrentManagedThreadId == owner._thread.ManagedThreadId && TryExecuteTask(task);

        protected override IEnumerable<Task>? GetScheduledTasks() => null;
    }
}
