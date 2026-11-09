using Lunar.Application.Settings.Models;

namespace Lunar.Application.Settings;

/// <summary>
/// Pure value rules for <see cref="LunarSettings"/> (ADR-0011). An invalid value is replaced by its default; valid
/// siblings are kept. Whether a folder is an absolute local path needs <c>System.IO</c> and is checked by the store.
/// </summary>
public static class SettingsRules
{
    public const int MaxIdLength = 128;
    public const int MaxPathLength = 260;
    public const int MaxLocaleLength = 35;
    public const int MinTopOffsetDip = 0;
    public const int MaxTopOffsetDip = 200;
    public const int DefaultTopOffsetDip = 8;

    /// <summary>Returns the repaired settings; <paramref name="changed"/> is true when any value was replaced.</summary>
    public static LunarSettings Normalize(LunarSettings settings, out bool changed)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var island = settings.Island ?? new IslandSettings();
        var appearance = settings.Appearance ?? new AppearanceSettings();
        var media = settings.Media ?? new MediaSettings();
        var conversion = settings.Conversion ?? new ConversionSettings();
        var privacy = settings.Privacy ?? new PrivacySettings();
        var recorder = settings.Recorder ?? new RecorderSettings();

        var normalized = settings with
        {
            Locale = Locale(settings.Locale),
            Island = island with
            {
                PreferredMonitorId = Token(island.PreferredMonitorId, MaxIdLength),
                TopOffsetDip = island.TopOffsetDip is >= MinTopOffsetDip and <= MaxTopOffsetDip
                    ? island.TopOffsetDip
                    : DefaultTopOffsetDip
            },
            Appearance = appearance with
            {
                Theme = Enum.IsDefined(appearance.Theme) ? appearance.Theme : ThemePreference.System
            },
            Media = media with { PreferredSessionId = Token(media.PreferredSessionId, MaxIdLength) },
            Conversion = conversion with { OutputDirectory = Token(conversion.OutputDirectory, MaxPathLength) },
            Privacy = privacy,
            Usage = settings.Usage ?? new UsageSettings(),
            Recorder = recorder with
            {
                OutputDirectory = Token(recorder.OutputDirectory, MaxPathLength),
                InputDeviceId = Token(recorder.InputDeviceId, MaxPathLength)
            }
        };

        changed = normalized != settings;
        return normalized;
    }

    /// <summary>Trimmed text of at most <paramref name="maxLength"/> characters without control characters, else null.</summary>
    private static string? Token(string? value, int maxLength)
    {
        if (value is null)
            return null;

        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.Length > maxLength)
            return null;

        foreach (var c in trimmed)
        {
            if (char.IsControl(c))
                return null;
        }

        return trimmed;
    }

    /// <summary>A plausible BCP-47 tag: starts with a letter, then ASCII letters, digits and '-'.</summary>
    private static string? Locale(string? value)
    {
        var token = Token(value, MaxLocaleLength);
        if (token is null || !char.IsAsciiLetter(token[0]))
            return null;

        foreach (var c in token)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-')
                return null;
        }

        return token;
    }
}
