using Lunar.Application.Abstractions.System;
using Lunar.Application.System.Models;
using Lunar.Application.System.UseCases;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Application.Tests.System;

/// <summary>ADR-0021: while a slider is dragged, the first value goes at once, then at most one write per interval, and the last one always arrives.</summary>
public sealed class BrightnessWriterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class SlowService : IBrightnessService
    {
        private readonly Lock _gate = new();

        public TimeSpan WriteTime { get; init; } = TimeSpan.FromMilliseconds(20);

        public ErrorCode? FailWith { get; set; }

        public List<(int Percent, DateTime At)> Writes { get; } = [];

        public int Concurrent { get; private set; }

        public int MaxConcurrent { get; private set; }

        /// <summary>Waits until <paramref name="count"/> writes have started, so a test never depends on how fast the thread pool is.</summary>
        public async Task WritesStartedAsync(int count)
        {
            for (var i = 0; i < 1000; i++)
            {
                lock (_gate)
                {
                    if (Writes.Count >= count)
                        return;
                }

                await Task.Delay(5);
            }

            throw new TimeoutException("The write did not start.");
        }

        public event Action<string>? Changed
        {
            add { }
            remove { }
        }

        public Task<Result<BrightnessState>> GetAsync(string? displayId, CancellationToken ct) =>
            Task.FromResult(Result.Success(new BrightnessState(50, BrightnessSource.BuiltIn, CapabilityStatus.Supported)));

        public async Task<Result> SetAsync(string? displayId, int percent, CancellationToken ct)
        {
            lock (_gate)
            {
                Concurrent++;
                MaxConcurrent = Math.Max(MaxConcurrent, Concurrent);
                Writes.Add((percent, DateTime.UtcNow));
            }

            await Task.Delay(WriteTime, ct);
            lock (_gate)
                Concurrent--;
            return FailWith is { } code ? Result.Failure(new OperationError(code, "No.")) : Result.Success();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task TheFirstValueIsWrittenAtOnce()
    {
        var service = new SlowService();
        // A long interval: had the first write waited for it, the check below would fail by a wide margin even on a slow CI runner.
        using var writer = new BrightnessWriter(service, interval: TimeSpan.FromSeconds(3));
        var started = DateTime.UtcNow;

        Assert.True((await writer.RequestAsync(null, 40, Ct)).IsSuccess);

        var write = Assert.Single(service.Writes);
        Assert.Equal(40, write.Percent);
        Assert.True(write.At - started < TimeSpan.FromSeconds(1), "The first write waited.");
    }

    [Fact]
    public async Task ADragOfManyValuesWritesFewOneAtATimeAndAlwaysEndsOnTheLast()
    {
        var service = new SlowService();
        using var writer = new BrightnessWriter(service, interval: TimeSpan.FromMilliseconds(60));
        var requests = new List<Task<Result>>();

        for (var value = 0; value <= 100; value += 2)
        {
            requests.Add(writer.RequestAsync(null, value, Ct));
            await Task.Delay(5, Ct); // a drag sends a value every few milliseconds
        }

        var results = await Task.WhenAll(requests);

        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(100, service.Writes[^1].Percent);
        Assert.Equal(1, service.MaxConcurrent);
        Assert.InRange(service.Writes.Count, 2, 25); // 51 requests: far fewer writes than values, however slow the machine
        for (var i = 1; i < service.Writes.Count; i++)
            Assert.True(service.Writes[i].At - service.Writes[i - 1].At >= TimeSpan.FromMilliseconds(55), "Two writes came too close.");
    }

    [Fact]
    public async Task AFailedWriteIsReportedToEveryRequestItCarried()
    {
        var service = new SlowService { FailWith = ErrorCode.IoFailure, WriteTime = TimeSpan.FromMilliseconds(80) };
        using var writer = new BrightnessWriter(service, interval: TimeSpan.FromMilliseconds(50));

        var first = writer.RequestAsync(null, 10, Ct);
        await service.WritesStartedAsync(1);
        var replaced = writer.RequestAsync(null, 20, Ct);
        var last = writer.RequestAsync(null, 30, Ct);

        Assert.Equal(ErrorCode.IoFailure, (await first).Error!.Code);
        Assert.Equal(ErrorCode.IoFailure, (await replaced).Error!.Code);
        Assert.Equal(ErrorCode.IoFailure, (await last).Error!.Code);
        Assert.Equal([10, 30], service.Writes.Select(w => w.Percent));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task AValueOutside0To100IsRefusedWithoutAWrite(int percent)
    {
        var service = new SlowService();
        using var writer = new BrightnessWriter(service);

        Assert.Equal(ErrorCode.InvalidInput, (await writer.RequestAsync(null, percent, Ct)).Error!.Code);
        Assert.Empty(service.Writes);
    }

    [Fact]
    public async Task DisposingCancelsWhatIsWaitingAndRefusesNewRequests()
    {
        var service = new SlowService { WriteTime = TimeSpan.FromMilliseconds(100) };
        var writer = new BrightnessWriter(service, interval: TimeSpan.FromMilliseconds(200));
        var running = writer.RequestAsync(null, 10, Ct);
        await service.WritesStartedAsync(1);
        var waiting = writer.RequestAsync(null, 20, Ct);

        writer.Dispose();

        Assert.True((await waiting).IsCancelled);
        Assert.True((await writer.RequestAsync(null, 30, Ct)).IsCancelled);
        await running;
        Assert.DoesNotContain(service.Writes, w => w.Percent is 20 or 30);
    }
}
