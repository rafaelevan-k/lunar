namespace Lunar.Application.Abstractions.System;

/// <summary>Whitelist of Windows Settings pages the app may open; callers never supply a URI.</summary>
public enum SettingsPage
{
    Wifi,
    Bluetooth,
    DateAndTime
}
