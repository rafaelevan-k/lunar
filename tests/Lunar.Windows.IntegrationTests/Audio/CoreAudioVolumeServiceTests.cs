using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Reactive;
using Lunar.Application.System.Models;
using Lunar.Domain.Shared;
using Lunar.Windows.Audio;
using Xunit;

namespace Lunar.Windows.IntegrationTests.Audio;

// The service logic against a fake audio backend: no sound hardware is touched, so device removal, driver errors,
// device changes and disposal can be simulated. The real device is exercised in CoreAudioRealDeviceTests.
public sealed class CoreAudioVolumeServiceTests
{
    internal sealed class FakeEndpoint : IAudioEndpoint
    {
        private readonly object _gate = new();
        private int _inside;

        public VolumeReading State { get; set; } = new(0.5f, false);

        public Queue<Exception> Failures { get; } = new();

        public List<string> Calls { get; } = [];

        public int MaxConcurrent { get; private set; }

        public bool Disposed { get; private set; }

        public TimeSpan Delay { get; set; }

        public event Action<VolumeReading>? Changed;

        public VolumeReading Read() => Enter("read", () => State);

        public void SetVolume(float scalar) => Enter("volume " + scalar.ToString("0.00"), () =>
        {
            State = State with { Scalar = scalar };
            return 0;
        });

        public void SetMute(bool muted) => Enter("mute " + muted, () =>
        {
            State = State with { IsMuted = muted };
            return 0;
        });

        public void Raise(VolumeReading reading)
        {
            State = reading;
            Changed?.Invoke(reading);
        }

        public bool HasSubscribers => Changed is not null;

        public void Dispose() => Disposed = true;

        private T Enter<T>(string call, Func<T> body)
        {
            lock (_gate)
            {
                _inside++;
                MaxConcurrent = Math.Max(MaxConcurrent, _inside);
                Calls.Add(call);
            }

            try
            {
                if (Delay > TimeSpan.Zero)
                    Thread.Sleep(Delay);
                if (Failures.TryDequeue(out var failure))
                    throw failure;
                return body();
            }
            finally
            {
                lock (_gate)
                    _inside--;
            }
        }
    }

    internal sealed class FakeBackend : IAudioBackend
    {
        public List<FakeEndpoint> Opened { get; } = [];

        public Func<FakeEndpoint?> Next { get; set; } = () => new FakeEndpoint();

        public Queue<Exception> OpenFailures { get; } = new();

        public bool Disposed { get; private set; }

        public int OpenCalls { get; private set; }

        public event Action? DefaultDeviceChanged;

        public IAudioEndpoint? OpenDefault()
        {
            OpenCalls++;
            if (OpenFailures.TryDequeue(out var failure))
                throw failure;
            var endpoint = Next();
            if (endpoint is not null)
                Opened.Add(endpoint);
            return endpoint;
        }

        public void RaiseDefaultChanged() => DefaultDeviceChanged?.Invoke();

        public bool HasSubscribers => DefaultDeviceChanged is not null;

        public void Dispose() => Disposed = true;
    }

    private sealed class Recorder : IObserver<VolumeState>
    {
        private readonly object _gate = new();

        public List<VolumeState> Values { get; } = [];

        public bool Completed { get; private set; }

        public void OnNext(VolumeState value)
        {
            lock (_gate)
                Values.Add(value);
        }

        public void OnError(Exception error) => throw new InvalidOperationException("OnError must never be called.");

        public void OnCompleted() => Completed = true;

        public VolumeState[] Snapshot()
        {
            lock (_gate)
                return [.. Values];
        }
    }

    private sealed class CollectingLog : IDiagnosticLog
    {
        public List<DiagnosticEvent> Events { get; } = [];

        public void Write(DiagnosticEvent diagnosticEvent)
        {
            lock (Events)
                Events.Add(diagnosticEvent);
        }
    }

    private static AudioException Gone() => new(AudioFailure.DeviceGone, unchecked((int)0x88890004), "gone");

