using Lunar.Application.System.Models;

namespace Lunar.Windows.Network;

/// <summary>
/// Turns adapter readings into the <see cref="WifiState"/> of the port. Pure: no I/O, so every combination of adapter,
/// radio and connection can be tested without hardware.
/// </summary>
internal static class WifiStateMapper
{
    /// <summary>No adapter at all, or the WLAN service is not running: nothing to show or toggle.</summary>
    public static WifiState NoAdapter { get; } =
        new(CapabilityStatus.Unavailable, RadioState.Unavailable, WifiConnectivity.Disconnected, false);

    /// <summary>This Windows cannot do Native Wi-Fi.</summary>
    public static WifiState NotSupported { get; } =
        new(CapabilityStatus.Unsupported, RadioState.Unavailable, WifiConnectivity.Unknown, false);

    /// <summary>Windows refused the status query.</summary>
    public static WifiState Denied { get; } =
        new(CapabilityStatus.PermissionDenied, RadioState.Unknown, WifiConnectivity.Unknown, false);

    public static WifiState Map(IReadOnlyList<WlanInterfaceReading> adapters)
    {
        if (adapters.Count == 0)
            return NoAdapter;

        var anyEnabled = false;
        var anyDisabled = false;
        var anyConnected = false;
        var anyKnownState = false;
        var canToggle = false;

        foreach (var adapter in adapters)
        {
            var radio = RadioOf(adapter);

            // Associated means the radio is on, whatever a driver says about its switches, unless its switches say off:
            // that is a switch-off still being processed, and it must not read as connected.
            var connected = adapter.State == WlanInterfaceState.Connected && radio != RadioState.Disabled;
            if (connected && radio == RadioState.Unknown)
                radio = RadioState.Enabled;

            anyEnabled |= radio == RadioState.Enabled;
            anyDisabled |= radio == RadioState.Disabled;
            anyConnected |= connected;
            anyKnownState |= adapter.State is not (WlanInterfaceState.Unknown or WlanInterfaceState.NotReady);
            canToggle |= CanToggle(adapter);
        }

        var radioState = anyEnabled ? RadioState.Enabled : anyDisabled ? RadioState.Disabled : RadioState.Unknown;

        // Connected only when Windows says an adapter is associated; an enabled radio alone never counts.
        var connectivity = anyConnected
            ? WifiConnectivity.Connected
            : anyKnownState || radioState == RadioState.Disabled ? WifiConnectivity.Disconnected : WifiConnectivity.Unknown;

        return new WifiState(CapabilityStatus.Supported, radioState, connectivity, canToggle);
    }

    // On when some PHY has both switches on; Off when some PHY is switched off by either switch and none is on; otherwise unknown.
    private static RadioState RadioOf(WlanInterfaceReading adapter)
    {
        if (adapter.Phys is not { Count: > 0 } phys)
            return RadioState.Unknown;

        var anyOn = false;
        var anyOff = false;
        foreach (var phy in phys)
        {
            if (phy.Software == RadioSwitch.On && phy.Hardware == RadioSwitch.On)
                anyOn = true;
            else if (phy.Software == RadioSwitch.Off || phy.Hardware == RadioSwitch.Off)
                anyOff = true;
        }

        return anyOn ? RadioState.Enabled : anyOff ? RadioState.Disabled : RadioState.Unknown;
    }

    // A software write can only help while the hardware switch is not off, and only if the driver reports the switches.
    private static bool CanToggle(WlanInterfaceReading adapter)
    {
        if (adapter.Phys is not { Count: > 0 } phys)
            return false;

        foreach (var phy in phys)
        {
            if (phy.Software != RadioSwitch.Unknown && phy.Hardware == RadioSwitch.On)
                return true;
        }

        return false;
    }
}
