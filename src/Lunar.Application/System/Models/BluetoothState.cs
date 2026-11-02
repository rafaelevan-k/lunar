namespace Lunar.Application.System.Models;

public sealed record BluetoothState(CapabilityStatus Capability, RadioState Radio, bool CanToggle)
{
    public static BluetoothState Unknown { get; } = new(CapabilityStatus.Unknown, RadioState.Unknown, false);
}
