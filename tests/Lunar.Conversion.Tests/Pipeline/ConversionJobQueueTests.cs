using Lunar.Application.Conversion.Models;
using Lunar.Conversion.Pipeline;
using Lunar.Conversion.Tests.Support;
using Lunar.Domain.Conversion;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Conversion.Tests.Pipeline;

// BE-21: the queue's promises, against scripted executors. The real work of a job (BE-22 to BE-27) is not here: these tests
// prove how the queue paces, cancels, times out and reports whatever an executor does.
public sealed class ConversionJobQueueTests : IAsyncLifetime
{
    private sealed class ScriptedExecutor(Func<PlannedConversion, IProgress<JobProgress>, CancellationToken, Task<Result<IReadOnlyList<string>>>> run)
        : IConversionJobExecutor
    {
        private int _running;
        private int _maxRunning;
        private int _started;

        public int Started => Volatile.Read(ref _started);

        public int MaxRunning => Volatile.Read(ref _maxRunning);

        public List<int> StartOrder { get; } = [];

        public List<int> ThreadIds { get; } = [];

        public async Task<Result<IReadOnlyList<string>>> ExecuteAsync(PlannedConversion job, IProgress<JobProgress> progress, CancellationToken ct)
        {
            lock (StartOrder)
            {
                StartOrder.Add(job.Index);
                ThreadIds.Add(Environment.CurrentManagedThreadId);
            }

            Interlocked.Increment(ref _started);
            var now = Interlocked.Increment(ref _running);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxRunning)) && Interlocked.CompareExchange(ref _maxRunning, now, seen) != seen)
            {
            }

            try
            {
                return await run(job, progress, ct);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }

    private sealed class Collector : IObserver<ConversionJobSnapshot>
    {
        public List<ConversionJobSnapshot> Seen { get; } = [];

        public bool Completed { get; private set; }

        public void OnNext(ConversionJobSnapshot value)
        {
            lock (Seen)
                Seen.Add(value);
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted() => Completed = true;

        public ConversionJobState[] StatesOf(int jobId)
        {
            lock (Seen)
                return [.. Seen.Where(s => s.JobId == jobId).Select(s => s.State)];
        }
    }

    private readonly List<ConversionJobQueue> _queues = [];
    private readonly TempFolder _folder = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var queue in _queues)
            await queue.DisposeAsync();
        _folder.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PlannedConversion Job(int index) => new(
        index, $@"C:\in\file{index}.png", FileFormat.Png, 10, DateTime.UtcNow,
        ConversionPolicy.Resolve(FileFormat.Png, FileFormat.Jpg), @"C:\out", $"file{index}", ".jpg", false, false);

    private static Result<IReadOnlyList<string>> Done(PlannedConversion job) =>
        Result.Success<IReadOnlyList<string>>([$@"C:\out\file{job.Index}.jpg"]);

    private ConversionJobQueue Create(ScriptedExecutor executor, ConversionQueueOptions? options = null)
    {
        var queue = new ConversionJobQueue(executor, options);
        _queues.Add(queue);
        return queue;
    }

    private static ScriptedExecutor Instant() => new((job, _, _) => Task.FromResult(Done(job)));

    private static ScriptedExecutor Gated(TaskCompletionSource gate) => new(async (job, _, ct) =>
    {
        await gate.Task.WaitAsync(ct);
        return Done(job);
    });

    private static async Task Until(Func<bool> condition)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < end)
            await Task.Delay(5, Ct);
        Assert.True(condition(), "The expected state was not reached in time.");
    }

    private static ConversionJobSnapshot Find(ConversionJobQueue queue, int id) => queue.Jobs.Single(j => j.JobId == id);

    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ---- running -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AJobRunsAndEndsSucceededWithItsOutputs()
    {
        var queue = Create(Instant());
        var seen = new Collector();
        using var subscription = queue.Changes.Subscribe(seen);

        var ids = queue.Enqueue([Job(0)]).Value;
        await queue.WhenIdleAsync(Ct);

        var snapshot = Find(queue, ids[0]);
        Assert.Equal(ConversionJobState.Succeeded, snapshot.State);
        Assert.True(snapshot.IsFinal);
        Assert.Equal([@"C:\out\file0.jpg"], snapshot.OutputPaths);
        Assert.Null(snapshot.Error);
        Assert.Equal([ConversionJobState.Queued, ConversionJobState.Running, ConversionJobState.Succeeded], seen.StatesOf(ids[0]));
    }

    [Fact]
    public async Task IdsAreAssignedInOrderAndJobsKeepTheirPlans()
    {
        var queue = Create(Instant());

        var ids = queue.Enqueue([Job(5), Job(6), Job(7)]).Value;
        await queue.WhenIdleAsync(Ct);

        Assert.Equal([1, 2, 3], ids);
        Assert.Equal([5, 6, 7], queue.Jobs.Select(j => j.Job.Index));
        Assert.Equal(@"C:\in\file6.png", queue.Jobs[1].Job.SourcePath);
    }

    [Fact]
    public async Task ASubmitReturnsWhileTheConverterIsStillBlocked()
    {
        // The executor blocks its thread synchronously. If the queue ran it on the caller's thread, Enqueue would hang here.
        using var release = new ManualResetEventSlim();
        var executor = new ScriptedExecutor((job, _, _) =>
        {
            release.Wait(Ct);
            return Task.FromResult(Done(job));
        });
        var queue = Create(executor);

        var started = DateTime.UtcNow;
        var result = queue.Enqueue([Job(0), Job(1)]);
        _ = queue.Jobs;
        queue.Cancel(2);
        var elapsed = DateTime.UtcNow - started;

        Assert.True(result.IsSuccess);
        Assert.True(elapsed < TimeSpan.FromSeconds(1), $"Enqueue, Jobs and Cancel took {elapsed.TotalMilliseconds} ms while a converter was blocked.");
        await Until(() => executor.Started >= 1);

        release.Set();
        await queue.WhenIdleAsync(Ct);
    }

    [Fact]
    public async Task NoMoreThanTheLimitRunAtOnceAndTheRestWaitInOrder()
    {
        var gate = NewGate();
        var executor = Gated(gate);
        var queue = Create(executor, new ConversionQueueOptions { MaxConcurrency = 2 });

        queue.Enqueue([.. Enumerable.Range(0, 6).Select(Job)]);
        await Until(() => executor.Started == 2);
        await Task.Delay(50, Ct);

        Assert.Equal(2, executor.Started);
        Assert.Equal(2, queue.Jobs.Count(j => j.State == ConversionJobState.Running));
        Assert.Equal(4, queue.Jobs.Count(j => j.State == ConversionJobState.Queued));

        gate.SetResult();
        await queue.WhenIdleAsync(Ct);

        Assert.Equal(2, executor.MaxRunning);
        Assert.All(queue.Jobs, j => Assert.Equal(ConversionJobState.Succeeded, j.State));
    }

    [Fact]
    public async Task JobsStartInTheOrderTheyWereAdded()
    {
        var executor = Instant();
        var queue = Create(executor, new ConversionQueueOptions { MaxConcurrency = 1 });

        queue.Enqueue([.. Enumerable.Range(0, 20).Select(Job)]);
        await queue.WhenIdleAsync(Ct);

        Assert.Equal(Enumerable.Range(0, 20), executor.StartOrder);
    }

    [Fact]
    public async Task ManyJobsEachRunExactlyOnce()
    {
        var counts = new int[300];
        var executor = new ScriptedExecutor(async (job, _, _) =>
        {
            Interlocked.Increment(ref counts[job.Index]);
            await Task.Yield();
            return Done(job);
        });
        var queue = Create(executor, new ConversionQueueOptions { MaxConcurrency = 4 });

        queue.Enqueue([.. Enumerable.Range(0, 300).Select(Job)]);
        await queue.WhenIdleAsync(Ct);

        Assert.All(counts, c => Assert.Equal(1, c));
        Assert.True(executor.MaxRunning <= 4);
    }

    // ---- cancelling --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AJobCancelledWhileWaitingNeverStartsAndTheOthersCarryOn()
    {
        var gate = NewGate();
        var executor = Gated(gate);
        var queue = Create(executor, new ConversionQueueOptions { MaxConcurrency = 1 });
        var ids = queue.Enqueue([Job(0), Job(1), Job(2)]).Value;
        await Until(() => executor.Started == 1);

        var cancelled = queue.Cancel(ids[1]);
        gate.SetResult();
        await queue.WhenIdleAsync(Ct);

        Assert.True(cancelled.IsSuccess);
        Assert.Equal(ConversionJobState.Cancelled, Find(queue, ids[1]).State);
        Assert.Equal([0, 2], executor.StartOrder);
        Assert.Equal(ConversionJobState.Succeeded, Find(queue, ids[2]).State);
        Assert.Empty(Find(queue, ids[1]).OutputPaths);
    }

    [Fact]
    public async Task ACancelledRunningJobIsCancellingUntilTheConverterHasStoppedAndCleanedUp()
    {
        var stopped = NewGate();
        var cleaned = false;
        var executor = new ScriptedExecutor(async (_, _, ct) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                await stopped.Task; // the converter is busy removing what it wrote
                cleaned = true;
                return Result.Cancelled<IReadOnlyList<string>>();
            }

            return Result.Failure<IReadOnlyList<string>>(new OperationError(ErrorCode.Unexpected, "unreachable"));
        });
        var queue = Create(executor);
        var seen = new Collector();
        using var subscription = queue.Changes.Subscribe(seen);
        var id = queue.Enqueue([Job(0)]).Value[0];
        await Until(() => Find(queue, id).State == ConversionJobState.Running);

        Assert.True(queue.Cancel(id).IsSuccess);
        await Until(() => Find(queue, id).State == ConversionJobState.Cancelling);
        await Task.Delay(100, Ct);

        // Still not final: the cleanup has not finished.
        Assert.Equal(ConversionJobState.Cancelling, Find(queue, id).State);
        Assert.False(Find(queue, id).IsFinal);
        Assert.False(cleaned);

        stopped.SetResult();
        await queue.WhenIdleAsync(Ct);

        Assert.True(cleaned);
        Assert.Equal(ConversionJobState.Cancelled, Find(queue, id).State);
        Assert.Empty(Find(queue, id).OutputPaths);
        Assert.Equal([ConversionJobState.Queued, ConversionJobState.Running, ConversionJobState.Cancelling, ConversionJobState.Cancelled], seen.StatesOf(id));
    }

    [Fact]
    public async Task CancellingMidWayLeavesNoPartialOutputOnDisk()
    {
        // An executor that follows the contract: it stages in a work folder, publishes only when done, and removes
        // everything it wrote when it is cancelled.
        var work = Path.Combine(_folder.Root, "work");
        var results = Path.Combine(_folder.Root, "results");
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(results);
        var halfWritten = NewGate();
        var executor = new ScriptedExecutor(async (job, progress, ct) =>
        {
            var staging = Path.Combine(work, $"job{job.Index}.tmp");
            try
            {
                await File.WriteAllBytesAsync(staging, new byte[1024], ct);
                progress.Report(new JobProgress(1, 10));
                halfWritten.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                File.Move(staging, Path.Combine(results, $"job{job.Index}.jpg"));
                return Done(job);
            }
            catch (OperationCanceledException)
            {
                File.Delete(staging);
                return Result.Cancelled<IReadOnlyList<string>>();
            }
        });
        var queue = Create(executor);
        var id = queue.Enqueue([Job(0)]).Value[0];
        await halfWritten.Task.WaitAsync(Ct);
        Assert.Single(Directory.GetFiles(work));

        queue.CancelAll();
        await queue.WhenIdleAsync(Ct);

        Assert.Equal(ConversionJobState.Cancelled, Find(queue, id).State);
        Assert.Empty(Directory.GetFiles(work));
        Assert.Empty(Directory.GetFiles(results));
    }

    [Fact]
    public async Task AnExecutorThatThrowsCancellationEndsTheJobCancelled()
    {
        var started = NewGate();
        var executor = new ScriptedExecutor(async (_, _, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Result.Success<IReadOnlyList<string>>([]);
        });
        var queue = Create(executor);
        var id = queue.Enqueue([Job(0)]).Value[0];
        await started.Task.WaitAsync(Ct);

        queue.Cancel(id);
        await queue.WhenIdleAsync(Ct);

        Assert.Equal(ConversionJobState.Cancelled, Find(queue, id).State);
    }

    [Fact]
    public async Task ACancelThatArrivesAfterTheConverterAlreadyFinishedKeepsTheTruthfulSuccess()
    {
        var reachedEnd = NewGate();
        var proceed = NewGate();
        var executor = new ScriptedExecutor(async (job, _, _) =>
        {
            reachedEnd.TrySetResult();
            await proceed.Task; // ignores the token: it completes its result
            return Done(job);
        });
        var queue = Create(executor);
        var id = queue.Enqueue([Job(0)]).Value[0];
        await reachedEnd.Task.WaitAsync(Ct);

        queue.Cancel(id);
        Assert.Equal(ConversionJobState.Cancelling, Find(queue, id).State);
        proceed.SetResult();
        await queue.WhenIdleAsync(Ct);

        Assert.Equal(ConversionJobState.Succeeded, Find(queue, id).State);
        Assert.NotEmpty(Find(queue, id).OutputPaths);
    }

    [Fact]
    public async Task CancelAllStopsEveryWaitingAndRunningJob()
    {
        var executor = new ScriptedExecutor(async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Result.Success<IReadOnlyList<string>>([]);
        });
        var queue = Create(executor, new ConversionQueueOptions { MaxConcurrency = 2 });
        queue.Enqueue([.. Enumerable.Range(0, 7).Select(Job)]);
        await Until(() => executor.Started == 2);

        queue.CancelAll();
        await queue.WhenIdleAsync(Ct);

        Assert.All(queue.Jobs, j => Assert.Equal(ConversionJobState.Cancelled, j.State));
        Assert.Equal(2, executor.Started); // the waiting ones never ran
    }

    [Fact]
    public async Task CancellingTwiceOrAFinishedJobIsHarmless()
    {
        var queue = Create(Instant());
        var id = queue.Enqueue([Job(0)]).Value[0];
        await queue.WhenIdleAsync(Ct);

        Assert.True(queue.Cancel(id).IsSuccess);
        Assert.True(queue.Cancel(id).IsSuccess);

        Assert.Equal(ConversionJobState.Succeeded, Find(queue, id).State);
    }

    [Fact]
    public void CancellingAnUnknownJobIsAnInputError()
    {
        var queue = Create(Instant());

        var result = queue.Cancel(999);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
    }

    // ---- time limit and failures -------------------------------------------------------------------------------------

    [Fact]
    public async Task AJobOverItsTimeLimitIsStoppedAndFailsWithARetryableTimeout()
    {
        var sawCancel = NewGate();
        var executor = new ScriptedExecutor(async (_, _, ct) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                sawCancel.TrySetResult();
                throw;
            }

            return Result.Success<IReadOnlyList<string>>([]);
        });
        var queue = Create(executor, new ConversionQueueOptions { JobTimeout = TimeSpan.FromMilliseconds(150) });
        var id = queue.Enqueue([Job(0)]).Value[0];

        await queue.WhenIdleAsync(Ct);

        var snapshot = Find(queue, id);
        Assert.True(sawCancel.Task.IsCompletedSuccessfully);
        Assert.Equal(ConversionJobState.Failed, snapshot.State);
        Assert.Equal(ErrorCode.Timeout, snapshot.Error!.Code);
        Assert.True(snapshot.Error.Retryable);
        Assert.Contains("too long", snapshot.Error.SafeMessage, StringComparison.Ordinal);
        Assert.Empty(snapshot.OutputPaths);
    }

    [Fact]
    public async Task ATimeoutOfOneJobDoesNotStopTheNextOne()
    {
        var executor = new ScriptedExecutor(async (job, _, ct) =>
        {
            if (job.Index == 0)
                await Task.Delay(Timeout.Infinite, ct);
            return Done(job);
        });
        var queue = Create(executor, new ConversionQueueOptions { MaxConcurrency = 1, JobTimeout = TimeSpan.FromMilliseconds(150) });
        var ids = queue.Enqueue([Job(0), Job(1)]).Value;

        await queue.WhenIdleAsync(Ct);

        Assert.Equal(ConversionJobState.Failed, Find(queue, ids[0]).State);
        Assert.Equal(ConversionJobState.Succeeded, Find(queue, ids[1]).State);
    }

    [Fact]
    public async Task ACancelByTheUserIsNeverReportedAsATimeout()
    {
        var executor = new ScriptedExecutor(async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Result.Success<IReadOnlyList<string>>([]);
        });
        var queue = Create(executor, new ConversionQueueOptions { JobTimeout = TimeSpan.FromMilliseconds(400) });
        var id = queue.Enqueue([Job(0)]).Value[0];
        await Until(() => Find(queue, id).State == ConversionJobState.Running);

        queue.Cancel(id);
        await queue.WhenIdleAsync(Ct);

        Assert.Equal(ConversionJobState.Cancelled, Find(queue, id).State);
        Assert.Null(Find(queue, id).Error);
    }

    [Fact]
    public async Task AnExecutorThatThrowsFailsTheJobWithoutLeakingTheExceptionText()
    {
        var executor = new ScriptedExecutor((_, _, _) => throw new InvalidOperationException(@"Could not read C:\secret\plans.png"));
        var queue = Create(executor);
        var id = queue.Enqueue([Job(0)]).Value[0];

        await queue.WhenIdleAsync(Ct);

        var snapshot = Find(queue, id);
        Assert.Equal(ConversionJobState.Failed, snapshot.State);
        Assert.Equal(ErrorCode.Unexpected, snapshot.Error!.Code);
        Assert.DoesNotContain("secret", snapshot.Error.SafeMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("plans", snapshot.Error.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AThrowingJobDoesNotStopTheQueue()
    {
        var executor = new ScriptedExecutor(async (job, _, _) =>
        {
            await Task.Yield();
            return job.Index % 2 == 0 ? throw new InvalidOperationException("boom") : Done(job);
        });
        var queue = Create(executor, new ConversionQueueOptions { MaxConcurrency = 1 });

        queue.Enqueue([.. Enumerable.Range(0, 6).Select(Job)]);
        await queue.WhenIdleAsync(Ct);

        Assert.Equal(3, queue.Jobs.Count(j => j.State == ConversionJobState.Failed));
        Assert.Equal(3, queue.Jobs.Count(j => j.State == ConversionJobState.Succeeded));
    }

    [Fact]
    public async Task AnExpectedFailureIsKeptAsItWasGiven()
    {
        var error = new OperationError(ErrorCode.IoFailure, "There is not enough space on the disk.", retryable: true);
        var queue = Create(new ScriptedExecutor((_, _, _) => Task.FromResult(Result.Failure<IReadOnlyList<string>>(error))));
        var id = queue.Enqueue([Job(0)]).Value[0];

        await queue.WhenIdleAsync(Ct);

        Assert.Equal(ConversionJobState.Failed, Find(queue, id).State);
        Assert.Equal(error, Find(queue, id).Error);
        Assert.Empty(Find(queue, id).OutputPaths);
    }

    // ---- retry -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFailedJobCanBeTriedAgainAsANewJob()
    {
        var attempts = 0;
        var executor = new ScriptedExecutor((job, _, _) => Task.FromResult(Interlocked.Increment(ref attempts) == 1
            ? Result.Failure<IReadOnlyList<string>>(new OperationError(ErrorCode.IoFailure, "Disk full.", retryable: true))
            : Done(job)));
        var queue = Create(executor);
        var first = queue.Enqueue([Job(0)]).Value[0];
        await queue.WhenIdleAsync(Ct);

        var retry = queue.Retry(first);
        await queue.WhenIdleAsync(Ct);

        Assert.True(retry.IsSuccess);
        Assert.NotEqual(first, retry.Value);
        Assert.Equal(ConversionJobState.Failed, Find(queue, first).State);
        Assert.Equal(ConversionJobState.Succeeded, Find(queue, retry.Value).State);
        Assert.Equal(Find(queue, first).Job, Find(queue, retry.Value).Job);
    }

    [Fact]
    public async Task ACancelledJobCanBeTriedAgain()
    {
        var gate = NewGate();
        var queue = Create(Gated(gate), new ConversionQueueOptions { MaxConcurrency = 1 });
        var ids = queue.Enqueue([Job(0), Job(1)]).Value;
        queue.Cancel(ids[1]);
        gate.SetResult();
        await queue.WhenIdleAsync(Ct);

        var retry = queue.Retry(ids[1]);
        await queue.WhenIdleAsync(Ct);

        Assert.Equal(ConversionJobState.Succeeded, Find(queue, retry.Value).State);
    }

    [Fact]
    public async Task OnlyAFailedOrCancelledJobCanBeRetried()
    {
        var gate = NewGate();
        var queue = Create(Gated(gate), new ConversionQueueOptions { MaxConcurrency = 1 });
        var ids = queue.Enqueue([Job(0), Job(1)]).Value;
        await Until(() => Find(queue, ids[0]).State == ConversionJobState.Running);

        Assert.True(queue.Retry(ids[0]).IsFailure); // running
        Assert.True(queue.Retry(ids[1]).IsFailure); // waiting
        Assert.True(queue.Retry(12345).IsFailure);  // unknown
        gate.SetResult();
        await queue.WhenIdleAsync(Ct);
        Assert.True(queue.Retry(ids[0]).IsFailure); // succeeded
    }

    // ---- progress ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ProgressIsPublishedWhenTheConverterKnowsThePageCount()
    {
        var step = NewGate();
        var executor = new ScriptedExecutor(async (job, progress, ct) =>
        {
            progress.Report(new JobProgress(0, 4));
            progress.Report(new JobProgress(2, 4));
            await step.Task.WaitAsync(ct);
            progress.Report(new JobProgress(4, 4));
            return Done(job);
        });
        var queue = Create(executor);
        var seen = new Collector();
        using var subscription = queue.Changes.Subscribe(seen);
        var id = queue.Enqueue([Job(0)]).Value[0];

        await Until(() => Find(queue, id).Progress.PagesCompleted == 2);
        var half = Find(queue, id).Progress;
        step.SetResult();
        await queue.WhenIdleAsync(Ct);

        Assert.True(half.IsDeterminate);
        Assert.Equal(0.5, half.Fraction);
        Assert.Equal(1.0, Find(queue, id).Progress.Fraction);
        lock (seen.Seen)
            Assert.Equal([0, 2, 4], seen.Seen.Where(s => s.Progress.PagesCompleted is not null).Select(s => s.Progress.PagesCompleted!.Value).Distinct());
    }

    [Fact]
    public async Task WithoutAPageCountTheProgressStaysUnknownAndHasNoPercentage()
    {
        var running = NewGate();
        var done = NewGate();
        var executor = new ScriptedExecutor(async (job, _, ct) =>
        {
            running.TrySetResult();
            await done.Task.WaitAsync(ct);
            return Done(job);
        });
        var queue = Create(executor);
        var id = queue.Enqueue([Job(0)]).Value[0];
        await running.Task.WaitAsync(Ct);

        var progress = Find(queue, id).Progress;
        done.SetResult();
        await queue.WhenIdleAsync(Ct);

        Assert.Equal(JobProgress.Unknown, progress);
        Assert.False(progress.IsDeterminate);
        Assert.Null(progress.Fraction);
    }

    [Fact]
    public async Task ReportedProgressIsKeptHonest()
    {
        var gate = NewGate();
        var executor = new ScriptedExecutor(async (job, progress, ct) =>
        {
            progress.Report(new JobProgress(3, 10));
            progress.Report(new JobProgress(1, 10));   // backwards: ignored
            progress.Report(new JobProgress(50, 10));  // past the total: held at the total
            progress.Report(new JobProgress(-4, 10));  // nonsense count: no number
            progress.Report(null!);                    // nothing: ignored
            await gate.Task.WaitAsync(ct);
            return Done(job);
        });
        var queue = Create(executor);
        var seen = new Collector();
        using var subscription = queue.Changes.Subscribe(seen);
        var id = queue.Enqueue([Job(0)]).Value[0];
        await Until(() => Find(queue, id).State == ConversionJobState.Running);
        await Task.Delay(100, Ct);

        gate.SetResult();
        await queue.WhenIdleAsync(Ct);

        lock (seen.Seen)
        {
            var completed = seen.Seen.Select(s => s.Progress).Where(p => p.PagesTotal == 10).Select(p => p.PagesCompleted).ToList();
            Assert.DoesNotContain(1, completed);
            Assert.Contains(3, completed);
            Assert.Contains(10, completed);
            Assert.All(seen.Seen, s => Assert.True(s.Progress.Fraction is null or (>= 0 and <= 1)));
        }
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(3, null, false)]
    [InlineData(3, 0, false)]
    [InlineData(0, 5, true)]
    [InlineData(5, 5, true)]
    public void AFractionExistsOnlyWhenThereIsATotal(int? completed, int? total, bool determinate)
    {
        var progress = new JobProgress(completed, total);

        Assert.Equal(determinate, progress.IsDeterminate);
        Assert.Equal(determinate, progress.Fraction is not null);
    }

    [Fact]
    public async Task ProgressAfterTheJobEndedIsIgnored()
    {
        IProgress<JobProgress>? kept = null;
        var executor = new ScriptedExecutor((job, progress, _) =>
        {
            kept = progress;
            return Task.FromResult(Done(job));
        });
        var queue = Create(executor);
        var id = queue.Enqueue([Job(0)]).Value[0];
        await queue.WhenIdleAsync(Ct);
        var before = Find(queue, id);

        kept!.Report(new JobProgress(1, 2));

        Assert.Equal(before, Find(queue, id));
    }

    // ---- capacity and bookkeeping ------------------------------------------------------------------------------------

    [Fact]
    public async Task AFullQueueRefusesAllOrNothingAndAcceptsAgainWhenRoomIsFree()
    {
        var gate = NewGate();
        var queue = Create(Gated(gate), new ConversionQueueOptions { MaxConcurrency = 1, MaxQueued = 3 });
        Assert.True(queue.Enqueue([Job(0), Job(1)]).IsSuccess);

        var refused = queue.Enqueue([Job(2), Job(3)]);

        Assert.True(refused.IsFailure);
        Assert.Equal(ErrorCode.InvalidInput, refused.Error!.Code);
        Assert.True(refused.Error.Retryable);
        Assert.Equal(2, queue.Jobs.Count); // none of the two was added

        gate.SetResult();
        await queue.WhenIdleAsync(Ct);
        Assert.True(queue.Enqueue([Job(2), Job(3)]).IsSuccess);
    }

    [Fact]
    public void NothingToEnqueueIsAnInputError()
    {
        var queue = Create(Instant());

        var result = queue.Enqueue([]);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.Throws<ArgumentNullException>(() => queue.Enqueue(null!));
        Assert.Throws<ArgumentNullException>(() => queue.Enqueue([null!]));
    }

    [Fact]
    public async Task ClearFinishedForgetsOnlyFinalJobs()
    {
        var gate = NewGate();
        var calls = 0;
        var executor = new ScriptedExecutor(async (job, _, ct) =>
        {
            if (Interlocked.Increment(ref calls) > 1)
                await gate.Task.WaitAsync(ct);
            return Done(job);
        });
        var queue = Create(executor, new ConversionQueueOptions { MaxConcurrency = 1 });
        var first = queue.Enqueue([Job(0)]).Value[0];
        await Until(() => Find(queue, first).IsFinal);
        var second = queue.Enqueue([Job(1), Job(2)]).Value;
        await Until(() => Find(queue, second[0]).State == ConversionJobState.Running);

        var cleared = queue.ClearFinished();

        Assert.Equal(1, cleared);
        Assert.Equal(second, queue.Jobs.Select(j => j.JobId));
        gate.SetResult();
        await queue.WhenIdleAsync(Ct);
    }

    [Fact]
    public async Task OnlyTheNewestFinalJobsAreRemembered()
    {
        var queue = Create(Instant(), new ConversionQueueOptions { MaxConcurrency = 1, MaxRetainedFinished = 3 });

        queue.Enqueue([.. Enumerable.Range(0, 10).Select(Job)]);
        await queue.WhenIdleAsync(Ct);

        Assert.Equal([8, 9, 10], queue.Jobs.Select(j => j.JobId));
    }

    [Fact]
    public async Task ASnapshotOnceTakenNeverChanges()
    {
        var gate = NewGate();
        var queue = Create(Gated(gate));
        var id = queue.Enqueue([Job(0)]).Value[0];
        var early = Find(queue, id);

        gate.SetResult();
        await queue.WhenIdleAsync(Ct);

        Assert.NotEqual(ConversionJobState.Succeeded, early.State);
        Assert.Equal(ConversionJobState.Succeeded, Find(queue, id).State);
    }

    // ---- waiting for idle --------------------------------------------------------------------------------------------

    [Fact]
    public async Task AQueueWithNoWorkIsIdleAtOnceAndAWaitCanBeCancelled()
    {
        var gate = NewGate();
        var queue = Create(Gated(gate));
        await queue.WhenIdleAsync(Ct);

        queue.Enqueue([Job(0)]);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var wait = queue.WhenIdleAsync(cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        gate.SetResult();
        await queue.WhenIdleAsync(Ct);
    }

    // ---- observers ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryJobsStatesArriveInLifeOrderEvenWithManyJobsAtOnce()
    {
        var queue = Create(Instant(), new ConversionQueueOptions { MaxConcurrency = 4 });
        var seen = new Collector();
        using var subscription = queue.Changes.Subscribe(seen);

        var ids = queue.Enqueue([.. Enumerable.Range(0, 50).Select(Job)]).Value;
        await queue.WhenIdleAsync(Ct);

        foreach (var id in ids)
            Assert.Equal([ConversionJobState.Queued, ConversionJobState.Running, ConversionJobState.Succeeded], seen.StatesOf(id));
    }

    [Fact]
    public async Task AThrowingObserverDoesNotHarmTheQueue()
    {
        var queue = Create(Instant());
        using var bad = queue.Changes.Subscribe(new ThrowingObserver());
        var good = new Collector();
        using var subscription = queue.Changes.Subscribe(good);

        var id = queue.Enqueue([Job(0)]).Value[0];
        await queue.WhenIdleAsync(Ct);

        Assert.Equal(ConversionJobState.Succeeded, Find(queue, id).State);
        Assert.Equal(3, good.StatesOf(id).Length);
    }

    private sealed class ThrowingObserver : IObserver<ConversionJobSnapshot>
    {
        public void OnNext(ConversionJobSnapshot value) => throw new InvalidOperationException("observer failure");

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }

    // ---- closing -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DisposingCancelsEverythingWaitsForTheConvertersAndCompletesTheObservers()
    {
        var stopped = false;
        var executor = new ScriptedExecutor(async (_, _, ct) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                await Task.Delay(150, CancellationToken.None); // cleanup takes a moment
                stopped = true;
                throw;
            }

            return Result.Success<IReadOnlyList<string>>([]);
        });
        var queue = new ConversionJobQueue(executor, new ConversionQueueOptions { MaxConcurrency = 1 });
        var seen = new Collector();
        using var subscription = queue.Changes.Subscribe(seen);
        var ids = queue.Enqueue([Job(0), Job(1), Job(2)]).Value;
        await Until(() => executor.Started == 1);

        await queue.DisposeAsync();

        Assert.True(stopped, "Dispose returned before the converter had stopped.");
        Assert.True(seen.Completed);
        Assert.All(queue.Jobs, j => Assert.Equal(ConversionJobState.Cancelled, j.State));
        Assert.Equal(1, executor.Started);
        Assert.All(ids, id => Assert.Contains(ConversionJobState.Cancelled, seen.StatesOf(id)));
    }

    [Fact]
    public async Task AfterDisposingNothingCanBeAddedOrRetried()
    {
        var queue = new ConversionJobQueue(Instant());
        var id = queue.Enqueue([Job(0)]).Value[0];
        await queue.WhenIdleAsync(Ct);
        await queue.DisposeAsync();

        Assert.True(queue.Enqueue([Job(1)]).IsFailure);
        Assert.Equal(ErrorCode.DeviceUnavailable, queue.Enqueue([Job(1)]).Error!.Code);
        Assert.True(queue.Retry(id).IsFailure);
    }

    [Fact]
    public async Task DisposingTwiceOrAtTheSameTimeIsHarmless()
    {
        var queue = new ConversionJobQueue(Instant());
        queue.Enqueue([Job(0), Job(1)]);

        await Task.WhenAll(queue.DisposeAsync().AsTask(), queue.DisposeAsync().AsTask());
        await queue.DisposeAsync();
    }

    [Fact]
    public async Task ANeverUsedQueueDisposesCleanly()
    {
        var queue = new ConversionJobQueue(Instant());

        await queue.DisposeAsync();

        Assert.Empty(queue.Jobs);
    }

    // ---- options -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 10, 60, 5)]
    [InlineData(17, 10, 60, 5)]
    [InlineData(2, 0, 60, 5)]
    [InlineData(2, 10, 0, 5)]
    [InlineData(2, 10, 100000, 5)]
    [InlineData(2, 10, 60, -1)]
    public void ImpossibleOptionsAreRejected(int concurrency, int queued, int timeoutSeconds, int retained)
    {
        var options = new ConversionQueueOptions
        {
            MaxConcurrency = concurrency,
            MaxQueued = queued,
            JobTimeout = TimeSpan.FromSeconds(timeoutSeconds),
            MaxRetainedFinished = retained
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => new ConversionJobQueue(Instant(), options));
    }

    [Fact]
    public void TheQueueNeedsAnExecutor()
    {
        Assert.Throws<ArgumentNullException>(() => new ConversionJobQueue(null!));
    }
}
