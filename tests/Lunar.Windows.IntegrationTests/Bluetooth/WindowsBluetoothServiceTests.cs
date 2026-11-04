using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.System.Models;
using Lunar.Domain.Shared;
using Lunar.Windows.Bluetooth;
using Lunar.Windows.Shared;
using Xunit;

namespace Lunar.Windows.IntegrationTests.Bluetooth;

// Everything here uses a fake radio backend: nothing touches the machine's real Bluetooth.
public sealed class WindowsBluetoothServiceTests
{
    internal sealed class FakeBackend : IBluetoothBackend
    {
        private readonly object _gate = new();
        private bool? _pending;
        private int _readsSincePending;

        public List<BluetoothRadioReading> Radios { get; set; } = [];

        public BluetoothException? ReadError { get; set; }

        public BluetoothException? SetError { get; set; }

        public BluetoothAccess Access { get; set; } = BluetoothAccess.Allowed;

        /// <summary>When true an allowed write changes every On/Off radio, after <see cref="ReadsBeforeApplied"/> reads.</summary>
        public bool ApplyWrites { get; set; } = true;

        public int ReadsBeforeApplied { get; set; }

        public int Reads { get; private set; }

        public List<bool> Writes { get; } = [];

        public bool Disposed { get; private set; }

        public bool HasSubscribers => Changed is not null;

        public event Action? Changed;

        public Task<IReadOnlyList<BluetoothRadioReading>> ReadAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                Reads++;
                if (ReadError is not null)
                    throw ReadError;
                if (_pending is { } on && ++_readsSincePending > ReadsBeforeApplied)
                {
                    Radios = [.. Radios.Select(r => r.Mode is BluetoothRadioMode.On or BluetoothRadioMode.Off ? new BluetoothRadioReading(on ? BluetoothRadioMode.On : BluetoothRadioMode.Off) : r)];
                    _pending = null;
                }