    private static AudioException Denied() => new(AudioFailure.AccessDenied, unchecked((int)0x80070005), "denied");

    private static AudioException Other() => new(AudioFailure.Other, unchecked((int)0x80004005), "other");

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 3000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < end)
            await Task.Delay(5);
        Assert.True(condition(), "The condition was not met in time.");
    }

    // ---- reading ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ReadingReturnsTheEndpointStateAsSupported()
    {
        var backend = new FakeBackend { Next = () => new FakeEndpoint { State = new VolumeReading(0.37f, true) } };
        await using var service = new CoreAudioVolumeService(backend);

        var result = await service.GetAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(new VolumeState(0.37f, true, CapabilityStatus.Supported), result.Value);
    }

    [Fact]
    public async Task TheEndpointIsOpenedOnceAndReused()
    {
        var backend = new FakeBackend();
        await using var service = new CoreAudioVolumeService(backend);

        for (var i = 0; i < 5; i++)
            await service.GetAsync(CancellationToken.None);

        Assert.Equal(1, backend.OpenCalls);
    }

    [Fact]
    public async Task NoOutputDeviceIsAnUnavailableStateNotAnError()
    {
        var backend = new FakeBackend { Next = () => null };
        await using var service = new CoreAudioVolumeService(backend);

        var result = await service.GetAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(VolumeState.Unavailable, result.Value);
        Assert.Equal(CapabilityStatus.Unavailable, result.Value.Capability);
    }

    [Fact]
    public async Task WritingWithoutADeviceFailsAsDeviceUnavailableAndRetryable()
    {
        var backend = new FakeBackend { Next = () => null };
        await using var service = new CoreAudioVolumeService(backend);

        var volume = await service.SetVolumeAsync(0.4f, CancellationToken.None);
        var mute = await service.SetMuteAsync(true, CancellationToken.None);

        Assert.Equal(ErrorCode.DeviceUnavailable, volume.Error!.Code);
        Assert.True(volume.Error.Retryable);
        Assert.Equal(ErrorCode.DeviceUnavailable, mute.Error!.Code);
    }

    [Fact]
    public async Task ADeviceThatAppearsLaterIsPickedUpOnTheNextCall()
    {
        FakeEndpoint? endpoint = null;
        var backend = new FakeBackend { Next = () => endpoint };
        await using var service = new CoreAudioVolumeService(backend);
        Assert.Equal(VolumeState.Unavailable, (await service.GetAsync(CancellationToken.None)).Value);

        endpoint = new FakeEndpoint { State = new VolumeReading(0.9f, false) };

        Assert.Equal(0.9f, (await service.GetAsync(CancellationToken.None)).Value.Scalar);
    }

    // ---- writing ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SettingTheVolumeAndMuteReachesTheEndpointWithTheExactValues()
    {
        var backend = new FakeBackend();
        await using var service = new CoreAudioVolumeService(backend);

        Assert.True((await service.SetVolumeAsync(0.25f, CancellationToken.None)).IsSuccess);
        Assert.True((await service.SetMuteAsync(true, CancellationToken.None)).IsSuccess);

        var endpoint = Assert.Single(backend.Opened);
        Assert.Equal(["volume 0.25", "mute True"], endpoint.Calls);
        Assert.Equal(new VolumeReading(0.25f, true), endpoint.State);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    [InlineData(0.5f)]
    public async Task TheBoundariesOfTheRangeAreAccepted(float value)
    {
        await using var service = new CoreAudioVolumeService(new FakeBackend());

        Assert.True((await service.SetVolumeAsync(value, CancellationToken.None)).IsSuccess);
    }

    [Theory]
    [InlineData(-0.01f)]
    [InlineData(1.01f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public async Task OutOfRangeValuesAreRejectedWithoutTouchingTheDevice(float value)
    {
        var backend = new FakeBackend();
        await using var service = new CoreAudioVolumeService(backend);

        var result = await service.SetVolumeAsync(value, CancellationToken.None);

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.Equal(0, backend.OpenCalls);
    }

    [Fact]
    public async Task ACancelledTokenReportsCancellationWithoutTouchingTheDevice()
    {
        var backend = new FakeBackend();
        await using var service = new CoreAudioVolumeService(backend);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Assert.True((await service.GetAsync(cts.Token)).IsCancelled);
        Assert.True((await service.SetVolumeAsync(0.5f, cts.Token)).IsCancelled);
        Assert.True((await service.SetMuteAsync(true, cts.Token)).IsCancelled);
        Assert.Equal(0, backend.OpenCalls);
    }

    [Fact]
    public async Task CancellingWhileWaitingForABusyDeviceReturnsCancelledPromptly()
    {
        var endpoint = new FakeEndpoint { Delay = TimeSpan.FromMilliseconds(300) };
        await using var service = new CoreAudioVolumeService(new FakeBackend { Next = () => endpoint });
        var slow = service.SetVolumeAsync(0.1f, CancellationToken.None);
        await WaitFor(() => endpoint.Calls.Count == 1);
        using var cts = new CancellationTokenSource(50);

        var waiting = await service.GetAsync(cts.Token); // queued behind the slow call

        Assert.True(waiting.IsCancelled);
        Assert.True((await slow).IsSuccess);
        Assert.Single(endpoint.Calls); // the cancelled read never reached the device
    }

    [Fact]
    public async Task CallsAreSerializedSoTheDeviceNeverSeesTwoAtOnce()
    {
        var endpoint = new FakeEndpoint { Delay = TimeSpan.FromMilliseconds(5) };
        await using var service = new CoreAudioVolumeService(new FakeBackend { Next = () => endpoint });

        var tasks = Enumerable.Range(0, 40).Select(i => i % 3 == 0
            ? service.SetVolumeAsync(i / 100f, CancellationToken.None)
            : i % 3 == 1 ? service.SetMuteAsync(i % 2 == 0, CancellationToken.None) : (Task)service.GetAsync(CancellationToken.None)).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(1, endpoint.MaxConcurrent);
        Assert.Equal(40, endpoint.Calls.Count);
    }

    // ---- failures -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ADeviceThatWentAwayIsReopenedAndTheCallRetriedOnce()
    {
        var first = new FakeEndpoint();
        first.Failures.Enqueue(Gone());
        var second = new FakeEndpoint { State = new VolumeReading(0.8f, false) };
        var queue = new Queue<FakeEndpoint>([first, second]);
        var backend = new FakeBackend { Next = () => queue.Dequeue() };
        await using var service = new CoreAudioVolumeService(backend);

        var result = await service.GetAsync(CancellationToken.None);

        Assert.Equal(0.8f, result.Value.Scalar);
        Assert.True(first.Disposed);
        Assert.False(second.Disposed);
        Assert.Equal(2, backend.OpenCalls);
    }

    [Fact]
    public async Task ADeviceThatStaysGoneIsReportedAsNoDeviceAndLoggedByTypeOnly()
    {
        var log = new CollectingLog();
        var backend = new FakeBackend
        {
            Next = () =>
            {
                var e = new FakeEndpoint();
                e.Failures.Enqueue(Gone());
                return e;
            }
        };
        await using var service = new CoreAudioVolumeService(backend, log);

        var read = await service.GetAsync(CancellationToken.None);
        var write = await service.SetVolumeAsync(0.5f, CancellationToken.None);

        Assert.Equal(VolumeState.Unavailable, read.Value);
        Assert.Equal(ErrorCode.DeviceUnavailable, write.Error!.Code);
        Assert.All(backend.Opened, endpoint => Assert.True(endpoint.Disposed));
        Assert.All(log.Events, e => Assert.Equal(typeof(AudioException), e.ExceptionType));
    }

    [Fact]
    public async Task OpenFailuresAreHandledTheSameWay()
    {
        var backend = new FakeBackend();
        backend.OpenFailures.Enqueue(Gone());
        backend.OpenFailures.Enqueue(Gone());
        await using var service = new CoreAudioVolumeService(backend);

        var result = await service.SetMuteAsync(true, CancellationToken.None);

        Assert.Equal(ErrorCode.DeviceUnavailable, result.Error!.Code);
        Assert.True((await service.SetMuteAsync(true, CancellationToken.None)).IsSuccess); // the next call works
    }

    [Fact]
    public async Task WindowsRefusingACallIsAccessDeniedAndTheEndpointIsKept()
    {
        var endpoint = new FakeEndpoint();
        endpoint.Failures.Enqueue(Denied());
        var backend = new FakeBackend { Next = () => endpoint };
        await using var service = new CoreAudioVolumeService(backend);

        var denied = await service.SetVolumeAsync(0.3f, CancellationToken.None);

        Assert.Equal(ErrorCode.AccessDenied, denied.Error!.Code);
        Assert.False(endpoint.Disposed);
        Assert.True((await service.SetVolumeAsync(0.3f, CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task AnUnknownDriverErrorIsUnexpectedAndRetryableAndTheEndpointIsReopenedNextTime()
    {
        var first = new FakeEndpoint();
        first.Failures.Enqueue(Other());
        var queue = new Queue<FakeEndpoint>([first, new FakeEndpoint()]);
        var backend = new FakeBackend { Next = () => queue.Dequeue() };
        await using var service = new CoreAudioVolumeService(backend);

        var failed = await service.SetVolumeAsync(0.3f, CancellationToken.None);

        Assert.Equal(ErrorCode.Unexpected, failed.Error!.Code);
        Assert.True(failed.Error.Retryable);
        Assert.True(first.Disposed);
        Assert.True((await service.SetVolumeAsync(0.3f, CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task ARefusedReadIsAnErrorNotAnUnavailableState()
    {
        var endpoint = new FakeEndpoint();
        endpoint.Failures.Enqueue(Denied());
        await using var service = new CoreAudioVolumeService(new FakeBackend { Next = () => endpoint });

        var result = await service.GetAsync(CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCode.AccessDenied, result.Error!.Code);
    }

    [Fact]
    public async Task WhenCoreAudioCannotStartAtAllTheServiceActsAsNoDevice()
    {
        await using var service = new CoreAudioVolumeService(new FakeBackend { Next = () => null });

        Assert.Equal(VolumeState.Unavailable, (await service.GetAsync(CancellationToken.None)).Value);
    }

    // ---- change notifications -----------------------------------------------------------------------------------------

    [Fact]
    public async Task ExternalChangesArePublishedOnceEachAndRepeatsAreSuppressed()
    {
        var endpoint = new FakeEndpoint();
        await using var service = new CoreAudioVolumeService(new FakeBackend { Next = () => endpoint });
        await service.GetAsync(CancellationToken.None); // opens the endpoint
        var recorder = new Recorder();
        using var subscription = service.Changes.Subscribe(recorder);

        endpoint.Raise(new VolumeReading(0.7f, false));
        endpoint.Raise(new VolumeReading(0.7f, false)); // the same again
        endpoint.Raise(new VolumeReading(0.7f, true));

        Assert.Equal(
            [new VolumeState(0.7f, false, CapabilityStatus.Supported), new VolumeState(0.7f, true, CapabilityStatus.Supported)],
            recorder.Snapshot());
    }

    [Fact]
    public async Task ThereIsNoReplayAndUnsubscribingIsIdempotent()
    {
        var endpoint = new FakeEndpoint();
        await using var service = new CoreAudioVolumeService(new FakeBackend { Next = () => endpoint });
        await service.GetAsync(CancellationToken.None);
        var recorder = new Recorder();

        var subscription = service.Changes.Subscribe(recorder);
        Assert.Empty(recorder.Snapshot()); // no replay of the earlier read
        subscription.Dispose();
        subscription.Dispose();
        endpoint.Raise(new VolumeReading(0.1f, false));

        Assert.Empty(recorder.Snapshot());
    }

    [Fact]
    public async Task ADefaultDeviceChangeSwitchesToTheNewDeviceAndPublishesItsState()
    {
        var first = new FakeEndpoint { State = new VolumeReading(0.2f, false) };
        var second = new FakeEndpoint { State = new VolumeReading(0.6f, true) };
        var queue = new Queue<FakeEndpoint>([first, second]);
        var backend = new FakeBackend { Next = () => queue.Dequeue() };
        await using var service = new CoreAudioVolumeService(backend);
        await service.GetAsync(CancellationToken.None);
        var recorder = new Recorder();
        using var subscription = service.Changes.Subscribe(recorder);

        backend.RaiseDefaultChanged();

        await WaitFor(() => recorder.Snapshot().Length == 1);
        Assert.Equal(new VolumeState(0.6f, true, CapabilityStatus.Supported), recorder.Snapshot()[0]);
        Assert.True(first.Disposed);
        Assert.Equal(0.6f, (await service.GetAsync(CancellationToken.None)).Value.Scalar);
        second.Raise(new VolumeReading(0.65f, true)); // the new endpoint is the one being listened to
        Assert.Equal(2, recorder.Snapshot().Length);
        first.Raise(new VolumeReading(0.99f, false)); // the old one is not
        Assert.Equal(2, recorder.Snapshot().Length);
    }

    [Fact]
    public async Task RemovingTheLastDevicePublishesUnavailableAndAReturningDevicePublishesItsState()
    {
        FakeEndpoint? current = new FakeEndpoint { State = new VolumeReading(0.4f, false) };
        var backend = new FakeBackend { Next = () => current };
        await using var service = new CoreAudioVolumeService(backend);
        await service.GetAsync(CancellationToken.None);
        var recorder = new Recorder();
        using var subscription = service.Changes.Subscribe(recorder);

        current = null;
        backend.RaiseDefaultChanged();
        await WaitFor(() => recorder.Snapshot().Length == 1);
        Assert.Equal(VolumeState.Unavailable, recorder.Snapshot()[0]);

        current = new FakeEndpoint { State = new VolumeReading(0.8f, false) };
        backend.RaiseDefaultChanged();
        await WaitFor(() => recorder.Snapshot().Length == 2);
        Assert.Equal(0.8f, recorder.Snapshot()[1].Scalar);
    }

    [Fact]
    public async Task ABurstOfDeviceChangesIsCoalescedIntoFewRefreshes()
    {
        var backend = new FakeBackend();
        await using var service = new CoreAudioVolumeService(backend);
        await service.GetAsync(CancellationToken.None);

        for (var i = 0; i < 50; i++)
            backend.RaiseDefaultChanged();

        await WaitFor(() => backend.OpenCalls >= 2);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.InRange(backend.OpenCalls, 2, 52); // never an unbounded number, and at least one refresh happened
        Assert.All(backend.Opened.SkipLast(1), endpoint => Assert.True(endpoint.Disposed)); // only the newest stays open
    }

    // ---- disposal -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DisposalReleasesTheEndpointAndBackendCompletesObserversAndUnsubscribes()
    {
        var endpoint = new FakeEndpoint();
        var backend = new FakeBackend { Next = () => endpoint };
        var service = new CoreAudioVolumeService(backend);
        await service.GetAsync(CancellationToken.None);
        var recorder = new Recorder();
        service.Changes.Subscribe(recorder);

        await service.DisposeAsync();

        Assert.True(endpoint.Disposed);
        Assert.True(backend.Disposed);
        Assert.True(recorder.Completed);
        Assert.False(backend.HasSubscribers);
        Assert.False(endpoint.HasSubscribers);
        endpoint.Raise(new VolumeReading(0.9f, false)); // a late callback does nothing
        Assert.Empty(recorder.Snapshot());
    }

    [Fact]
    public async Task DisposalIsIdempotentAndLaterCallsFailAsClosed()
    {
        var service = new CoreAudioVolumeService(new FakeBackend());

        await service.DisposeAsync();
        await service.DisposeAsync();

        Assert.Equal(ErrorCode.DeviceUnavailable, (await service.GetAsync(CancellationToken.None)).Error!.Code);
        Assert.Equal(ErrorCode.DeviceUnavailable, (await service.SetVolumeAsync(0.5f, CancellationToken.None)).Error!.Code);
        Assert.Equal(ErrorCode.DeviceUnavailable, (await service.SetMuteAsync(true, CancellationToken.None)).Error!.Code);
        var late = new Recorder();
        service.Changes.Subscribe(late);
        Assert.True(late.Completed); // subscribing after disposal completes at once
    }

    [Fact]
    public async Task SynchronousDisposalDoesTheSameTeardownAndMixesSafelyWithAsyncDisposal()
    {
        var endpoint = new FakeEndpoint();
        var backend = new FakeBackend { Next = () => endpoint };
        var service = new CoreAudioVolumeService(backend);
        await service.GetAsync(CancellationToken.None);
        var recorder = new Recorder();
        service.Changes.Subscribe(recorder);

        service.Dispose();
        service.Dispose();
        await service.DisposeAsync();

        Assert.True(endpoint.Disposed);
        Assert.True(backend.Disposed);
        Assert.True(recorder.Completed);
        Assert.False(backend.HasSubscribers);
    }

    [Fact]
    public async Task DisposingWhileACallIsRunningWaitsForItAndNeverLeavesTheEndpointOpen()
    {
        var endpoint = new FakeEndpoint { Delay = TimeSpan.FromMilliseconds(200) };
        var service = new CoreAudioVolumeService(new FakeBackend { Next = () => endpoint });
        var running = service.SetVolumeAsync(0.5f, CancellationToken.None);
        await WaitFor(() => endpoint.Calls.Count == 1);

        await service.DisposeAsync();

        Assert.True(endpoint.Disposed);
        Assert.True((await running).IsSuccess || (await running).IsFailure); // it finished, whatever the outcome
    }

    [Fact]
    public async Task DisposingDuringADeviceChangeRefreshDoesNotThrowOrLeak()
    {
        for (var round = 0; round < 25; round++)
        {
            var backend = new FakeBackend();
            var service = new CoreAudioVolumeService(backend);
            await service.GetAsync(CancellationToken.None);

            backend.RaiseDefaultChanged();
            backend.RaiseDefaultChanged();
            await service.DisposeAsync();
            await Task.Delay(5, TestContext.Current.CancellationToken);

            Assert.True(backend.Disposed);
            Assert.All(backend.Opened, endpoint => Assert.True(endpoint.Disposed));
        }
    }

    [Fact]
    public async Task ANullBackendIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new CoreAudioVolumeService((IAudioBackend)null!));
        await Task.CompletedTask;
    }

    // ---- the observable helper ----------------------------------------------------------------------------------------

    [Fact]
    public void ABrokenObserverDoesNotStopTheOthersOrTheProducer()
    {
        var broadcaster = new StateBroadcaster<int>();
        var seen = new List<int>();
        broadcaster.Subscribe(new LambdaObserver<int>(_ => throw new InvalidOperationException("bad observer")));
        broadcaster.Subscribe(new LambdaObserver<int>(seen.Add));

        broadcaster.Publish(1);
        broadcaster.Publish(2);

        Assert.Equal([1, 2], seen);
        Assert.Equal(2, broadcaster.ObserverCount);
        broadcaster.Complete();
        broadcaster.Complete();
        broadcaster.Publish(3);
        Assert.Equal([1, 2], seen);
        Assert.Equal(0, broadcaster.ObserverCount);
    }

    private sealed class LambdaObserver<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnNext(T value) => onNext(value);

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }
}
