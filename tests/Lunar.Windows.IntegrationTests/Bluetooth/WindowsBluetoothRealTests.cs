using Lunar.Application.System.Models;
using Lunar.Windows.Bluetooth;
using Xunit;

namespace Lunar.Windows.IntegrationTests.Bluetooth;

// These use the real Windows radio API of the machine they run on and only READ. They never switch a radio, never list,
// pair or connect a device, never read a device name or address, and write nothing to the output except the state
// labels. A machine without Bluetooth (a build server) still passes the state check and skips the rest. The real radio
// switch is a manual check done only with the owner's permission and recorded in the BE-13 evidence.
public sealed class WindowsBluetoothRealTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReadsTheRealRadioStateWithoutAdministratorRights()
    {
        await using var service = new WindowsBluetoothService();

        var read = await service.GetAsync(Ct);

        Assert.True(read.IsSuccess, read.Error?.SafeMessage);
        var state = read.Value;
        TestContext.Current.SendDiagnosticMessage($"Real Bluetooth state: {state.Capability}, radio {state.Radio}, toggle {state.CanToggle}");
        Assert.NotEqual(CapabilityStatus.Unknown, state.Capability);
        if (state.Capability == CapabilityStatus.Supported)
            Assert.NotEqual(RadioState.Unavailable, state.Radio);
        else
            Assert.False(state.CanToggle);
    }

    [Fact]
    public async Task AskingForTheCurrentRadioStateWritesNothingAndSucceeds()
    {
        await using var service = new WindowsBluetoothService();
        var state = (await service.GetAsync(Ct)).Value;
        if (state.Radio is not (RadioState.Enabled or RadioState.Disabled))
            Assert.Skip("This machine reports no Bluetooth radio state.");

        var result = await service.SetEnabledAsync(state.Radio == RadioState.Enabled, Ct);

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal(state.Radio, (await service.GetAsync(Ct)).Value.Radio);
    }

    [Fact]
    public async Task ReadingRepeatedlyAndDisposingReleasesTheRadiosCleanly()
    {
        var service = new WindowsBluetoothService();
        for (var i = 0; i < 20; i++)
            Assert.True((await service.GetAsync(Ct)).IsSuccess);

        await service.DisposeAsync();

        Assert.True((await service.GetAsync(Ct)).IsFailure);
    }

    [Fact]
    public async Task ChangesAreObservableAndCompleteOnDisposal()
    {
        var service = new WindowsBluetoothService();
        var completed = new TaskCompletionSource();
        service.Changes.Subscribe(new Completion(completed));
        _ = await service.GetAsync(Ct);

        await service.DisposeAsync();

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    private sealed class Completion(TaskCompletionSource done) : IObserver<BluetoothState>
    {
        public void OnNext(BluetoothState value)
        {
        }

        public void OnError(Exception error) => done.TrySetException(error);

        public void OnCompleted() => done.TrySetResult();
    }
}
