using System.Globalization;
using Lunar.Application.System.Models;
using Lunar.Desktop.Controls;
using Lunar.Desktop.Resources;
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
            RadioState.Unavailable => (Strings.Get("System.NoWifi"), StatusKind.Unavailable),
            RadioState.Disabled => (Strings.Get("System.Off"), StatusKind.Neutral),
            RadioState.Enabled when state.Connectivity == WifiConnectivity.Connected => (Strings.Get("System.Connected"), StatusKind.Success),
            RadioState.Enabled when state.Connectivity == WifiConnectivity.Disconnected => (Strings.Get("System.OnNotConnected"), StatusKind.Info),
            RadioState.Enabled => (Strings.Get("System.On"), StatusKind.Info),
            _ => (Strings.Get("System.Unknown"), StatusKind.Neutral)
        };
    }

    public static (string Text, StatusKind Kind) Bluetooth(BluetoothState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (Capability(state.Capability, "Bluetooth") is { } blocked)
            return blocked;

        return state.Radio switch
        {
            RadioState.Unavailable => (Strings.Get("System.NoBluetooth"), StatusKind.Unavailable),
            RadioState.Disabled => (Strings.Get("System.Off"), StatusKind.Neutral),
            RadioState.Enabled => (Strings.Get("System.On"), StatusKind.Success),
            _ => (Strings.Get("System.Unknown"), StatusKind.Neutral)
        };
    }

    public static string UnknownHint => Strings.Get("System.UnknownHint");

    /// <summary>What the chip says while a switch request is out. An in-progress status, never a result.</summary>
    public static string Switching(bool enabling) => enabling ? Strings.Get("System.TurningOn") : Strings.Get("System.TurningOff");

    public static string? WifiHint(WifiState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Hint(state.Capability, state.Radio, state.CanToggle);
    }

    public static string? BluetoothHint(BluetoothState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Hint(state.Capability, state.Radio, state.CanToggle);
    }

    // One short sentence about why there is no switch. A machine with no adapter needs none: the chip says it.
    private static string? Hint(CapabilityStatus capability, RadioState radio, bool canToggle) => capability switch
    {
        CapabilityStatus.Unsupported => Strings.Get("System.HintUnsupported"),
        CapabilityStatus.PermissionDenied => Strings.Get("System.HintDenied"),
        CapabilityStatus.Unknown => UnknownHint,
        CapabilityStatus.Supported when !canToggle && radio is RadioState.Enabled or RadioState.Disabled => Strings.Get("System.HintCannotSwitch"),
        CapabilityStatus.Supported when radio == RadioState.Unknown => UnknownHint,
        _ => null
    };

    /// <summary>Whether a Settings link helps: it does for everything except a machine that simply has no adapter or radio.</summary>
    public static bool OffersSettings(CapabilityStatus capability, RadioState radio, bool canToggle) =>
        !canToggle && capability != CapabilityStatus.Unavailable && !(capability == CapabilityStatus.Supported && radio == RadioState.Unavailable);

    /// <summary>What the volume card says instead of the slider, or null when the slider is usable.</summary>
    public static string? VolumeNote(CapabilityStatus capability) => capability switch
    {
        CapabilityStatus.Supported => null,
        CapabilityStatus.Unavailable => Strings.Get("System.VolumeNoDevice"),
        CapabilityStatus.Unsupported => Strings.Get("System.VolumeUnsupported"),
        CapabilityStatus.PermissionDenied => Strings.Get("System.VolumeDenied"),
        _ => Strings.Get("System.VolumeUnavailable")
    };

    public static string VolumeText(double percent, bool muted) => muted ? Strings.Get("System.Muted") : Math.Round(percent).ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <summary>A short, user-safe sentence for a failed action. Never contains a device name or a raw exception message.</summary>
    public static string Failure(OperationError error, string subject)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Code switch
        {
            ErrorCode.AccessDenied => Strings.Format("System.FailDenied", subject),
            ErrorCode.Unsupported => Strings.Format("System.FailUnsupported", subject),
            ErrorCode.DeviceUnavailable => Strings.Format("System.FailUnavailable", subject),
            ErrorCode.Timeout => Strings.Get("System.FailTimeout"),
            _ => Strings.Format("System.FailOther", subject)
        };
    }

    private static (string Text, StatusKind Kind)? Capability(CapabilityStatus capability, string subject) => capability switch
    {
        CapabilityStatus.Supported => null,
        CapabilityStatus.Unsupported => (Strings.Get("System.NotSupported"), StatusKind.Unavailable),
        CapabilityStatus.PermissionDenied => (Strings.Get("System.AccessDenied"), StatusKind.Error),
        CapabilityStatus.Unavailable => (Strings.Get(subject == "Wi-Fi" ? "System.NoWifi" : "System.NoBluetooth"), StatusKind.Unavailable),
        _ => (Strings.Get("System.Unknown"), StatusKind.Neutral)
    };
}
