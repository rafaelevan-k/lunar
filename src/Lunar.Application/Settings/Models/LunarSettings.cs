namespace Lunar.Application.Settings.Models;

/// <summary>Preferred colour theme; the System value follows Windows.</summary>
public enum ThemePreference
{
    System,
    Dark,
    Light
}

public sealed record IslandSettings
{
    /// <summary>Display device name (<c>DisplayInfo.Id</c>); null means the primary display.</summary>
    public string? PreferredMonitorId { get; init; }

    public int TopOffsetDip { get; init; } = SettingsRules.DefaultTopOffsetDip;
}

public sealed record AppearanceSettings
{
    public ThemePreference Theme { get; init; } = ThemePreference.System;

    public bool ReduceMotion { get; init; }

    public bool EyeAnimationEnabled { get; init; } = true;

    public bool AutoCollapse { get; init; } = true;
}

public sealed record MediaSettings
{
    /// <summary>Opaque id of the preferred media app/session. Never a title, artist or history.</summary>
    public string? PreferredSessionId { get; init; }
}

public sealed record ConversionSettings
{
    /// <summary>Absolute local output folder, or null for the default. Never logged.</summary>
    public string? OutputDirectory { get; init; }
}

public sealed record PrivacySettings
{
    /// <summary>Opt-in verbose diagnostics (ADR-0009). Off by default.</summary>
    public bool DiagnosticsEnabled { get; init; }
}

/// <summary>
/// User preferences (ADR-0011). Immutable; every default is a safe value, so a missing file or field is never an
/// error. Values are checked and repaired by <see cref="SettingsRules.Normalize"/>.
/// </summary>
public sealed record LunarSettings
{
    public static LunarSettings Defaults { get; } = new();

    public bool LaunchAtStartup { get; init; }

    /// <summary>BCP-47 language tag, or null to follow Windows.</summary>
    public string? Locale { get; init; }

    public IslandSettings Island { get; init; } = new();

    public AppearanceSettings Appearance { get; init; } = new();

    public MediaSettings Media { get; init; } = new();

    public ConversionSettings Conversion { get; init; } = new();

    public PrivacySettings Privacy { get; init; } = new();
}