                return Task.FromResult<IReadOnlyList<BluetoothRadioReading>>([.. Radios]);
            }
        }

        public Task<BluetoothAccess> SetAsync(bool on, CancellationToken ct)
        {
            lock (_gate)
            {
                if (SetError is not null)
                    throw SetError;
                if (Access == BluetoothAccess.Allowed)
                {
                    Writes.Add(on);
                    if (ApplyWrites)
                    {
                        _pending = on;
                        _readsSincePending = 0;
                    }
                }

                return Task.FromResult(Access);
            }
        }

        public void Raise() => Changed?.Invoke();

        public void Dispose() => Disposed = true;
    }

    private sealed class Recorder : IObserver<BluetoothState>
    {
        private readonly object _gate = new();

        public List<BluetoothState> Values { get; } = [];

        public bool Completed { get; private set; }

        public void OnNext(BluetoothState value)
        {
            lock (_gate)
                Values.Add(value);
        }

        public void OnError(Exception error) => throw new InvalidOperationException("OnError must never be called.");

        public void OnCompleted() => Completed = true;

        public BluetoothState[] Snapshot()
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

    private static BluetoothRadioReading Radio(BluetoothRadioMode mode) => new(mode);

    private static WindowsBluetoothService Create(FakeBackend backend, IDiagnosticLog? log = null, RadioConfirmOptions? options = null) =>
        new(backend, log, options ?? NoWait);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 3000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < end)
            await Task.Delay(5, Ct);
        Assert.True(condition(), "Condition not reached in time.");
    }

    // ---- reading -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OnRadioReadsAsEnabledAndSwitchable()
    {
        var backend = new FakeBackend { Radios = [Radio(BluetoothRadioMode.On)] };
        await using var service = Create(backend);

        var state = (await service.GetAsync(Ct)).Value;

        Assert.Equal(new BluetoothState(CapabilityStatus.Supported, RadioState.Enabled, true), state);
    }

    [Fact]
    public async Task OffRadioReadsAsDisabledButSwitchable()
    {
        var backend = new FakeBackend { Radios = [Radio(BluetoothRadioMode.Off)] };
        await using var service = Create(backend);

        Assert.Equal(new BluetoothState(CapabilityStatus.Supported, RadioState.Disabled, true), (await service.GetAsync(Ct)).Value);
    }

    [Fact]
    public async Task RadioDisabledBySystemReadsAsDisabledAndNotSwitchable()
    {
        var backend = new FakeBackend { Radios = [Radio(BluetoothRadioMode.Disabled)] };
        await using var service = Create(backend);

        var state = (await service.GetAsync(Ct)).Value;

        Assert.Equal(RadioState.Disabled, state.Radio);
        Assert.False(state.CanToggle);
    }

    [Fact]
    public async Task NoRadioIsAStateNotAnError()
    {
        await using var service = Create(new FakeBackend());

        var read = await service.GetAsync(Ct);

        Assert.True(read.IsSuccess);
        Assert.Equal(CapabilityStatus.Unavailable, read.Value.Capability);
        Assert.Equal(RadioState.Unavailable, read.Value.Radio);
        Assert.False(read.Value.CanToggle);
    }

    [Theory]
    [InlineData("NotSupported", CapabilityStatus.Unsupported)]
    [InlineData("AccessDenied", CapabilityStatus.PermissionDenied)]
    public async Task WindowsRefusalsAreDistinguishableStates(string failure, CapabilityStatus expected)
    {
        var backend = new FakeBackend { ReadError = new BluetoothException(Enum.Parse<BluetoothFailure>(failure), 1, "x") };
        await using var service = Create(backend);

        var read = await service.GetAsync(Ct);

        Assert.True(read.IsSuccess);
        Assert.Equal(expected, read.Value.Capability);
        Assert.False(read.Value.CanToggle);
    }

    [Fact]
    public async Task NoRadioUnsupportedAndDeniedAreThreeDifferentStates()
    {
        var none = (await Read(new FakeBackend())).Capability;
        var unsupported = (await Read(new FakeBackend { ReadError = new BluetoothException(BluetoothFailure.NotSupported, 1, "x") })).Capability;
        var denied = (await Read(new FakeBackend { ReadError = new BluetoothException(BluetoothFailure.AccessDenied, 1, "x") })).Capability;

        Assert.Equal(3, new[] { none, unsupported, denied }.Distinct().Count());

        static async Task<BluetoothState> Read(FakeBackend backend)
        {
            await using var service = Create(backend);
            return (await service.GetAsync(Ct)).Value;
        }
    }

    [Fact]
    public async Task UnexpectedFailureIsATypedRetryableError()
    {
        var backend = new FakeBackend { ReadError = new BluetoothException(BluetoothFailure.Other, 31, "x") };
        await using var service = Create(backend);

        var read = await service.GetAsync(Ct);

        Assert.Equal(ErrorCode.Unexpected, read.Error!.Code);
        Assert.True(read.Error.Retryable);
    }

    [Fact]
    public async Task CancelledReadDoesNotTouchWindows()
    {
        var backend = new FakeBackend { Radios = [Radio(BluetoothRadioMode.On)] };
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

        Assert.Equal(ErrorCode.DeviceUnavailable, (await service.GetAsync(Ct)).Error!.Code);
        Assert.Equal(ErrorCode.DeviceUnavailable, (await service.SetEnabledAsync(true, Ct)).Error!.Code);
    }

    // ---- notifications -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task RadioChangesArePublishedWithoutRepeats()
    {
        var backend = new FakeBackend { Radios = [Radio(BluetoothRadioMode.On)] };
        await using var service = Create(backend);
        var recorder = new Recorder();
        using var subscription = service.Changes.Subscribe(recorder);
        _ = await service.GetAsync(Ct);

        backend.Raise(); // nothing changed: no new state
        backend.Radios = [Radio(BluetoothRadioMode.Off)];
        backend.Raise();
        await WaitFor(() => recorder.Snapshot().Length >= 2);
        backend.Raise();
        await Task.Delay(100, Ct);

        var seen = recorder.Snapshot();
        Assert.Equal(2, seen.Length);
        Assert.Equal(RadioState.Enabled, seen[0].Radio);
        Assert.Equal(RadioState.Disabled, seen[1].Radio);
    }

    [Fact]
    public async Task RadioRemovalAndArrivalAreReported()
    {
        var backend = new FakeBackend { Radios = [Radio(BluetoothRadioMode.On)] };
        await using var service = Create(backend);
        var recorder = new Recorder();
        using var subscription = service.Changes.Subscribe(recorder);
        _ = await service.GetAsync(Ct);

        backend.Radios = [];
        backend.Raise();
        await WaitFor(() => recorder.Snapshot().Length >= 2);
        backend.Radios = [Radio(BluetoothRadioMode.On)];
        backend.Raise();
        await WaitFor(() => recorder.Snapshot().Length >= 3);

        var seen = recorder.Snapshot();
        Assert.Equal(CapabilityStatus.Unavailable, seen[1].Capability);
        Assert.Equal(CapabilityStatus.Supported, seen[2].Capability);
    }

    [Fact]
    public async Task ABurstOfNotificationsCausesFewReads()
    {
        var backend = new FakeBackend { Radios = [Radio(BluetoothRadioMode.On)] };
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
        var backend = new FakeBackend { Radios = [Radio(BluetoothRadioMode.On)] };
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
    public async Task TurningOnSucceedsOnlyOnceTheRadioIsSeenOn()
    {
        var backend = new FakeBackend { Radios = [Radio(BluetoothRadioMode.Off)], ReadsBeforeApplied = 3 };
        await using var service = Create(backend);

        var result = await service.SetEnabledAsync(true, Ct);

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal([true], backend.Writes);
        Assert.Equal(RadioState.Enabled, (await service.GetAsync(Ct)).Value.Radio);
    }

    [Fact]
    public async Task TurningOffWorksTheSameWay()
    {
        var backend = new FakeBackend { Radios = [Radio(BluetoothRadioMode.On)] };
        await using var service = Create(backend);

        var result = await service.SetEnabledAsync(false, Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal([false], backend.Writes);
        Assert.Equal(RadioState.Disabled, (await service.GetAsync(Ct)).Value.Radio);
    }

    [Fact]
    public async Task AskingForTheCurrentStateWritesNothing()
    {
        var backend = new FakeBackend { Radios = [Radio(BluetoothRadioMode.On)] };
        await using var service = Create(backend);

        Assert.True((await service.SetEnabledAsync(true, Ct)).IsSuccess);
        Assert.Empty(backend.Writes);
    }

    [Fact]
    public async Task AcceptedChangeThatNeverTakesEffectTimesOut()
    {
        var backend = new FakeBackend { Radios = [Radio(BluetoothRadioMode.Off)], ApplyWrites = false };
        await using var service = Create(backend, options: new RadioConfirmOptions { ConfirmAttempts = 4, Delay = (_, _) => Task.CompletedTask });

        var result = await service.SetEnabledAsync(true, Ct);

        Assert.Equal(ErrorCode.Timeout, result.Error!.Code);
        Assert.True(result.Error.Retryable);
        Assert.InRange(backend.Reads, 5, 6);
    }

    [Fact]
    public async Task RadioHeldOffBySystemRefusesWithoutWriting()
    {
        var backend = new FakeBackend { Radios = [Radio(BluetoothRadioMode.Disabled)] };
        await using var service = Create(backend);

        var result = await service.SetEnabledAsync(true, Ct);

        Assert.Equal(ErrorCode.Unsupported, result.Error!.Code);
        Assert.Empty(backend.Writes);
    }

    [Fact]
    public async Task NoRadioRefusesToggleAsDeviceUnavailable()
    {
        var backend = new FakeBackend();
        await using var service = Create(backend);

        Assert.Equal(ErrorCode.DeviceUnavailable, (await service.SetEnabledAsync(true, Ct)).Error!.Code);
        Assert.Empty(backend.Writes);
    }

    [Theory]
    [InlineData("DeniedByUser")]
    [InlineData("DeniedBySystem")]
    public async Task RefusedChangeIsAccessDeniedAndTheSwitchIsNotOfferedAgain(string access)
    {
        var backend = new FakeBackend { Radios = [Radio(BluetoothRadioMode.On)], Access = Enum.Parse<BluetoothAccess>(access) };
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
    }

    [Fact]
    public async Task RefusalReportedAsAnExceptionIsTheSame()
    {
        var backend = new FakeBackend
        {
            Radios = [Radio(BluetoothRadioMode.On)],
            SetError = new BluetoothException(BluetoothFailure.AccessDenied, unchecked((int)0x80070005), "denied")
        };
        await using var service = Create(backend);

        var result = await service.SetEnabledAsync(false, Ct);

        Assert.Equal(ErrorCode.AccessDenied, result.Error!.Code);
        Assert.False((await service.GetAsync(Ct)).Value.CanToggle);
    }

    [Fact]
    public async Task UnspecifiedAnswerIsATypedRetryableErrorNotARefusal()
    {
        var backend = new FakeBackend { Radios = [Radio(BluetoothRadioMode.On)], Access = BluetoothAccess.Unspecified };
        await using var service = Create(backend);

        var result = await service.SetEnabledAsync(false, Ct);

        Assert.Equal(ErrorCode.Unexpected, result.Error!.Code);
        Assert.True(result.Error.Retryable);
        Assert.True((await service.GetAsync(Ct)).Value.CanToggle);
    }

    [Fact]
    public async Task RadioThatCannotBeSwitchedIsUnsupported()
    {
        var backend = new FakeBackend
        {
            Radios = [Radio(BluetoothRadioMode.On)],
            SetError = new BluetoothException(BluetoothFailure.NotSupported, 0, "no")
        };
        await using var service = Create(backend);

        Assert.Equal(ErrorCode.Unsupported, (await service.SetEnabledAsync(false, Ct)).Error!.Code);
    }

    [Fact]
    public async Task OtherChangeFailureIsATypedRetryableError()
    {
        var backend = new FakeBackend
        {
            Radios = [Radio(BluetoothRadioMode.On)],
            SetError = new BluetoothException(BluetoothFailure.Other, 31, "x")
        };
        await using var service = Create(backend);

        var result = await service.SetEnabledAsync(false, Ct);

        Assert.Equal(ErrorCode.Unexpected, result.Error!.Code);
        Assert.True(result.Error.Retryable);
        Assert.True((await service.GetAsync(Ct)).Value.CanToggle);
    }

    [Fact]
    public async Task CancellingWhileConfirmingReturnsCancelled()
    {
        using var cts = new CancellationTokenSource();
        var backend = new FakeBackend { Radios = [Radio(BluetoothRadioMode.Off)], ApplyWrites = false };
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
        var backend = new FakeBackend { Radios = [Radio(BluetoothRadioMode.On)] };
        await using var service = Create(backend);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => service.SetEnabledAsync(i % 2 == 0, Ct)));

        Assert.All(results, r => Assert.True(r.IsSuccess, r.Error?.SafeMessage));
    }

    // ---- disposal and privacy ----------------------------------------------------------------------------------------

    [Fact]
    public async Task DisposalCompletesObserversClosesTheBackendAndIsIdempotent()
    {
        var backend = new FakeBackend { Radios = [Radio(BluetoothRadioMode.On)] };
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
        var backend = new FakeBackend { ReadError = new BluetoothException(BluetoothFailure.AccessDenied, 5, "Headset AA:BB:CC secret") };
        await using var service = Create(backend, log);

        _ = await service.GetAsync(Ct);

        var entry = Assert.Single(log.Events);
        Assert.StartsWith("bluetooth.", entry.EventId, StringComparison.Ordinal);
        Assert.DoesNotContain("AA:BB", entry.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructorArgumentsAreValidated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsBluetoothService(new FakeBackend(), null, new RadioConfirmOptions { ConfirmAttempts = 0 }));
        Assert.Throws<ArgumentNullException>(() => new WindowsBluetoothService((IBluetoothBackend)null!));
    }
}
