using System.Globalization;
using System.Resources;

namespace Lunar.Desktop.Resources;

/// <summary>
/// The one place user-visible words come from (FE-24, ADR-0017). Texts live in <c>Strings.resx</c> (English, the neutral language) and
/// <c>Strings.id.resx</c> (Indonesian). A key missing in the chosen language falls back to English; a key missing in English is a
/// programming error that a test catches, so at run time it shows the key instead of throwing.
/// The language is fixed once at start (<see cref="Use"/>); until then it is English, so tests do not depend on the PC's language.
/// </summary>
internal static class Strings
{
    private static readonly ResourceManager Manager = new("Lunar.Desktop.Resources.Strings", typeof(Strings).Assembly);

    /// <summary>The language the texts are read in. Never null.</summary>
    internal static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en");

    /// <summary>Language tags Lunar has texts for.</summary>
    internal static IReadOnlyList<string> Supported { get; } = ["en", "id"];

    /// <summary>Chooses the language for texts from here on. Any tag Lunar has no texts for means English.</summary>
    internal static void Use(string? tag) => Culture = CultureInfo.GetCultureInfo(Resolve(tag));

    /// <summary>"en" or "id" for a stored choice (null, "en", "id", "id-ID", anything else).</summary>
    internal static string Resolve(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return "en";
        var primary = tag.Split('-', '_')[0].Trim().ToLowerInvariant();
        return Supported.Contains(primary) ? primary : "en";
    }

    /// <summary>The language for "follow Windows": Indonesian when Windows displays in Indonesian, English otherwise.</summary>
    internal static string FromWindows(CultureInfo windowsUiCulture) => Resolve(windowsUiCulture.Name);

    internal static string Get(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return Manager.GetString(key, Culture) ?? key;
    }

    internal static string Format(string key, params object?[] args) =>
        string.Format(Culture, Get(key), args);

    /// <summary>Every key in one language (for the parity tests); the neutral set is English.</summary>
    internal static IReadOnlyDictionary<string, string> All(string tag)
    {
        // The set belongs to the ResourceManager, which caches it: it must not be disposed here.
        var culture = CultureInfo.GetCultureInfo(tag);
        var set = Manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (set is null)
            return result;
        foreach (System.Collections.DictionaryEntry entry in set)
            if (entry.Key is string key && entry.Value is string value)
                result[key] = value;
        return result;
    }
}
