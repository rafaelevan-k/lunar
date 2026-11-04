using Lunar.Application.System.Models;
using Lunar.Windows.Network;
using Xunit;

namespace Lunar.Windows.IntegrationTests.Network;

// These use the real Native Wi-Fi API of the machine they run on and only READ. They never switch the radio, never ask
// for the SSID, a scan or a profile, and never write anything to the output. A machine with no Wi-Fi adapter (a build
// server) still passes the state check and skips the rest. The real radio toggle is a manual check done only with the
// owner's permission and recorded in the BE-12 evidence.
public sealed class NativeWifiRealTests
{
    [Fact]
    public async Task ReadsTheRealStateWithoutAdministratorRights()
    {
        await using var service = new NativeWifiService();

        var read = await service.GetAsync(TestContext.Current.CancellationToken);

        Assert.True(read.IsSuccess, read.Error?.SafeMessage);
        var state = read.Value;
        TestContext.Current.SendDiagnosticMessage($"Real Wi-Fi state: {state.Capability}, radio {state.Radio}, {state.Connectivity}, toggle {state.CanToggle}");
        Assert.NotEqual(CapabilityStatus.Unknown, state.Capability);
        if (state.Capability == CapabilityStatus.Supported)
        {
            Assert.NotEqual(RadioState.Unavailable, state.Radio);
            if (state.Connectivity == WifiConnectivity.Connected)
                Assert.Equal(RadioState.Enabled, state.Radio);
        }
        else
        {
            Assert.False(state.CanToggle);
        }
    }

    [Fact]
    public async Task AskingForTheCurrentRadioStateWritesNothingAndSucceeds()
    {
        await using var service = new NativeWifiService();
        var state = (await service.GetAsync(TestContext.Current.CancellationToken)).Value;
        if (state.Radio is not (RadioState.Enabled or RadioState.Disabled))
            Assert.Skip("This machine reports no switchable Wi-Fi radio.");

        var result = await service.SetEnabledAsync(state.Radio == RadioState.Enabled, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal(state.Radio, (await service.GetAsync(TestContext.Current.CancellationToken)).Value.Radio);
    }

    [Fact]
    public async Task ReadingRepeatedlyAndDisposingReleasesTheHandleCleanly()
    {
        var service = new NativeWifiService();
        for (var i = 0; i < 20; i++)
            Assert.True((await service.GetAsync(TestContext.Current.CancellationToken)).IsSuccess);

        await service.DisposeAsync();

        Assert.True((await service.GetAsync(TestContext.Current.CancellationToken)).IsFailure);
    }
}
