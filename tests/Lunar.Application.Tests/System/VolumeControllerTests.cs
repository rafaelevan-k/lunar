using Lunar.Application.Abstractions.System;
using Lunar.Application.System.Models;
using Lunar.Application.System.UseCases;
using Lunar.Application.Tests.Fakes;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Application.Tests.System;

// The use cases against a scripted fake service: no hardware, no real time.
public sealed class VolumeControllerTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 4, 9, 0, 0, TimeSpan.FromHours(7));

    private sealed class ScriptedService : IVolumeService
    {
        private readonly TestObservable<VolumeState> _changes = new();
        private readonly object _gate = new();
        private int _inside;

        public VolumeState State { get; set; } = new(0.5f, false, CapabilityStatus.Supported);

        public List<string> Calls { get; } = [];

        public int Writes => Calls.Count(c => c.StartsWith("volume", StringComparison.Ordinal) || c.StartsWith("mute", StringComparison.Ordinal));

        public int MaxConcurrent { get; private set; }

        /// <summary>When set, writes wait for it (one at a time) before they are applied.</summary>
        public TaskCompletionSource? Hold { get; set; }

        public Queue<OperationError> WriteFailures { get; } = new();

        public Func<float, float> Round { get; set; } = v => v;

        /// <summary>Report the new state to subscribers like Windows does: on every write, before the call returns.</summary>
        public bool Echo { get; set; } = true;

        public bool Disposed { get; private set; }

        public int ObserverCount => _changes.ObserverCount;

        public IObservable<VolumeState> Changes => _changes;

        public Task<Result<VolumeState>> GetAsync(CancellationToken ct)
        {
            lock (_gate)
                Calls.Add("get");
            return Task.FromResult(ct.IsCancellationRequested ? Result.Cancelled<VolumeState>() : Result.Success(State));
        }

        public async Task<Result> SetVolumeAsync(float scalar, CancellationToken ct) =>
            await Write("volume " + scalar.ToString("0.000"), ct, () => State = State with { }, () =>
            {
                State = new VolumeState(Round(scalar), State.IsMuted, State.Capability);
            });

        public async Task<Result> SetMuteAsync(bool muted, CancellationToken ct) =>
            await Write("mute " + muted, ct, () => State = State with { }, () =>
            {
                State = new VolumeState(State.Scalar, muted, State.Capability);
            });

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _changes.Complete();
            return ValueTask.CompletedTask;
        }

        public void RaiseExternal(VolumeState state)
        {
            State = state;
            _changes.Publish(state);
        }

        private async Task<Result> Write(string call, CancellationToken ct, Action _, Action apply)
        {
            if (ct.IsCancellationRequested)
                return Result.Cancelled();

            lock (_gate)
            {
                _inside++;
                MaxConcurrent = Math.Max(MaxConcurrent, _inside);
                Calls.Add(call);
            }

            try
            {
                var hold = Hold;
                if (hold is not null)
                    await hold.Task.WaitAsync(ct).ConfigureAwait(false);
                if (WriteFailures.TryDequeue(out var failure))
                    return Result.Failure(failure);

                apply();
                if (Echo)
                    _changes.Publish(State);
                return Result.Success();
            }
            catch (OperationCanceledException)
            {
                return Result.Cancelled();
            }
            finally
            {
                lock (_gate)
                    _inside--;
            }
        }
    }

    private sealed class Recorder : IObserver<VolumeSnapshot>
    {
        private readonly object _gate = new();
        private readonly List<VolumeSnapshot> _values = [];

        public bool Completed { get; private set; }

        public void OnNext(VolumeSnapshot value)
        {
            lock (_gate)
                _values.Add(value);
        }

        public void OnError(Exception error) => throw new InvalidOperationException("OnError must never be called.");

        public void OnCompleted() => Completed = true;

        public VolumeSnapshot[] Snapshot()
        {
            lock (_gate)
                return [.. _values];
        }
    }

    private sealed record Rig(VolumeController Controller, ScriptedService Service, FakeClock Clock, Recorder Recorder);

    private static async Task<Rig> Create(Action<ScriptedService>? configure = null, VolumeControllerOptions? options = null, bool initialize = true)
    {
        var service = new ScriptedService();
        configure?.Invoke(service);
        var clock = new FakeClock(Start);
        var controller = new VolumeController(service, clock, options);
        var recorder = new Recorder();
        controller.Snapshots.Subscribe(recorder);
        if (initialize)
            Assert.True((await controller.InitializeAsync(CancellationToken.None)).IsSuccess);
        return new Rig(controller, service, clock, recorder);
    }

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 3000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < end)
            await Task.Delay(2);
        Assert.True(condition(), "The condition was not met in time.");
    }

    // ---- start ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task InitializingReadsTheStateAndDeliversItAsTheInitialSnapshot()
    {
        var rig = await Create(s => s.State = new VolumeState(0.3f, true, CapabilityStatus.Supported));

        Assert.Equal(new VolumeState(0.3f, true, CapabilityStatus.Supported), rig.Controller.Current);
        Assert.Equal([new VolumeSnapshot(new VolumeState(0.3f, true, CapabilityStatus.Supported), VolumeChangeSource.Initial)], rig.Recorder.Snapshot());
        Assert.Equal(1, rig.Service.ObserverCount);
    }

    [Fact]
    public async Task InitializingTwiceDoesNotSubscribeTwice()
    {
        var rig = await Create();

        await rig.Controller.InitializeAsync(CancellationToken.None);

        Assert.Equal(1, rig.Service.ObserverCount);
    }

    [Fact]
    public async Task BeforeInitializationThereIsNoStateAndNoSnapshots()
    {
        var rig = await Create(initialize: false);

        Assert.Null(rig.Controller.Current);
        Assert.Empty(rig.Recorder.Snapshot());
        Assert.Equal(0, rig.Service.ObserverCount);
    }

    [Fact]
    public async Task AnUnavailableDeviceIsAStateTheUiCanShow()
    {
        var rig = await Create(s => s.State = VolumeState.Unavailable);

        Assert.Equal(CapabilityStatus.Unavailable, rig.Controller.Current!.Capability);
        Assert.Equal(VolumeChangeSource.Initial, rig.Recorder.Snapshot()[0].Source);
    }

    // ---- clamp ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(-5f, 0f)]
    [InlineData(-0.001f, 0f)]
    [InlineData(0f, 0f)]
    [InlineData(0.42f, 0.42f)]
    [InlineData(1f, 1f)]
    [InlineData(1.7f, 1f)]
    [InlineData(float.PositiveInfinity, 1f)]
    [InlineData(float.NegativeInfinity, 0f)]
    public async Task LevelsAreClampedIntoTheValidRangeBeforeTheyReachTheService(float requested, float sent)
    {
        var rig = await Create();

        var result = await rig.Controller.SetVolumeAsync(requested, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(sent, rig.Service.State.Scalar);
    }

    [Fact]
    public async Task NotANumberIsInvalidInputAndNothingIsSent()
    {
        var rig = await Create();

        var result = await rig.Controller.SetVolumeAsync(float.NaN, CancellationToken.None);

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.Equal(0, rig.Service.Writes);
    }

    // ---- coalescing -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ADragOfAHundredChangesSendsOnlyTheFirstAndTheNewest()
    {
        var rig = await Create();
        rig.Service.Hold = new TaskCompletionSource();
        var first = rig.Controller.SetVolumeAsync(0.01f, CancellationToken.None);
        await WaitFor(() => rig.Service.Writes == 1); // the first is now in flight and blocked

        var tasks = Enumerable.Range(2, 99).Select(i => rig.Controller.SetVolumeAsync(i / 100f, CancellationToken.None)).ToArray();
        rig.Service.Hold.SetResult();
        await first;
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(2, rig.Service.Writes);
        Assert.Equal(["volume 0.010", "volume 1.000"], rig.Service.Calls.Where(c => c.StartsWith("volume", StringComparison.Ordinal)));
        Assert.Equal(1.0f, rig.Service.State.Scalar);
    }

    [Fact]
    public async Task WritesNeverOverlapAndTheLastRequestAlwaysWins()
    {
        var rig = await Create();
        var rnd = new Random(11);
        var requests = new List<Task<Result>>();
        float last = 0;

        for (var i = 0; i < 300; i++)
        {
            last = (float)rnd.NextDouble();
            requests.Add(rig.Controller.SetVolumeAsync(last, CancellationToken.None));
            if (i % 17 == 0)
                await Task.Yield();
        }

        var results = await Task.WhenAll(requests);

        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(1, rig.Service.MaxConcurrent);
        Assert.Equal(last, rig.Service.State.Scalar, 4);
        Assert.True(rig.Service.Writes < 300, "Coalescing must have dropped some intermediate values.");
    }

    [Fact]
    public async Task ConcurrentCallersFromManyThreadsEndOnTheNewestValueSent()
    {
        var rig = await Create();

        var tasks = Enumerable.Range(0, 8).Select(t => Task.Run(async () =>
        {
            for (var i = 0; i < 200; i++)
                await rig.Controller.SetVolumeAsync(((t * 200) + i) % 100 / 100f, CancellationToken.None);
        })).ToArray();
        await Task.WhenAll(tasks);
        await rig.Controller.SetVolumeAsync(0.77f, CancellationToken.None);

        Assert.Equal(1, rig.Service.MaxConcurrent);
        Assert.Equal(0.77f, rig.Service.State.Scalar);
    }

    [Fact]
    public async Task LevelAndMuteRequestsAreBothDeliveredNewestFirstEach()
    {
        var rig = await Create();
        rig.Service.Hold = new TaskCompletionSource();
        var first = rig.Controller.SetVolumeAsync(0.1f, CancellationToken.None);
        await WaitFor(() => rig.Service.Writes == 1);

        var level = rig.Controller.SetVolumeAsync(0.9f, CancellationToken.None);
        var mute1 = rig.Controller.SetMuteAsync(true, CancellationToken.None);
        var mute2 = rig.Controller.SetMuteAsync(false, CancellationToken.None);
        var mute3 = rig.Controller.SetMuteAsync(true, CancellationToken.None);
        rig.Service.Hold.SetResult();
        await Task.WhenAll(first, level, mute1, mute2, mute3);

        Assert.Equal(0.9f, rig.Service.State.Scalar);
        Assert.True(rig.Service.State.IsMuted);
        Assert.Equal(["volume 0.100", "volume 0.900", "mute True"], rig.Service.Calls.Where(c => c != "get"));
    }

    // ---- mute -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ToggleFlipsTheCurrentMute()
    {
        var rig = await Create();

        Assert.True((await rig.Controller.ToggleMuteAsync(CancellationToken.None)).IsSuccess);
        Assert.True(rig.Service.State.IsMuted);
        Assert.True((await rig.Controller.ToggleMuteAsync(CancellationToken.None)).IsSuccess);
        Assert.False(rig.Service.State.IsMuted);
    }

    [Fact]
    public async Task TwoQuickTogglesEndWhereTheyStarted()
    {
        var rig = await Create();
        rig.Service.Hold = new TaskCompletionSource();
        var slow = rig.Controller.SetVolumeAsync(0.2f, CancellationToken.None);
        await WaitFor(() => rig.Service.Writes == 1);

        var a = rig.Controller.ToggleMuteAsync(CancellationToken.None);
        var b = rig.Controller.ToggleMuteAsync(CancellationToken.None);
        rig.Service.Hold.SetResult();
        await Task.WhenAll(slow, a, b);

        Assert.False(rig.Service.State.IsMuted);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(8)]
    public async Task AnyNumberOfRapidTogglesEndsWithTheRightParity(int toggles)
    {
        var rig = await Create();
        rig.Service.Hold = new TaskCompletionSource();
        var slow = rig.Controller.SetVolumeAsync(0.2f, CancellationToken.None);
        await WaitFor(() => rig.Service.Writes == 1);

        var tasks = Enumerable.Range(0, toggles).Select(_ => rig.Controller.ToggleMuteAsync(CancellationToken.None)).ToArray();
        rig.Service.Hold.SetResult();
        await Task.WhenAll(tasks.Append(slow));

        Assert.Equal(toggles % 2 == 1, rig.Service.State.IsMuted);
    }

    [Fact]
    public async Task TogglingBeforeInitializationReadsTheStateFirst()
    {
        var rig = await Create(s => s.State = new VolumeState(0.5f, true, CapabilityStatus.Supported), initialize: false);

        var result = await rig.Controller.ToggleMuteAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(rig.Service.State.IsMuted); // it was muted, now it is not
        Assert.Equal(VolumeChangeSource.Initial, rig.Recorder.Snapshot()[0].Source);
    }

    [Fact]
    public async Task ATogglePicksUpAnOutsideMuteChange()
    {
        var rig = await Create();
        rig.Service.RaiseExternal(new VolumeState(0.5f, true, CapabilityStatus.Supported)); // muted with the keyboard key

        await rig.Controller.ToggleMuteAsync(CancellationToken.None);

        Assert.False(rig.Service.State.IsMuted);
    }

    // ---- feedback loops ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheEchoOfAWriteIsLabelledSelfAndAnOutsideChangeExternal()
    {
        var rig = await Create();

        await rig.Controller.SetVolumeAsync(0.6f, CancellationToken.None);
        rig.Service.RaiseExternal(new VolumeState(0.8f, false, CapabilityStatus.Supported));

        var snapshots = rig.Recorder.Snapshot();
        Assert.Equal(VolumeChangeSource.Initial, snapshots[0].Source);
        Assert.Equal(new VolumeSnapshot(new VolumeState(0.6f, false, CapabilityStatus.Supported), VolumeChangeSource.Self), snapshots[1]);
        Assert.Equal(new VolumeSnapshot(new VolumeState(0.8f, false, CapabilityStatus.Supported), VolumeChangeSource.External), snapshots[2]);
    }

    [Fact]
    public async Task ADriverThatRoundsTheLevelStillEchoesAsSelf()
    {
        var rig = await Create(s => s.Round = v => MathF.Round(v * 100) / 100);

        await rig.Controller.SetVolumeAsync(0.504f, CancellationToken.None); // reported back as 0.50

        Assert.Equal(VolumeChangeSource.Self, rig.Recorder.Snapshot()[1].Source);
    }

    [Fact]
    public async Task AnEchoIsConsumedSoALaterIdenticalReportIsAnOutsideChange()
    {
        var rig = await Create();

        await rig.Controller.SetVolumeAsync(0.6f, CancellationToken.None);
        rig.Service.RaiseExternal(new VolumeState(0.6f, false, CapabilityStatus.Supported)); // the same value, again

        Assert.Equal([VolumeChangeSource.Initial, VolumeChangeSource.Self, VolumeChangeSource.External], rig.Recorder.Snapshot().Select(s => s.Source));
    }

    [Fact]
    public async Task AReportThatArrivesAfterTheEchoWindowCountsAsOutside()
    {
        var rig = await Create(s => s.Echo = false);
        await rig.Controller.SetVolumeAsync(0.6f, CancellationToken.None);
        rig.Clock.LocalNow += TimeSpan.FromSeconds(3);

        rig.Service.RaiseExternal(new VolumeState(0.6f, false, CapabilityStatus.Supported));

        Assert.Equal(VolumeChangeSource.External, rig.Recorder.Snapshot()[^1].Source);
    }

    [Fact]
    public async Task AReportBeforeTheWriteReturnsIsStillRecognisedAsTheEcho()
    {
        var rig = await Create();
        rig.Service.Hold = new TaskCompletionSource();
        var write = rig.Controller.SetVolumeAsync(0.3f, CancellationToken.None);
        await WaitFor(() => rig.Service.Writes == 1);

        rig.Service.RaiseExternal(new VolumeState(0.3f, false, CapabilityStatus.Supported)); // Windows reports while the call is still running
        rig.Service.Hold.SetResult();
        await write;

        Assert.Equal(VolumeChangeSource.Self, rig.Recorder.Snapshot()[1].Source);
    }

    [Fact]
    public async Task OutsideChangesNeverCauseWritesAndALongStormStaysQuiet()
    {
        var rig = await Create();

        for (var i = 0; i < 500; i++)
            rig.Service.RaiseExternal(new VolumeState((i % 100) / 100f, i % 2 == 0, CapabilityStatus.Supported));

        Assert.Equal(0, rig.Service.Writes);
        Assert.Equal(501, rig.Recorder.Snapshot().Length);
        Assert.All(rig.Recorder.Snapshot().Skip(1), s => Assert.Equal(VolumeChangeSource.External, s.Source));
    }

    [Fact]
    public async Task AnOutsideChangeWhileTheUserIsDraggingDoesNotCancelTheirPendingLevel()
    {
        var rig = await Create();
        rig.Service.Hold = new TaskCompletionSource();
        var first = rig.Controller.SetVolumeAsync(0.1f, CancellationToken.None);
        await WaitFor(() => rig.Service.Writes == 1);
        var newest = rig.Controller.SetVolumeAsync(0.9f, CancellationToken.None);

        rig.Service.RaiseExternal(new VolumeState(0.4f, false, CapabilityStatus.Supported)); // keyboard key mid-drag
        rig.Service.Hold.SetResult();
        await Task.WhenAll(first, newest);

        Assert.Equal(0.9f, rig.Service.State.Scalar); // the user's latest request is applied, the outside change is not re-applied
        Assert.Equal(2, rig.Service.Writes);
    }

    // ---- failure and rollback ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AFailedWriteIsReturnedAndFollowedByACorrectedSnapshot()
    {
        var rig = await Create();
        rig.Service.WriteFailures.Enqueue(new OperationError(ErrorCode.DeviceUnavailable, "No audio output device is available.", retryable: true));

        var result = await rig.Controller.SetVolumeAsync(0.9f, CancellationToken.None);

        Assert.Equal(ErrorCode.DeviceUnavailable, result.Error!.Code);
        var corrected = rig.Recorder.Snapshot()[^1];
        Assert.Equal(VolumeChangeSource.Corrected, corrected.Source);
        Assert.Equal(0.5f, corrected.State.Scalar); // what Windows really has
        Assert.Equal(0.5f, rig.Controller.Current!.Scalar);
    }

    [Fact]
    public async Task AFailedMuteRollsBackToo()
    {
        var rig = await Create();
        rig.Service.WriteFailures.Enqueue(new OperationError(ErrorCode.AccessDenied, "Windows did not allow the volume to be changed."));

        var result = await rig.Controller.SetMuteAsync(true, CancellationToken.None);

        Assert.Equal(ErrorCode.AccessDenied, result.Error!.Code);
        Assert.False(rig.Recorder.Snapshot()[^1].State.IsMuted);
        Assert.Equal(VolumeChangeSource.Corrected, rig.Recorder.Snapshot()[^1].Source);
    }

    [Fact]
    public async Task TheControllerKeepsWorkingAfterAFailure()
    {
        var rig = await Create();
        rig.Service.WriteFailures.Enqueue(new OperationError(ErrorCode.Unexpected, "The volume could not be read or changed.", retryable: true));
        await rig.Controller.SetVolumeAsync(0.9f, CancellationToken.None);

        var again = await rig.Controller.SetVolumeAsync(0.9f, CancellationToken.None);

        Assert.True(again.IsSuccess);
        Assert.Equal(0.9f, rig.Service.State.Scalar);
        Assert.Equal(VolumeChangeSource.Self, rig.Recorder.Snapshot()[^1].Source);
    }

    [Fact]
    public async Task AFailedWriteDoesNotLeaveAnEchoExpectationBehind()
    {
        var rig = await Create();
        rig.Service.WriteFailures.Enqueue(new OperationError(ErrorCode.Unexpected, "x", retryable: true));
        await rig.Controller.SetVolumeAsync(0.9f, CancellationToken.None);

        rig.Service.RaiseExternal(new VolumeState(0.9f, false, CapabilityStatus.Supported)); // someone else sets the value we failed to set

        Assert.Equal(VolumeChangeSource.External, rig.Recorder.Snapshot()[^1].Source);
    }

    // ---- cancellation -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACancelledTokenIsReportedAndNothingIsSent()
    {
        var rig = await Create();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Assert.True((await rig.Controller.SetVolumeAsync(0.5f, cts.Token)).IsCancelled);
        Assert.True((await rig.Controller.SetMuteAsync(true, cts.Token)).IsCancelled);
        Assert.True((await rig.Controller.ToggleMuteAsync(cts.Token)).IsCancelled);
        Assert.True((await rig.Controller.InitializeAsync(cts.Token)).IsCancelled);
        Assert.Equal(0, rig.Service.Writes);
    }

    [Fact]
    public async Task CancellingAQueuedRequestRemovesItAndItIsNeverSent()
    {
        var rig = await Create();
        rig.Service.Hold = new TaskCompletionSource();
        var first = rig.Controller.SetVolumeAsync(0.1f, CancellationToken.None);
        await WaitFor(() => rig.Service.Writes == 1);
        using var cts = new CancellationTokenSource();
        var queued = rig.Controller.SetVolumeAsync(0.9f, cts.Token);

        await cts.CancelAsync();
        Assert.True((await queued).IsCancelled);
        rig.Service.Hold.SetResult();
        await first;

        Assert.Equal(1, rig.Service.Writes);
        Assert.Equal(0.1f, rig.Service.State.Scalar);
    }

    [Fact]
    public async Task CancellingARequestThatWasReplacedChangesNothing()
    {
        var rig = await Create();
        rig.Service.Hold = new TaskCompletionSource();
        var first = rig.Controller.SetVolumeAsync(0.1f, CancellationToken.None);
        await WaitFor(() => rig.Service.Writes == 1);
        using var cts = new CancellationTokenSource();
        var older = rig.Controller.SetVolumeAsync(0.5f, cts.Token);
        var newer = rig.Controller.SetVolumeAsync(0.9f, CancellationToken.None);

        await cts.CancelAsync(); // too late: it was already replaced
        rig.Service.Hold.SetResult();
        await Task.WhenAll(first, older, newer);

        Assert.Equal(0.9f, rig.Service.State.Scalar);
        Assert.True((await older).IsSuccess);
    }

    [Fact]
    public async Task CancellingARequestAlreadyBeingSentCannotTakeItBack()
    {
        var rig = await Create();
        rig.Service.Hold = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();
        var running = rig.Controller.SetVolumeAsync(0.8f, cts.Token);
        await WaitFor(() => rig.Service.Writes == 1);

        await cts.CancelAsync();
        rig.Service.Hold.SetResult();

        Assert.True((await running).IsSuccess);
        Assert.Equal(0.8f, rig.Service.State.Scalar);
    }

    // ---- disposal ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DisposalUnsubscribesCompletesObserversAndDoesNotDisposeTheService()
    {
        var rig = await Create();

        await rig.Controller.DisposeAsync();
        await rig.Controller.DisposeAsync(); // idempotent

        Assert.Equal(0, rig.Service.ObserverCount);
        Assert.True(rig.Recorder.Completed);
        Assert.False(rig.Service.Disposed); // the container owns the service
        rig.Service.RaiseExternal(new VolumeState(0.1f, false, CapabilityStatus.Supported));
        Assert.Single(rig.Recorder.Snapshot()); // nothing after disposal
    }

    [Fact]
    public async Task AfterDisposalCallsFailAsClosed()
    {
        var rig = await Create();
        await rig.Controller.DisposeAsync();

        Assert.Equal(ErrorCode.DeviceUnavailable, (await rig.Controller.SetVolumeAsync(0.5f, CancellationToken.None)).Error!.Code);
        Assert.Equal(ErrorCode.DeviceUnavailable, (await rig.Controller.SetMuteAsync(true, CancellationToken.None)).Error!.Code);
        Assert.Equal(ErrorCode.DeviceUnavailable, (await rig.Controller.ToggleMuteAsync(CancellationToken.None)).Error!.Code);
        Assert.Equal(ErrorCode.DeviceUnavailable, (await rig.Controller.InitializeAsync(CancellationToken.None)).Error!.Code);
    }

    [Fact]
    public async Task DisposingWithWritesWaitingCancelsThemAndStopsTheWriter()
    {
        var rig = await Create();
        rig.Service.Hold = new TaskCompletionSource();
        var running = rig.Controller.SetVolumeAsync(0.1f, CancellationToken.None);
        await WaitFor(() => rig.Service.Writes == 1);
        var queued = rig.Controller.SetVolumeAsync(0.9f, CancellationToken.None);
        var queuedMute = rig.Controller.SetMuteAsync(true, CancellationToken.None);

        await rig.Controller.DisposeAsync();

        Assert.True((await queued).IsCancelled);
        Assert.True((await queuedMute).IsCancelled);
        Assert.True((await running).IsCancelled); // the in-flight write was cancelled through the controller's token
        Assert.Equal(1, rig.Service.Writes);
    }

    // ---- snapshots --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SnapshotsHaveNoReplayAndUnsubscribingIsIdempotent()
    {
        var rig = await Create();
        var late = new Recorder();

        var subscription = rig.Controller.Snapshots.Subscribe(late);
        Assert.Empty(late.Snapshot());
        subscription.Dispose();
        subscription.Dispose();
        rig.Service.RaiseExternal(new VolumeState(0.1f, false, CapabilityStatus.Supported));

        Assert.Empty(late.Snapshot());
    }

    // ---- arguments --------------------------------------------------------------------------------------------------

    [Fact]
    public void ArgumentsAreValidated()
    {
        var service = new ScriptedService();
        var clock = new FakeClock(Start);
        Assert.Throws<ArgumentNullException>(() => new VolumeController(null!, clock));
        Assert.Throws<ArgumentNullException>(() => new VolumeController(service, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VolumeController(service, clock, new VolumeControllerOptions { EchoTolerance = -1f }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VolumeController(service, clock, new VolumeControllerOptions { EchoTolerance = 0.9f }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VolumeController(service, clock, new VolumeControllerOptions { EchoWindow = TimeSpan.Zero }));
    }
}
