using Lunar.Application.System.Models;

namespace Lunar.Windows.Bluetooth;

/// <summary>Turns radio readings into the <see cref="BluetoothState"/> of the port. Pure: no I/O, testable without hardware.</summary>
internal static class BluetoothStateMapper
{
    /// <summary>No Bluetooth radio at all: nothing to show or toggle.</summary>
    public static BluetoothState NoRadio { get; } = new(CapabilityStatus.Unavailable, RadioState.Unavailable, false);

    /// <summary>This Windows has no usable radio API.</summary>
    public static BluetoothState NotSupported { get; } = new(CapabilityStatus.Unsupported, RadioState.Unavailable, false);

    /// <summary>Windows refused the status query.</summary>
    public static BluetoothState Denied { get; } = new(CapabilityStatus.PermissionDenied, RadioState.Unknown, false);

    public static BluetoothState Map(IReadOnlyList<BluetoothRadioReading> radios)
    {
        if (radios.Count == 0)
            return NoRadio;

        var anyOn = false;
        var anyOff = false;
        var canToggle = false;
        foreach (var radio in radios)
        {
            anyOn |= radio.Mode == BluetoothRadioMode.On;
            anyOff |= radio.Mode is BluetoothRadioMode.Off or BluetoothRadioMode.Disabled;
            canToggle |= radio.Mode is BluetoothRadioMode.On or BluetoothRadioMode.Off; // Disabled: the system holds it off
        }

        var state = anyOn ? RadioState.Enabled : anyOff ? RadioState.Disabled : RadioState.Unknown;
        return new BluetoothState(CapabilityStatus.Supported, state, canToggle);
    }
}
