using Lunar.Application.System.Models;
using Lunar.Desktop.Controls;
using Lunar.Desktop.Features.SystemControls;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Desktop.UITests.Features;

public sealed class SystemPageTextTests
{
    [Theory]
    [InlineData(CapabilityStatus.Supported, RadioState.Enabled, WifiConnectivity.Connected, "Connected", StatusKind.Success)]
    [InlineData(CapabilityStatus.Supported, RadioState.Enabled, WifiConnectivity.Disconnected, "On, not connected", StatusKind.Info)]
    [InlineData(CapabilityStatus.Supported, RadioState.Enabled, WifiConnectivity.Unknown, "On", StatusKind.Info)]
    [InlineData(CapabilityStatus.Supported, RadioState.Disabled, WifiConnectivity.Disconnected, "Off", StatusKind.Neutral)]
    [InlineData(CapabilityStatus.Supported, RadioState.Unknown, WifiConnectivity.Unknown, "Unknown", StatusKind.Neutral)]
    [InlineData(CapabilityStatus.Supported, RadioState.Unavailable, WifiConnectivity.Unknown, "No Wi-Fi adapter", StatusKind.Unavailable)]
    [InlineData(CapabilityStatus.Unavailable, RadioState.Unavailable, WifiConnectivity.Disconnected, "No Wi-Fi adapter", StatusKind.Unavailable)]
    [InlineData(CapabilityStatus.Unsupported, RadioState.Unavailable, WifiConnectivity.Unknown, "Not supported", StatusKind.Unavailable)]
    [InlineData(CapabilityStatus.PermissionDenied, RadioState.Unknown, WifiConnectivity.Unknown, "Access denied", StatusKind.Error)]
    [InlineData(CapabilityStatus.Unknown, RadioState.Unknown, WifiConnectivity.Unknown, "Unknown", StatusKind.Neutral)]
    public void WifiWords(CapabilityStatus capability, RadioState radio, WifiConnectivity connectivity, string text, StatusKind kind)
    {
        var (actualText, actualKind) = SystemPageText.Wifi(new WifiState(capability, radio, connectivity, false));

        Assert.Equal(text, actualText);
        Assert.Equal(kind, actualKind);
    }

    [Theory]
    [InlineData(CapabilityStatus.Supported, RadioState.Enabled, "On", StatusKind.Success)]
    [InlineData(CapabilityStatus.Supported, RadioState.Disabled, "Off", StatusKind.Neutral)]
    [InlineData(CapabilityStatus.Supported, RadioState.Unknown, "Unknown", StatusKind.Neutral)]
    [InlineData(CapabilityStatus.Supported, RadioState.Unavailable, "No Bluetooth radio", StatusKind.Unavailable)]
    [InlineData(CapabilityStatus.Unavailable, RadioState.Unavailable, "No Bluetooth radio", StatusKind.Unavailable)]
    [InlineData(CapabilityStatus.Unsupported, RadioState.Unavailable, "Not supported", StatusKind.Unavailable)]
    [InlineData(CapabilityStatus.PermissionDenied, RadioState.Unknown, "Access denied", StatusKind.Error)]
    [InlineData(CapabilityStatus.Unknown, RadioState.Unknown, "Unknown", StatusKind.Neutral)]
    public void BluetoothWords(CapabilityStatus capability, RadioState radio, string text, StatusKind kind)
    {
        var (actualText, actualKind) = SystemPageText.Bluetooth(new BluetoothState(capability, radio, false));

        Assert.Equal(text, actualText);
        Assert.Equal(kind, actualKind);
    }

    [Fact]
    public void NoTwoUnusableStatesSayTheSameThingWithTheSameSymbol()
    {
        var states = new[]
        {
            SystemPageText.Wifi(new WifiState(CapabilityStatus.Unavailable, RadioState.Unavailable, WifiConnectivity.Disconnected, false)),
            SystemPageText.Wifi(new WifiState(CapabilityStatus.Unsupported, RadioState.Unavailable, WifiConnectivity.Unknown, false)),
            SystemPageText.Wifi(new WifiState(CapabilityStatus.PermissionDenied, RadioState.Unknown, WifiConnectivity.Unknown, false)),
            SystemPageText.Wifi(WifiState.Unknown)
        };

        Assert.Equal(states.Length, states.Distinct().Count());
    }

    [Theory]
    [InlineData(CapabilityStatus.Supported, RadioState.Enabled, true, false)]
    [InlineData(CapabilityStatus.Supported, RadioState.Disabled, false, true)]
    [InlineData(CapabilityStatus.Unsupported, RadioState.Unavailable, false, true)]
    [InlineData(CapabilityStatus.Supported, RadioState.Unavailable, false, false)]
    [InlineData(CapabilityStatus.PermissionDenied, RadioState.Unknown, false, true)]
    [InlineData(CapabilityStatus.Unknown, RadioState.Unknown, false, true)]
    [InlineData(CapabilityStatus.Unavailable, RadioState.Unavailable, false, false)]
    public void TheSettingsLinkIsOfferedWhereItCanHelp(CapabilityStatus capability, RadioState radio, bool canToggle, bool offered) =>
        Assert.Equal(offered, SystemPageText.OffersSettings(capability, radio, canToggle));

    [Theory]
    [InlineData(CapabilityStatus.Supported, null)]
    [InlineData(CapabilityStatus.Unavailable, "No audio output device")]
    [InlineData(CapabilityStatus.Unsupported, "Volume can't be controlled on this PC")]
    [InlineData(CapabilityStatus.PermissionDenied, "Windows didn't allow access to the volume")]
    [InlineData(CapabilityStatus.Unknown, "Volume is unavailable")]
    public void VolumeNotes(CapabilityStatus capability, string? note) =>
        Assert.Equal(note, SystemPageText.VolumeNote(capability));

    [Theory]
    [InlineData(0, false, "0%")]
    [InlineData(42.4, false, "42%")]
    [InlineData(100, false, "100%")]
    [InlineData(42, true, "Muted")]
    public void VolumeText(double percent, bool muted, string text) =>
        Assert.Equal(text, SystemPageText.VolumeText(percent, muted));

    [Theory]
    [InlineData(ErrorCode.AccessDenied)]
    [InlineData(ErrorCode.Unsupported)]
    [InlineData(ErrorCode.DeviceUnavailable)]
    [InlineData(ErrorCode.Timeout)]
    [InlineData(ErrorCode.Unexpected)]
    public void FailureSentencesNeverRepeatTheRawMessage(ErrorCode code)
    {
        var text = SystemPageText.Failure(new OperationError(code, "SSID Home-Net 00:11:22"), "Wi-Fi");

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.DoesNotContain("Home-Net", text, StringComparison.Ordinal);
    }
}
