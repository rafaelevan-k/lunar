using Lunar.Application.System.Models;
using Lunar.Windows.Bluetooth;
using Xunit;

namespace Lunar.Windows.IntegrationTests.Bluetooth;

public sealed class BluetoothStateMapperTests
{
    private static BluetoothState Map(params string[] modes) =>
        BluetoothStateMapper.Map([.. modes.Select(m => new BluetoothRadioReading(Enum.Parse<BluetoothRadioMode>(m)))]);

    [Fact]
    public void NoRadioIsUnavailableAndNotSwitchable()
    {
        var state = Map();

        Assert.Equal(CapabilityStatus.Unavailable, state.Capability);
        Assert.Equal(RadioState.Unavailable, state.Radio);
        Assert.False(state.CanToggle);
    }

    [Theory]
    [InlineData("On", RadioState.Enabled, true)]
    [InlineData("Off", RadioState.Disabled, true)]
    [InlineData("Disabled", RadioState.Disabled, false)]
    [InlineData("Unknown", RadioState.Unknown, false)]
    public void OneRadioMapsByItsMode(string mode, RadioState radio, bool canToggle)
    {
        var state = Map(mode);

        Assert.Equal(CapabilityStatus.Supported, state.Capability);
        Assert.Equal(radio, state.Radio);
        Assert.Equal(canToggle, state.CanToggle);
    }

    [Fact]
    public void AnOnRadioWinsOverOthers() =>
        Assert.Equal(RadioState.Enabled, Map("Off", "On", "Disabled").Radio);

    [Fact]
    public void OffWinsOverUnknown() =>
        Assert.Equal(RadioState.Disabled, Map("Unknown", "Off").Radio);

    [Fact]
    public void ASwitchableRadioAmongDisabledOnesMakesItSwitchable() =>
        Assert.True(Map("Disabled", "Off").CanToggle);

    [Fact]
    public void RadiosHeldDisabledOnlyAreNotSwitchable() =>
        Assert.False(Map("Disabled", "Disabled", "Unknown").CanToggle);

    [Theory]
    [InlineData(0, "Unknown")]
    [InlineData(1, "On")]
    [InlineData(2, "Off")]
    [InlineData(3, "Disabled")]
    [InlineData(9, "Unknown")]
    public void WindowsRadioStatesAreTranslated(int native, string expected) =>
        Assert.Equal(Enum.Parse<BluetoothRadioMode>(expected), WinRtBluetoothBackend.ToMode((global::Windows.Devices.Radios.RadioState)native));

    [Theory]
    [InlineData(0, "Unspecified")]
    [InlineData(1, "Allowed")]
    [InlineData(2, "DeniedByUser")]
    [InlineData(3, "DeniedBySystem")]
    public void WindowsAccessAnswersAreTranslated(int native, string expected) =>
        Assert.Equal(Enum.Parse<BluetoothAccess>(expected), WinRtBluetoothBackend.ToAccess((global::Windows.Devices.Radios.RadioAccessStatus)native));
}
