using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.System.Models;
using Lunar.Domain.Shared;
using Lunar.Windows.Network;
using Lunar.Windows.Shared;
using Xunit;

namespace Lunar.Windows.IntegrationTests.Network;

// Everything here uses a fake Native Wi-Fi backend: nothing touches the machine's real Wi-Fi.
public sealed class NativeWifiServiceTests
{
    internal sealed class FakeBackend : IWlanBackend
    {
        private readonly object _gate = new();

        public List<WlanInterfaceReading> Adapters { get; set; } = [];

        public WlanException? ReadError { get; set; }

        public WlanException? WriteError { get; set; }

        /// <summary>When true a successful write changes the software switch of every PHY, after <see cref="ReadsBeforeApplied"/> reads.</summary>
        public bool ApplyWrites { get; set; } = true;

        public int ReadsBeforeApplied { get; set; }

        public int Reads { get; private set; }

        public List<bool> Writes { get; } = [];

        public bool Disposed { get; private set; }

        public bool HasSubscribers => Changed is not null;

        private bool? _pending;
        private int _readsSincePending;

        public event Action? Changed;

        public IReadOnlyList<WlanInterfaceReading> Read()
        {
            lock (_gate)
            {
                Reads++;
                if (ReadError is not null)
                    throw ReadError;
                if (_pending is { } on && ++_readsSincePending > ReadsBeforeApplied)
                {
                    Adapters = [.. Adapters.Select(a => a with { Phys = a.Phys?.Select(p => p with { Software = on ? RadioSwitch.On : RadioSwitch.Off }).ToList() })];
                    _pending = null;
                }

                return [.. Adapters];
            }
        }

        public void SetSoftwareRadio(bool on)
        {
            lock (_gate)
            {
                if (WriteError is not null)
                    throw WriteError;
                Writes.Add(on);
                if (ApplyWrites)
                {
                    _pending = on;
                    _readsSincePending = 0;
                }
            }
        }

        public void Raise() => Changed?.Invoke();

        public void Dispose() => Disposed = true;
    }

    private sealed class Recorder : IObserver<WifiState>
    {
        private readonly object _gate = new();

        public List<WifiState> Values { get; } = [];

        public bool Completed { get; private set; }

        public void OnNext(WifiState value)
        {
            lock (_gate)
                Values.Add(value);
        }

        public void OnError(Exception error) => throw new InvalidOperationException("OnError must never be called.");

        public void OnCompleted() => Completed = true;

        public WifiState[] Snapshot()
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

    private static readonly RadioConfirmOptions NoWait = new() { Delay = (_, _) => Task.CompletedTask };

    private static PhyRadio On => new(RadioSwitch.On, RadioSwitch.On);

    private static PhyRadio SoftOff => new(RadioSwitch.Off, RadioSwitch.On);

    private static PhyRadio HardOff => new(RadioSwitch.On, RadioSwitch.Off);

    private static WlanInterfaceReading Adapter(WlanInterfaceState state, params PhyRadio[] phys) => new(state, phys);

    private static NativeWifiService Create(FakeBackend backend, IDiagnosticLog? log = null, RadioConfirmOptions? options = null) =>
        new(backend, log, options ?? NoWait);

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 3000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < end)
            await Task.Delay(5, TestContext.Current.CancellationToken);
        Assert.True(condition(), "Condition not reached in time.");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- reading -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ConnectedAdapterReadsAsEnabledAndConnectedAndSwitchable()
    {
        var backend = new FakeBackend { Adapters = [Adapter(WlanInterfaceState.Connected, On)] };
        await using var service = Create(backend);

        var state = (await service.GetAsync(Ct)).Value;

        Assert.Equal(new WifiState(CapabilityStatus.Supported, RadioState.Enabled, WifiConnectivity.Connected, true), state);
    }

