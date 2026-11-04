using System.Globalization;
using Lunar.Application.System.Models;
using Lunar.Desktop.Controls;
using Lunar.Domain.Shared;

namespace Lunar.Desktop.Features.SystemControls;

/// <summary>
/// The words and status kinds of the System page for every state a port can report. Pure: no UI objects and no Windows
/// calls, so every unknown, unsupported, denied and missing-device case is tested without a window. Colour is never the
/// only signal: each <see cref="StatusKind"/> also has its own symbol (FE-12).
/// </summary>
internal static class SystemPageText
{
    public static (string Text, StatusKind Kind) Wifi(WifiState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (Capability(state.Capability, "Wi-Fi") is { } blocked)
            return blocked;

        return state.Radio switch
        {
            RadioState.Unavailable => ("No Wi-Fi adapter", StatusKind.Unavailable),
            RadioState.Disabled => ("Off", StatusKind.Neutral),
            RadioState.Enabled when state.Connectivity == WifiConnectivity.Connected => ("Connected", StatusKind.Success),
            RadioState.Enabled when state.Connectivity == WifiConnectivity.Disconnected => ("On, not connected", StatusKind.Info),
            RadioState.Enabled => ("On", StatusKind.Info),
            _ => ("Unknown", StatusKind.Neutral)
        };
    }

    public static (string Text, StatusKind Kind) Bluetooth(BluetoothState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (Capability(state.Capability, "Bluetooth") is { } blocked)
            return blocked;

        return state.Radio switch
        {
            RadioState.Unavailable => ("No Bluetooth radio", StatusKind.Unavailable),
            RadioState.Disabled => ("Off", StatusKind.Neutral),
            RadioState.Enabled => ("On", StatusKind.Success),
            _ => ("Unknown", StatusKind.Neutral)
        };
    }

    /// <summary>Whether a Settings link helps: it does for everything except a machine that simply has no adapter or radio.</summary>
    public static bool OffersSettings(CapabilityStatus capability, RadioState radio, bool canToggle) =>
        !canToggle && capability != CapabilityStatus.Unavailable && !(capability == CapabilityStatus.Supported && radio == RadioState.Unavailable);

    /// <summary>What the volume card says instead of the slider, or null when the slider is usable.</summary>
    public static string? VolumeNote(CapabilityStatus capability) => capability switch
    {
        CapabilityStatus.Supported => null,
        CapabilityStatus.Unavailable => "No audio output device",
        CapabilityStatus.Unsupported => "Volume can't be controlled on this PC",
        CapabilityStatus.PermissionDenied => "Windows didn't allow access to the volume",
        _ => "Volume is unavailable"
    };

    public static string VolumeText(double percent, bool muted) => muted ? "Muted" : Math.Round(percent).ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <summary>A short, user-safe sentence for a failed action. Never contains a device name or a raw exception message.</summary>
    public static string Failure(OperationError error, string subject)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Code switch
        {
            ErrorCode.AccessDenied => $"Windows didn't allow this change. Use Settings to switch {subject}.",
            ErrorCode.Unsupported => $"{subject} can't be switched from here. Use Settings.",
            ErrorCode.DeviceUnavailable => $"{subject} isn't available.",
            ErrorCode.Timeout => "Windows didn't confirm the change. Try again.",
            _ => $"Couldn't change {subject}."
        };
    }

    private static (string Text, StatusKind Kind)? Capability(CapabilityStatus capability, string subject) => capability switch
    {
        CapabilityStatus.Supported => null,
        CapabilityStatus.Unsupported => ("Not supported", StatusKind.Unavailable),
        CapabilityStatus.PermissionDenied => ("Access denied", StatusKind.Error),
        CapabilityStatus.Unavailable => (subject == "Wi-Fi" ? "No Wi-Fi adapter" : "No Bluetooth radio", StatusKind.Unavailable),
        _ => ("Unknown", StatusKind.Neutral)
    };
}
