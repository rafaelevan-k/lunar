namespace Lunar.Application.Abstractions.System;

/// <summary>Whitelist of Windows Settings pages the app may open; callers never supply a URI.</summary>
public enum SettingsPage
{
    Wifi,
    Bluetooth,
    DateAndTime,

    /// <summary>Windows Settings &gt; Display, the fallback when a display's brightness cannot be changed from Lunar (ADR-0021).</summary>
    Display,

    /// <summary>Windows Settings &gt; Privacy &gt; Microphone, the way out when Windows blocks the microphone (ADR-0022).</summary>
    Microphone
}