    [Fact]
    public async Task EnabledRadioAloneNeverReadsAsConnected()
    {
        var backend = new FakeBackend { Adapters = [Adapter(WlanInterfaceState.Disconnected, On)] };
        await using var service = Create(backend);

        var state = (await service.GetAsync(Ct)).Value;

        Assert.Equal(RadioState.Enabled, state.Radio);
        Assert.Equal(WifiConnectivity.Disconnected, state.Connectivity);
    }

    [Fact]
    public async Task SoftwareOffReadsAsDisabledButSwitchable()
    {
        var backend = new FakeBackend { Adapters = [Adapter(WlanInterfaceState.NotReady, SoftOff)] };
        await using var service = Create(backend);

        var state = (await service.GetAsync(Ct)).Value;

        Assert.Equal(new WifiState(CapabilityStatus.Supported, RadioState.Disabled, WifiConnectivity.Disconnected, true), state);
    }

    [Fact]
    public async Task HardwareSwitchOffReadsAsDisabledAndNotSwitchable()
    {
        var backend = new FakeBackend { Adapters = [Adapter(WlanInterfaceState.NotReady, HardOff)] };
        await using var service = Create(backend);

        var state = (await service.GetAsync(Ct)).Value;

        Assert.Equal(RadioState.Disabled, state.Radio);
        Assert.False(state.CanToggle);
    }

    [Fact]
    public async Task NoAdapterIsAStateNotAnError()
    {
        await using var service = Create(new FakeBackend());

        var read = await service.GetAsync(Ct);

        Assert.True(read.IsSuccess);
        Assert.Equal(CapabilityStatus.Unavailable, read.Value.Capability);
        Assert.Equal(RadioState.Unavailable, read.Value.Radio);
        Assert.False(read.Value.CanToggle);
    }

    [Theory]
    [InlineData("ServiceUnavailable", CapabilityStatus.Unavailable)]
    [InlineData("NotSupported", CapabilityStatus.Unsupported)]
    [InlineData("AccessDenied", CapabilityStatus.PermissionDenied)]
    public async Task WindowsRefusalsAreDistinguishableStates(string failure, CapabilityStatus expected)
    {
        var backend = new FakeBackend { ReadError = new WlanException(Enum.Parse<WlanFailure>(failure), 1, "x") };
        await using var service = Create(backend);

        var read = await service.GetAsync(Ct);

        Assert.True(read.IsSuccess);
        Assert.Equal(expected, read.Value.Capability);
        Assert.False(read.Value.CanToggle);
    }

    [Fact]
    public async Task UnexpectedFailureIsATypedRetryableError()
    {
        var backend = new FakeBackend { ReadError = new WlanException(WlanFailure.Other, 31, "x") };
        await using var service = Create(backend);

        var read = await service.GetAsync(Ct);

        Assert.True(read.IsFailure);
        Assert.Equal(ErrorCode.Unexpected, read.Error!.Code);
        Assert.True(read.Error.Retryable);
    }

    [Fact]
    public async Task ServiceComingBackIsNoticedByTheNextRead()
    {
        var backend = new FakeBackend { ReadError = new WlanException(WlanFailure.ServiceUnavailable, 1062, "stopped") };
        await using var service = Create(backend);
        Assert.Equal(CapabilityStatus.Unavailable, (await service.GetAsync(Ct)).Value.Capability);

        backend.ReadError = null;
        backend.Adapters = [Adapter(WlanInterfaceState.Connected, On)];

        Assert.Equal(CapabilityStatus.Supported, (await service.GetAsync(Ct)).Value.Capability);
    }

    [Fact]
    public async Task CancelledReadDoesNotTouchWindows()
    {
        var backend = new FakeBackend { Adapters = [Adapter(WlanInterfaceState.Connected, On)] };
        await using var service = Create(backend);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var read = await service.GetAsync(cts.Token);

        Assert.True(read.IsCancelled);
        Assert.Equal(0, backend.Reads);
    }

