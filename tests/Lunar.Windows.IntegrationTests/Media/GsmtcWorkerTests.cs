using Lunar.Windows.Media;
using Xunit;

namespace Lunar.Windows.IntegrationTests.Media;

// The media-session objects belong to the thread that first used them, so every call, and the code after every await inside
// it, must run on one thread. A second read right after the first used to lose every session because it did not.
public sealed class GsmtcWorkerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WorkRunsOnTheWorkerThreadNotTheCallers()
    {
        var worker = new GsmtcWorker();

        var thread = await worker.RunAsync(() => Task.FromResult(System.Environment.CurrentManagedThreadId));

        Assert.NotEqual(System.Environment.CurrentManagedThreadId, thread);
        worker.Complete(() => { });
    }

    [Fact]
    public async Task EverythingAfterEachAwaitResumesOnTheSameThread()
    {
        var worker = new GsmtcWorker();

        var threads = await worker.RunAsync(async () =>
        {
            var seen = new List<int> { System.Environment.CurrentManagedThreadId };
            await Task.Delay(5, Ct);
            seen.Add(System.Environment.CurrentManagedThreadId);
            await Task.Yield();
            seen.Add(System.Environment.CurrentManagedThreadId);
            await Task.Run(() => 1, Ct); // work that hops to the pool and comes back
            seen.Add(System.Environment.CurrentManagedThreadId);
            return seen;
        });

        Assert.Single(threads.Distinct());
        worker.Complete(() => { });
    }

    [Fact]
    public async Task ManySeparateCallsAllRunOnTheSameThread()
    {
        var worker = new GsmtcWorker();

        var threads = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => worker.RunAsync(async () =>
        {
            await Task.Delay(1, Ct);
            return System.Environment.CurrentManagedThreadId;
        })));

        Assert.Single(threads.Distinct());
        worker.Complete(() => { });
    }

    [Fact]
    public async Task ACallsExceptionComesBackThroughItsTaskAndTheWorkerKeepsGoing()
    {
        var worker = new GsmtcWorker();

        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.RunAsync<int>(() => throw new InvalidOperationException("x")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.RunAsync<int>(async () =>
        {
            await Task.Delay(1, Ct);
            throw new InvalidOperationException("y");
        }));

        Assert.Equal(7, await worker.RunAsync(() => Task.FromResult(7)));
        worker.Complete(() => { });
    }

    [Fact]
    public async Task CompletingRunsTheCleanupOnTheWorkerThreadAfterQueuedWork()
    {
        var worker = new GsmtcWorker();
        var order = new List<string>();
        var workThread = await worker.RunAsync(() => Task.FromResult(System.Environment.CurrentManagedThreadId));
        var cleanupThread = new TaskCompletionSource<int>();

        var queued = worker.RunAsync(async () =>
        {
            await Task.Delay(20, Ct);
            order.Add("work");
            return 0;
        });
        worker.Complete(() =>
        {
            order.Add("cleanup");
            cleanupThread.SetResult(System.Environment.CurrentManagedThreadId);
        });

        await queued;
        Assert.Equal(workThread, await cleanupThread.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Equal(["work", "cleanup"], order);
    }

    [Fact]
    public async Task AfterCompletionNewWorkIsRefusedAndCompletingAgainIsHarmless()
    {
        var worker = new GsmtcWorker();
        var done = new TaskCompletionSource();
        worker.Complete(() => done.SetResult());
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Throws<ObjectDisposedException>(() => { _ = worker.RunAsync(() => Task.FromResult(1)); });
        worker.Complete(() => throw new InvalidOperationException("must not run"));
    }

    [Fact]
    public void ANullWorkItemIsRejected()
    {
        var worker = new GsmtcWorker();

        Assert.Throws<ArgumentNullException>(() => { _ = worker.RunAsync<int>(null!); });
        worker.Complete(() => { });
    }
}
