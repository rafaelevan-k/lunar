namespace Lunar.Application.System.Models;

/// <summary>Wi-Fi snapshot. The SSID is deliberately not part of this contract (privacy).</summary>
public sealed record WifiState(
    CapabilityStatus Capability,
    RadioState Radio,
    WifiConnectivity Connectivity,
    bool CanToggle)
{
    public static WifiState Unknown { get; } =
        new(CapabilityStatus.Unknown, RadioState.Unknown, WifiConnectivity.Unknown, false);
}