    [Fact]
    public async Task ReadAfterDisposeFailsAsClosed()
    {
        var service = Create(new FakeBackend());
        await service.DisposeAsync();

        var read = await service.GetAsync(Ct);
        var write = await service.SetEnabledAsync(true, Ct);

        Assert.Equal(ErrorCode.DeviceUnavailable, read.Error!.Code);
        Assert.Equal(ErrorCode.DeviceUnavailable, write.Error!.Code);
    }

    // ---- notifications -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task OsNotificationsArePublishedAsChangesWithoutRepeats()
    {
        var backend = new FakeBackend { Adapters = [Adapter(WlanInterfaceState.Disconnected, On)] };
        await using var service = Create(backend);
        var recorder = new Recorder();
        using var subscription = service.Changes.Subscribe(recorder);
        _ = await service.GetAsync(Ct);

        backend.Raise(); // nothing changed: no new state
        backend.Adapters = [Adapter(WlanInterfaceState.Connected, On)];
        backend.Raise();
        await WaitFor(() => recorder.Snapshot().Length >= 2);
        backend.Raise();
        await Task.Delay(100, Ct);

        var seen = recorder.Snapshot();
        Assert.Equal(2, seen.Length);
        Assert.Equal(WifiConnectivity.Disconnected, seen[0].Connectivity);
        Assert.Equal(WifiConnectivity.Connected, seen[1].Connectivity);
    }

    [Fact]
    public async Task AdapterRemovalAndArrivalAreReported()
    {
        var backend = new FakeBackend { Adapters = [Adapter(WlanInterfaceState.Connected, On)] };
        await using var service = Create(backend);
        var recorder = new Recorder();
        using var subscription = service.Changes.Subscribe(recorder);
        _ = await service.GetAsync(Ct);

        backend.Adapters = [];
        backend.Raise();
        await WaitFor(() => recorder.Snapshot().Length >= 2);
        backend.Adapters = [Adapter(WlanInterfaceState.Connected, On)];
        backend.Raise();
        await WaitFor(() => recorder.Snapshot().Length >= 3);

        var seen = recorder.Snapshot();
        Assert.Equal(CapabilityStatus.Unavailable, seen[1].Capability);
        Assert.Equal(CapabilityStatus.Supported, seen[2].Capability);
    }

    [Fact]
    public async Task ABurstOfNotificationsCausesFewReads()
    {
        var backend = new FakeBackend { Adapters = [Adapter(WlanInterfaceState.Connected, On)] };
        await using var service = Create(backend);
        _ = await service.GetAsync(Ct);
        var before = backend.Reads;

        Parallel.For(0, 500, _ => backend.Raise());
        await Task.Delay(300, Ct);

        Assert.InRange(backend.Reads - before, 1, 50);
    }

    [Fact]
    public async Task NotificationAfterDisposeIsIgnored()
    {
        var backend = new FakeBackend { Adapters = [Adapter(WlanInterfaceState.Connected, On)] };
        var service = Create(backend);
        await service.DisposeAsync();
        var reads = backend.Reads;

        backend.Raise();
        await Task.Delay(50, Ct);

        Assert.Equal(reads, backend.Reads);
        Assert.False(backend.HasSubscribers);
    }

    // ---- toggling ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TurningOnWritesAndSucceedsOnlyOnceTheRadioIsSeenOn()
    {
        var backend = new FakeBackend { Adapters = [Adapter(WlanInterfaceState.NotReady, SoftOff)], ReadsBeforeApplied = 3 };
        await using var service = Create(backend);

        var result = await service.SetEnabledAsync(true, Ct);

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal([true], backend.Writes);
        Assert.Equal(RadioState.Enabled, (await service.GetAsync(Ct)).Value.Radio);
    }

