using System.IO.Pipes;
using Lunar.Application.Abstractions.Shell;
using Lunar.Domain.Shared;
using Lunar.Infrastructure.Startup;
using Xunit;

namespace Lunar.Infrastructure.Tests.Startup;

// Uses real named mutexes and pipes with a unique name per test, so tests never touch a running Lunar.
public sealed class SingleInstanceCoordinatorTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(400);

    private static string UniqueName() => "Lunar.Tests." + Guid.NewGuid().ToString("N");

    private static SingleInstanceCoordinator Create(string name) => new(name, Short, Short);

    private static async Task<bool> WaitFor(Task task, TimeSpan timeout) =>
        await Task.WhenAny(task, Task.Delay(timeout)) == task;

    [Fact]
    public void FirstInstanceIsPrimaryAndLaterOnesAreSecondary()
    {
        var name = UniqueName();
        using var first = Create(name);
        using var second = Create(name);

        Assert.Equal(InstanceRole.Primary, first.Start().Value);
        Assert.Equal(InstanceRole.Secondary, second.Start().Value);
    }

    [Fact]
    public async Task StartTwiceAndMisuseFailWithInvalidInput()
    {
        var name = UniqueName();
        using var primary = Create(name);

        Assert.True(primary.Start().IsSuccess);

        Assert.Equal(ErrorCode.InvalidInput, primary.Start().Error!.Code);
        var request = await primary.RequestPrimaryActivationAsync(CancellationToken.None);
        Assert.Equal(ErrorCode.InvalidInput, request.Error!.Code);
    }

    [Fact]
    public async Task RequestBeforeStartFails()
    {
        using var coordinator = Create(UniqueName());

        var result = await coordinator.RequestPrimaryActivationAsync(CancellationToken.None);

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
    }

    [Fact]
    public async Task SecondaryActivationIsDeliveredToThePrimaryOnce()
    {
        var name = UniqueName();
        using var primary = Create(name);
        using var secondary = Create(name);
        var raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        primary.ActivationRequested += () =>
        {
            Interlocked.Increment(ref count);
            raised.TrySetResult();
        };
        primary.Start();
        secondary.Start();

        var result = await secondary.RequestPrimaryActivationAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(await WaitFor(raised.Task, TimeSpan.FromSeconds(5)));
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public async Task PrimaryKeepsListeningForRepeatedRequests()
    {
        var name = UniqueName();
        using var primary = Create(name);
        using var secondary = Create(name);
        var count = 0;
        var third = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.ActivationRequested += () =>
        {
            if (Interlocked.Increment(ref count) == 3)
                third.TrySetResult();
        };
        primary.Start();
        secondary.Start();

        for (var i = 0; i < 3; i++)
            Assert.True((await secondary.RequestPrimaryActivationAsync(CancellationToken.None)).IsSuccess);

        Assert.True(await WaitFor(third.Task, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task UnknownBytesAreIgnoredAndTheListenerKeepsWorking()
    {
        var name = UniqueName();
        using var primary = Create(name);
        var raised = 0;
        var valid = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.ActivationRequested += () =>
        {
            Interlocked.Increment(ref raised);
            valid.TrySetResult();
        };
        primary.Start();

        await using (var client = new NamedPipeClientStream(".", name + ".activate", PipeDirection.Out,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            var token = TestContext.Current.CancellationToken;
            await client.ConnectAsync(2000, token);
            await client.WriteAsync(new byte[] { 0x02 }, token);
            await client.FlushAsync(token);
        }

        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(0, Volatile.Read(ref raised));

        using var secondary = Create(name);
        secondary.Start();
        Assert.True((await secondary.RequestPrimaryActivationAsync(CancellationToken.None)).IsSuccess);
        Assert.True(await WaitFor(valid.Task, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ClientThatSendsNothingDoesNotBlockTheListener()
    {
        var name = UniqueName();
        using var primary = Create(name);
        var valid = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.ActivationRequested += () => valid.TrySetResult();
        primary.Start();

        await using var silent = new NamedPipeClientStream(".", name + ".activate", PipeDirection.Out,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await silent.ConnectAsync(2000, TestContext.Current.CancellationToken);

        // The listener drops the silent client after its read timeout, then serves the next one; the secondary
        // waits longer than that timeout.
        using var secondary = new SingleInstanceCoordinator(name, TimeSpan.FromSeconds(5), Short);
        secondary.Start();
        var result = await secondary.RequestPrimaryActivationAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(await WaitFor(valid.Task, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ThrowingHandlerDoesNotStopLaterActivations()
    {
        var name = UniqueName();
        using var primary = Create(name);
        using var secondary = Create(name);
        var calls = 0;
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.ActivationRequested += () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new InvalidOperationException("handler failure");
            second.TrySetResult();
        };
        primary.Start();
        secondary.Start();

        await secondary.RequestPrimaryActivationAsync(CancellationToken.None);
        await secondary.RequestPrimaryActivationAsync(CancellationToken.None);

        Assert.True(await WaitFor(second.Task, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ThrowingHandlerIsReportedToTheDiagnosticLogByTypeOnly()
    {
        var name = UniqueName();
        var log = new CollectingLog();
        using var primary = new SingleInstanceCoordinator(name, Short, Short, log);
        using var secondary = Create(name);
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.ActivationRequested += () =>
        {
            handled.TrySetResult();
            throw new InvalidOperationException(@"C:\Users\alice\secret.docx");
        };
        primary.Start();
        secondary.Start();

        await secondary.RequestPrimaryActivationAsync(CancellationToken.None);
        Assert.True(await WaitFor(handled.Task, TimeSpan.FromSeconds(5)));
        await Task.Delay(200, TestContext.Current.CancellationToken);

        var logged = Assert.Single(log.Events);
        Assert.Equal("instance.handler.failed", logged.EventId);
        Assert.Equal(typeof(InvalidOperationException), logged.ExceptionType);
        Assert.DoesNotContain("alice", logged.ToString());
    }

    private sealed class CollectingLog : Lunar.Application.Abstractions.Diagnostics.IDiagnosticLog
    {
        private readonly List<Lunar.Application.Abstractions.Diagnostics.DiagnosticEvent> _events = [];

        public IReadOnlyList<Lunar.Application.Abstractions.Diagnostics.DiagnosticEvent> Events
        {
            get
            {
                lock (_events)
                    return [.. _events];
            }
        }

        public void Write(Lunar.Application.Abstractions.Diagnostics.DiagnosticEvent diagnosticEvent)
        {
            lock (_events)
                _events.Add(diagnosticEvent);
        }
    }

    [Fact]
    public async Task UnreachablePrimaryTimesOut()
    {
        var name = UniqueName();
        // Something else owns the instance name but nobody listens: the secondary must give up, not hang.
        using var foreignOwner = new Mutex(false, $@"Local\{name}.instance", out var created);
        Assert.True(created);
        using var secondary = Create(name);
        Assert.Equal(InstanceRole.Secondary, secondary.Start().Value);

        var result = await secondary.RequestPrimaryActivationAsync(CancellationToken.None);

        Assert.Equal(ErrorCode.Timeout, result.Error!.Code);
        Assert.True(result.Error.Retryable);
    }

    [Fact]
    public async Task CancelledRequestReportsCancellation()
    {
        var name = UniqueName();
        using var foreignOwner = new Mutex(false, $@"Local\{name}.instance", out _);
        using var secondary = new SingleInstanceCoordinator(name, TimeSpan.FromSeconds(30), Short);
        secondary.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        var result = await secondary.RequestPrimaryActivationAsync(cts.Token);

        Assert.True(result.IsCancelled);
    }

    [Fact]
    public async Task DisposeStopsTheListenerFreesTheNameAndIsIdempotent()
    {
        var name = UniqueName();
        var primary = Create(name);
        primary.Start();

        primary.Dispose();
        primary.Dispose();

        Assert.True(await WaitFor(primary.ListenerCompletion, TimeSpan.FromSeconds(5)));
        using var next = Create(name);
        Assert.Equal(InstanceRole.Primary, next.Start().Value);
    }

    [Fact]
    public void StartAfterDisposeFails()
    {
        var coordinator = Create(UniqueName());
        coordinator.Dispose();

        Assert.Equal(ErrorCode.InvalidInput, coordinator.Start().Error!.Code);
    }

    [Fact]
    public void BlankNameIsRejected()
    {
        Assert.ThrowsAny<ArgumentException>(() => new SingleInstanceCoordinator(" "));
    }
}
