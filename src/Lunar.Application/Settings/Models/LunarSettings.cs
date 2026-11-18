using Lunar.Domain.Focus;

namespace Lunar.Application.Settings.Models;

/// <summary>Preferred colour theme; the System value follows Windows.</summary>
public enum ThemePreference
{
    System,
    Dark,
    Light
}

/// <summary>The island's outline (ADR-0027): the floating Pill, or the Notch attached to the top edge of the screen.</summary>
public enum IslandStyle
{
    Pill,
    Notch
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

    /// <summary>Pill (the default) or Notch (ADR-0027). Applies at once.</summary>
    public IslandStyle IslandStyle { get; init; } = IslandStyle.Pill;
}

public sealed record MediaSettings
{
    /// <summary>Opaque id of the preferred media app/session. Never a title, artist or history.</summary>
    public string? PreferredSessionId { get; init; }

    /// <summary>The collapsed island shows the playing track's thumbnail and sound bars instead of the eyes (ADR-0026). On by default.</summary>
    public bool ShowInPill { get; init; } = true;
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

public sealed record UsageSettings
{
    /// <summary>
    /// The user turned the Usage page on (ADR-0014). Off by default: until it is on, Lunar makes no network request and reads no token.
    /// Turning it on is the user's consent that Lunar contacts Anthropic with their own token.
    /// </summary>
    public bool Enabled { get; init; }
}

public sealed record RecorderSettings
{
    /// <summary>Absolute local folder for recordings, or null for the default. Never logged.</summary>
    public string? OutputDirectory { get; init; }

    /// <summary>Opaque id of the chosen microphone, or null for Automatic (the Windows default). Never logged.</summary>
    public string? InputDeviceId { get; init; }
}

public sealed record FocusSettings
{
    /// <summary>The minutes of the last focus session (ADR-0023).</summary>
    public int LastMinutes { get; init; } = FocusTimerRules.DefaultMinutes;
}

public sealed record UpdateSettings
{
    /// <summary>Lunar asks GitHub once a day whether a newer version was released (ADR-0025). On by default; off means no request at all.</summary>
    public bool Check { get; init; } = true;
}

/// <summary>Screen translation (ADR-0028). The key is not here: it is kept encrypted on its own (<c>ITranslationKeyStore</c>).</summary>
public sealed record TranslateSettings
{
    public const string DefaultShortcut = "Ctrl+Alt+T";
    public const string AzureProvider = "Azure";

    /// <summary>Off: no shortcut, no button, no request.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The language to translate into; null follows Lunar's language (English UI → Indonesian, Indonesian UI → English).</summary>
    public Lunar.Application.Abstractions.Translate.TranslationLanguage? Target { get; init; }

    /// <summary>The global shortcut, such as "Ctrl+Alt+T".</summary>
    public string Shortcut { get; init; } = DefaultShortcut;

    /// <summary>The service; only Azure AI Translator in this version.</summary>
    public string Provider { get; init; } = AzureProvider;

    /// <summary>The Azure resource's region, or null for a global resource. Not private: it names a data centre region.</summary>
    public string? Region { get; init; }

    /// <summary>The user allowed sending recognised text to the service (the one-time consent screen).</summary>
    public bool Consented { get; init; }
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

    public UsageSettings Usage { get; init; } = new();

    public RecorderSettings Recorder { get; init; } = new();

    public FocusSettings Focus { get; init; } = new();

    public UpdateSettings Updates { get; init; } = new();

    public TranslateSettings Translate { get; init; } = new();
}