    [Fact]
    public async Task TurningOffWorksTheSameWay()
    {
        var backend = new FakeBackend { Adapters = [Adapter(WlanInterfaceState.Connected, On)] };
        await using var service = Create(backend);

        var result = await service.SetEnabledAsync(false, Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal([false], backend.Writes);
        Assert.Equal(RadioState.Disabled, (await service.GetAsync(Ct)).Value.Radio);
    }

    [Fact]
    public async Task AskingForTheCurrentStateWritesNothing()
    {
        var backend = new FakeBackend { Adapters = [Adapter(WlanInterfaceState.Connected, On)] };
        await using var service = Create(backend);

        var result = await service.SetEnabledAsync(true, Ct);

        Assert.True(result.IsSuccess);
        Assert.Empty(backend.Writes);
    }

    [Fact]
    public async Task AcceptedWriteThatNeverTakesEffectTimesOutInsteadOfClaimingSuccess()
    {
        var backend = new FakeBackend { Adapters = [Adapter(WlanInterfaceState.NotReady, SoftOff)], ApplyWrites = false };
        await using var service = Create(backend, options: new RadioConfirmOptions { ConfirmAttempts = 4, Delay = (_, _) => Task.CompletedTask });

        var result = await service.SetEnabledAsync(true, Ct);

        Assert.Equal(ErrorCode.Timeout, result.Error!.Code);
        Assert.True(result.Error.Retryable);
        Assert.InRange(backend.Reads, 5, 6); // the read before the write plus the bounded confirmation reads
    }

    [Fact]
    public async Task HardwareSwitchOffRefusesWithoutWriting()
    {
        var backend = new FakeBackend { Adapters = [Adapter(WlanInterfaceState.NotReady, HardOff)] };
        await using var service = Create(backend);

        var result = await service.SetEnabledAsync(true, Ct);

        Assert.Equal(ErrorCode.Unsupported, result.Error!.Code);
        Assert.Empty(backend.Writes);
    }

    [Fact]
    public async Task NoAdapterRefusesToggleAsDeviceUnavailable()
    {
        var backend = new FakeBackend();
        await using var service = Create(backend);

        var result = await service.SetEnabledAsync(true, Ct);

        Assert.Equal(ErrorCode.DeviceUnavailable, result.Error!.Code);
        Assert.Empty(backend.Writes);
    }

    [Fact]
    public async Task DriverThatDoesNotReportItsRadioCannotBeToggled()
    {
        var backend = new FakeBackend { Adapters = [new WlanInterfaceReading(WlanInterfaceState.Disconnected, null)] };
        await using var service = Create(backend);

        var state = (await service.GetAsync(Ct)).Value;
        var result = await service.SetEnabledAsync(true, Ct);

        Assert.Equal(RadioState.Unknown, state.Radio);
        Assert.False(state.CanToggle);
        Assert.Equal(ErrorCode.Unsupported, result.Error!.Code);
        Assert.Empty(backend.Writes);
    }

    [Fact]
    public async Task RefusedWriteIsAccessDeniedAndTheSwitchIsNotOfferedAgain()
    {
        var backend = new FakeBackend
        {
            Adapters = [Adapter(WlanInterfaceState.Connected, On)],
            WriteError = new WlanException(WlanFailure.AccessDenied, 5, "denied")
        };
        await using var service = Create(backend);
        var recorder = new Recorder();
        using var subscription = service.Changes.Subscribe(recorder);
        Assert.True((await service.GetAsync(Ct)).Value.CanToggle);

        var first = await service.SetEnabledAsync(false, Ct);
        var second = await service.SetEnabledAsync(false, Ct);

        Assert.Equal(ErrorCode.AccessDenied, first.Error!.Code);
        Assert.Equal(ErrorCode.Unsupported, second.Error!.Code); // no second attempt at Windows
        Assert.False((await service.GetAsync(Ct)).Value.CanToggle);
        Assert.False(recorder.Snapshot().Last().CanToggle);
        Assert.Empty(backend.Writes);
    }

    [Fact]
    public async Task AdapterThatCannotBeSwitchedIsUnsupportedNotAnUnexpectedFailure()
    {
        var backend = new FakeBackend
        {
            Adapters = [Adapter(WlanInterfaceState.Connected, On)],
            WriteError = new WlanException(WlanFailure.NotSupported, 50, "no")
        };
        await using var service = Create(backend);

        var result = await service.SetEnabledAsync(false, Ct);

        Assert.Equal(ErrorCode.Unsupported, result.Error!.Code);
    }

    [Fact]
    public async Task OtherWriteFailureIsATypedRetryableError()
    {
        var backend = new FakeBackend
        {
            Adapters = [Adapter(WlanInterfaceState.Connected, On)],
            WriteError = new WlanException(WlanFailure.Other, 31, "x")
        };
        await using var service = Create(backend);

        var result = await service.SetEnabledAsync(false, Ct);

        Assert.Equal(ErrorCode.Unexpected, result.Error!.Code);
        Assert.True(result.Error.Retryable);
        Assert.True((await service.GetAsync(Ct)).Value.CanToggle); // an unexpected failure is not a refusal
    }

    [Fact]
    public async Task CancellingWhileConfirmingReturnsCancelled()
    {
        using var cts = new CancellationTokenSource();
        var backend = new FakeBackend { Adapters = [Adapter(WlanInterfaceState.NotReady, SoftOff)], ApplyWrites = false };
        await using var service = Create(backend, options: new RadioConfirmOptions
        {
            ConfirmAttempts = 50,
            Delay = async (_, token) =>
            {
                await cts.CancelAsync();
                token.ThrowIfCancellationRequested();
            }
        });

        var result = await service.SetEnabledAsync(true, cts.Token);

        Assert.True(result.IsCancelled);
        Assert.True(backend.Reads < 10);
    }

    [Fact]
    public async Task TogglesAreSerialized()
    {
        var backend = new FakeBackend { Adapters = [Adapter(WlanInterfaceState.Connected, On)] };
        await using var service = Create(backend);

        var tasks = Enumerable.Range(0, 20).Select(i => service.SetEnabledAsync(i % 2 == 0, Ct)).ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.True(r.IsSuccess, r.Error?.SafeMessage));
    }

