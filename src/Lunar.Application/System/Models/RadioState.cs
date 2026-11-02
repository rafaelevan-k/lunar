namespace Lunar.Application.System.Models;

/// <summary>State of a radio (Wi-Fi, Bluetooth). "Enabled" never implies connected.</summary>
public enum RadioState
{
    Unknown,
    Unavailable,
    Disabled,
    Enabled
}
