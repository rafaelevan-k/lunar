namespace Lunar.Domain.Conversion;

/// <summary>
/// Pure rules for the names of converted files (PRD section 5, FR-CON-05): a name made from the input's name is cleaned so it
/// is a valid, harmless Windows file name, results of a paged source are named "name_page_001.png", and a taken name is
/// followed by a counter, "name (2)". It works on text only and never looks at a disk; whether a name is taken is decided by
/// the planner.
/// </summary>
public static class OutputNaming
{
    /// <summary>The name used when nothing usable is left of the input's name.</summary>
    public const string FallbackBaseName = "converted";

    /// <summary>The longest name of one file or folder (NTFS limit).</summary>
    public const int MaxNameChars = 255;

    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    ];

    /// <summary>
    /// True for a name Windows treats as a device ("CON", "nul.txt", "COM1 .png"): the part before the first dot, ignoring
    /// case and trailing spaces, is a reserved device name. Such a name must never be created or opened as a file.
    /// </summary>
    public static bool IsReservedDeviceName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        var dot = name.IndexOf('.');
        var stem = (dot < 0 ? name : name[..dot]).TrimEnd(' ');
        return Array.Exists(ReservedNames, reserved => string.Equals(reserved, stem, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A safe base name (no extension) made from <paramref name="name"/>: characters Windows forbids in a file name and control
    /// characters become "_", leading and trailing spaces and trailing dots go, a device name gets a "_" in front, an empty
    /// result becomes <see cref="FallbackBaseName"/>, and the length is cut to <paramref name="maxChars"/> without splitting a
    /// character. Unicode letters are kept as they are.
    /// </summary>
    public static string SanitizeBaseName(string? name, int maxChars = MaxNameChars)
    {
        if (maxChars < 1)
            throw new ArgumentOutOfRangeException(nameof(maxChars), maxChars, "At least one character must be allowed.");

        var chars = (name ?? string.Empty).ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] < ' ' || chars[i] == '\u007f' || "<>:\"/\\|?*".Contains(chars[i], StringComparison.Ordinal))
                chars[i] = '_';
        }

        var text = Trim(new string(chars));
        if (text.Length == 0)
            text = FallbackBaseName;
        if (IsReservedDeviceName(text))
            text = "_" + text;

        if (text.Length > maxChars)
        {
            var cut = maxChars;
            if (char.IsHighSurrogate(text[cut - 1]))
                cut--;
            text = Trim(text[..cut]);
            if (text.Length == 0)
                text = FallbackBaseName[..Math.Min(FallbackBaseName.Length, maxChars)];
        }

        return text;
    }

    /// <summary>"name (2)" for counter 2; a counter below 2 returns the name unchanged.</summary>
    public static string WithCounter(string baseName, int counter)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseName);
        return counter < 2 ? baseName : $"{baseName} ({counter})";
    }

    /// <summary>
    /// The name of one page of a paged source: "name_page_001.png". Pages count from 1; the number has at least three digits and
    /// as many as <paramref name="pageCount"/> needs, so names sort in page order.
    /// </summary>
    public static string PageFileName(string baseName, int page, int pageCount, string extension)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseName);
        ArgumentException.ThrowIfNullOrEmpty(extension);
        if (page < 1 || pageCount < 1 || page > pageCount)
            throw new ArgumentOutOfRangeException(nameof(page), page, "A page number runs from 1 to the page count.");

        var width = Math.Max(3, pageCount.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
        return baseName + "_page_" + page.ToString("D" + width, System.Globalization.CultureInfo.InvariantCulture) + extension;
    }

    /// <summary>The glob that finds every page file of a base name: "name_page_*.png" (the base name has no wildcard).</summary>
    public static string PagePattern(string baseName, string extension)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseName);
        ArgumentException.ThrowIfNullOrEmpty(extension);
        return baseName + "_page_*" + extension;
    }

    private static string Trim(string text) => text.Trim(' ').TrimEnd('.', ' ').TrimStart(' ');
}