    // ---- disposal and privacy ----------------------------------------------------------------------------------------

    [Fact]
    public async Task DisposalCompletesObserversClosesTheBackendAndIsIdempotent()
    {
        var backend = new FakeBackend { Adapters = [Adapter(WlanInterfaceState.Connected, On)] };
        var service = Create(backend);
        var recorder = new Recorder();
        service.Changes.Subscribe(recorder);

        await service.DisposeAsync();
        await service.DisposeAsync();
        service.Dispose();

        Assert.True(recorder.Completed);
        Assert.True(backend.Disposed);
        Assert.False(backend.HasSubscribers);
    }

    [Fact]
    public async Task SynchronousDisposalWorksToo()
    {
        var backend = new FakeBackend();
        var service = Create(backend);

        service.Dispose();

        Assert.True(backend.Disposed);
        Assert.Equal(ErrorCode.DeviceUnavailable, (await service.GetAsync(Ct)).Error!.Code);
    }

    [Fact]
    public async Task LogEntriesCarryOnlyEventAndExceptionType()
    {
        var log = new CollectingLog();
        var backend = new FakeBackend { ReadError = new WlanException(WlanFailure.AccessDenied, 5, "SSID Home-Net secret") };
        await using var service = Create(backend, log);

        _ = await service.GetAsync(Ct);

        var entry = Assert.Single(log.Events);
        Assert.StartsWith("wifi.", entry.EventId, StringComparison.Ordinal);
        Assert.DoesNotContain("Home-Net", entry.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ConfirmationAttemptsMustBePositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeWifiService(new FakeBackend(), null, new RadioConfirmOptions { ConfirmAttempts = 0 }));
        Assert.Throws<ArgumentNullException>(() => new NativeWifiService((IWlanBackend)null!));
    }
}
