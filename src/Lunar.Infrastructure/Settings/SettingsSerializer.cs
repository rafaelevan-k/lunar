using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lunar.Application.Settings.Models;

namespace Lunar.Infrastructure.Settings;

/// <summary>
/// Maps the JSON tree to <see cref="LunarSettings"/> and back. Reading is lenient per field: a missing field is its
/// default, a field of the wrong type is its default and reported through <c>valuesReset</c>, unknown fields are ignored.
/// </summary>
internal static class SettingsSerializer
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static LunarSettings Read(JsonObject root, out bool valuesReset)
    {
        var reset = false;
        var defaults = LunarSettings.Defaults;

        var island = Section(root, "island", ref reset);
        var appearance = Section(root, "appearance", ref reset);
        var media = Section(root, "media", ref reset);
        var conversion = Section(root, "conversion", ref reset);
        var privacy = Section(root, "privacy", ref reset);
        var usage = Section(root, "usage", ref reset);

        var settings = new LunarSettings
        {
            LaunchAtStartup = Bool(root, "launchAtStartup", defaults.LaunchAtStartup, ref reset),
            Locale = Text(root, "locale", ref reset),
            Island = new IslandSettings
            {
                PreferredMonitorId = Text(island, "preferredMonitorId", ref reset),
                TopOffsetDip = Int(island, "topOffsetDip", defaults.Island.TopOffsetDip, ref reset)
            },
            Appearance = new AppearanceSettings
            {
                Theme = Theme(appearance, ref reset),
                ReduceMotion = Bool(appearance, "reduceMotion", defaults.Appearance.ReduceMotion, ref reset),
                EyeAnimationEnabled = Bool(appearance, "eyeAnimationEnabled", defaults.Appearance.EyeAnimationEnabled, ref reset),
                AutoCollapse = Bool(appearance, "autoCollapse", defaults.Appearance.AutoCollapse, ref reset)
            },
            Media = new MediaSettings { PreferredSessionId = Text(media, "preferredSessionId", ref reset) },
            Conversion = new ConversionSettings { OutputDirectory = Text(conversion, "outputDirectory", ref reset) },
            Privacy = new PrivacySettings
            {
                DiagnosticsEnabled = Bool(privacy, "diagnosticsEnabled", defaults.Privacy.DiagnosticsEnabled, ref reset)
            },
            Usage = new UsageSettings { Enabled = Bool(usage, "enabled", defaults.Usage.Enabled, ref reset) }
        };

        valuesReset = reset;
        return settings;
    }

    /// <summary>UTF-8 without a byte-order mark.</summary>
    public static byte[] Write(LunarSettings settings, int schemaVersion)
    {
        var root = new JsonObject
        {
            [SettingsMigration.VersionProperty] = schemaVersion,
            ["launchAtStartup"] = settings.LaunchAtStartup,
            ["locale"] = settings.Locale,
            ["island"] = new JsonObject
            {
                ["preferredMonitorId"] = settings.Island.PreferredMonitorId,
                ["topOffsetDip"] = settings.Island.TopOffsetDip
            },
            ["appearance"] = new JsonObject
            {
                ["theme"] = settings.Appearance.Theme.ToString(),
                ["reduceMotion"] = settings.Appearance.ReduceMotion,
                ["eyeAnimationEnabled"] = settings.Appearance.EyeAnimationEnabled,
                ["autoCollapse"] = settings.Appearance.AutoCollapse
            },
            ["media"] = new JsonObject { ["preferredSessionId"] = settings.Media.PreferredSessionId },
            ["conversion"] = new JsonObject { ["outputDirectory"] = settings.Conversion.OutputDirectory },
            ["privacy"] = new JsonObject { ["diagnosticsEnabled"] = settings.Privacy.DiagnosticsEnabled },
            ["usage"] = new JsonObject { ["enabled"] = settings.Usage.Enabled }
        };

        return new UTF8Encoding(false).GetBytes(root.ToJsonString(WriteOptions));
    }

    private static JsonObject? Section(JsonObject root, string name, ref bool reset)
    {
        var node = root[name];
        if (node is null)
            return null;
        if (node is JsonObject section)
            return section;

        reset = true;
        return null;
    }

    private static bool Bool(JsonObject? owner, string name, bool fallback, ref bool reset)
    {
        var node = owner?[name];
        if (node is null)
            return fallback;
        if (node is JsonValue value && value.TryGetValue<bool>(out var result))
            return result;

        reset = true;
        return fallback;
    }

    private static int Int(JsonObject? owner, string name, int fallback, ref bool reset)
    {
        var node = owner?[name];
        if (node is null)
            return fallback;
        if (node is JsonValue value && value.TryGetValue<int>(out var result))
            return result;

        reset = true;
        return fallback;
    }

    private static string? Text(JsonObject? owner, string name, ref bool reset)
    {
        var node = owner?[name];
        if (node is null)
            return null;
        if (node is JsonValue value && value.TryGetValue<string>(out var result))
            return result;

        reset = true;
        return null;
    }

    private static ThemePreference Theme(JsonObject? appearance, ref bool reset)
    {
        var text = Text(appearance, "theme", ref reset);
        if (text is null)
            return LunarSettings.Defaults.Appearance.Theme;

        // Names only: Enum.TryParse would also accept numeric strings such as "1".
        if (text.Length > 0 && char.IsAsciiLetter(text[0])
            && Enum.TryParse<ThemePreference>(text, ignoreCase: true, out var theme)
            && Enum.IsDefined(theme))
        {
            return theme;
        }

        reset = true;
        return LunarSettings.Defaults.Appearance.Theme;
    }
}
