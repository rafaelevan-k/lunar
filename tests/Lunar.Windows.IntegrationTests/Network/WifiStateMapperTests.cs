using Lunar.Application.System.Models;
using Lunar.Windows.Network;
using Xunit;

namespace Lunar.Windows.IntegrationTests.Network;

public sealed class WifiStateMapperTests
{
    private static readonly PhyRadio BothOn = new(RadioSwitch.On, RadioSwitch.On);
    private static readonly PhyRadio SoftOff = new(RadioSwitch.Off, RadioSwitch.On);
    private static readonly PhyRadio HardOff = new(RadioSwitch.On, RadioSwitch.Off);
    private static readonly PhyRadio BothOff = new(RadioSwitch.Off, RadioSwitch.Off);
    private static readonly PhyRadio Unreported = new(RadioSwitch.Unknown, RadioSwitch.Unknown);

    private static WifiState Map(params WlanInterfaceReading[] adapters) => WifiStateMapper.Map(adapters);

    private static WlanInterfaceReading A(WlanInterfaceState state, params PhyRadio[] phys) => new(state, phys);

    [Fact]
    public void NoAdapterIsUnavailableAndNotSwitchable()
    {
        var state = Map();

        Assert.Equal(CapabilityStatus.Unavailable, state.Capability);
        Assert.Equal(RadioState.Unavailable, state.Radio);
        Assert.False(state.CanToggle);
    }

    [Theory]
    [InlineData("Disconnected")]
    [InlineData("Associating")]
    [InlineData("Discovering")]
    [InlineData("Authenticating")]
    [InlineData("Disconnecting")]
    [InlineData("AdHocFormed")]
    public void OnlyAnAssociatedAdapterIsConnected(string name)
    {
        var mapped = Map(A(Enum.Parse<WlanInterfaceState>(name), BothOn));

        Assert.Equal(RadioState.Enabled, mapped.Radio);
        Assert.Equal(WifiConnectivity.Disconnected, mapped.Connectivity);
    }

    [Fact]
    public void ConnectedAdapterIsConnected() =>
        Assert.Equal(WifiConnectivity.Connected, Map(A(WlanInterfaceState.Connected, BothOn)).Connectivity);

    [Fact]
    public void ConnectedImpliesEnabledEvenIfTheDriverDoesNotReportItsRadio()
    {
        var state = Map(new WlanInterfaceReading(WlanInterfaceState.Connected, null));

        Assert.Equal(RadioState.Enabled, state.Radio);
        Assert.Equal(WifiConnectivity.Connected, state.Connectivity);
        Assert.False(state.CanToggle);
    }

    [Theory]
    [MemberData(nameof(RadioCases))]
    public void RadioIsDerivedFromBothSwitches(string software, string hardware, RadioState radio, bool canToggle)
    {
        var phy = new PhyRadio(Enum.Parse<RadioSwitch>(software), Enum.Parse<RadioSwitch>(hardware));
        var state = Map(A(WlanInterfaceState.NotReady, phy));

        Assert.Equal(radio, state.Radio);
        Assert.Equal(canToggle, state.CanToggle);
    }

    public static TheoryData<string, string, RadioState, bool> RadioCases() => new()
    {
        { "On", "On", RadioState.Enabled, true },
        { "Off", "On", RadioState.Disabled, true },
        { "On", "Off", RadioState.Disabled, false },
        { "Off", "Off", RadioState.Disabled, false },
        { "Unknown", "Unknown", RadioState.Unknown, false },
        { "On", "Unknown", RadioState.Unknown, false },
        { "Unknown", "On", RadioState.Unknown, false }
    };

    [Fact]
    public void AnAdapterBeingSwitchedOffDoesNotReadAsConnected()
    {
        var state = Map(A(WlanInterfaceState.Connected, SoftOff));

        Assert.Equal(RadioState.Disabled, state.Radio);
        Assert.Equal(WifiConnectivity.Disconnected, state.Connectivity);
    }

    [Fact]
    public void NotReadyWithUnknownRadioIsUnknownNotDisconnected()
    {
        var state = Map(A(WlanInterfaceState.NotReady, Unreported));

        Assert.Equal(RadioState.Unknown, state.Radio);
        Assert.Equal(WifiConnectivity.Unknown, state.Connectivity);
    }

    [Fact]
    public void AnUnreadableStateWithUnknownRadioIsUnknown()
    {
        Assert.Equal(WifiConnectivity.Unknown, Map(A(WlanInterfaceState.Unknown, Unreported)).Connectivity);
    }

    [Fact]
    public void OneConnectedAdapterWinsOverOthers()
    {
        var state = Map(A(WlanInterfaceState.Disconnected, SoftOff), A(WlanInterfaceState.Connected, BothOn));

        Assert.Equal(WifiConnectivity.Connected, state.Connectivity);
        Assert.Equal(RadioState.Enabled, state.Radio);
        Assert.True(state.CanToggle);
    }

    [Fact]
    public void EnabledAdapterWinsOverDisabledOne()
    {
        var state = Map(A(WlanInterfaceState.Disconnected, BothOn), A(WlanInterfaceState.NotReady, HardOff));

        Assert.Equal(RadioState.Enabled, state.Radio);
    }

    [Fact]
    public void AdapterWithTwoPhysIsOnWhenOnePhyIsOn()
    {
        var state = Map(A(WlanInterfaceState.Disconnected, SoftOff, BothOn));

        Assert.Equal(RadioState.Enabled, state.Radio);
    }

    [Fact]
    public void AdapterWithAnEmptyPhyListIsUnknown()
    {
        var state = Map(new WlanInterfaceReading(WlanInterfaceState.Disconnected, []));

        Assert.Equal(RadioState.Unknown, state.Radio);
        Assert.False(state.CanToggle);
    }

    [Theory]
    [InlineData(0, "NotReady")]
    [InlineData(1, "Connected")]
    [InlineData(2, "AdHocFormed")]
    [InlineData(3, "Disconnecting")]
    [InlineData(4, "Disconnected")]
    [InlineData(5, "Associating")]
    [InlineData(6, "Discovering")]
    [InlineData(7, "Authenticating")]
    [InlineData(8, "Unknown")]
    [InlineData(-1, "Unknown")]
    public void NativeInterfaceStatesAreTranslated(int native, string expected) =>
        Assert.Equal(Enum.Parse<WlanInterfaceState>(expected), NativeWlanBackend.ToState(native));

    [Theory]
    [InlineData(0, "Unknown")]
    [InlineData(1, "On")]
    [InlineData(2, "Off")]
    [InlineData(3, "Unknown")]
    public void NativeRadioStatesAreTranslated(int native, string expected) =>
        Assert.Equal(Enum.Parse<RadioSwitch>(expected), NativeWlanBackend.ToSwitch(native));

    [Theory]
    [InlineData(5u, "AccessDenied")]
    [InlineData(1062u, "ServiceUnavailable")]
    [InlineData(1722u, "ServiceUnavailable")]
    [InlineData(1726u, "ServiceUnavailable")]
    [InlineData(50u, "NotSupported")]
    [InlineData(1060u, "NotSupported")]
    [InlineData(120u, "NotSupported")]
    [InlineData(87u, "Other")]
    [InlineData(31u, "Other")]
    public void Win32CodesAreClassified(uint code, string expected) =>
        Assert.Equal(Enum.Parse<WlanFailure>(expected), NativeWlanBackend.Classify(code));
}
