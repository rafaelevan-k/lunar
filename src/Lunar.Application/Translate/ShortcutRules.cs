namespace Lunar.Application.Translate;

/// <summary>Modifier keys of a global shortcut.</summary>
[Flags]
public enum ShortcutModifiers
{
    None = 0,
    Alt = 1,
    Ctrl = 2,
    Shift = 4,
    Win = 8
}

/// <summary>A global shortcut: modifiers and one key named as Windows names it ("T", "5", "F8", "Space").</summary>
public sealed record Shortcut(ShortcutModifiers Modifiers, string Key)
{
    /// <summary>"Ctrl+Alt+T": modifiers in a fixed order, then the key.</summary>
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ShortcutModifiers.Ctrl))
            parts.Add("Ctrl");
        if (Modifiers.HasFlag(ShortcutModifiers.Alt))
            parts.Add("Alt");
        if (Modifiers.HasFlag(ShortcutModifiers.Shift))
            parts.Add("Shift");
        if (Modifiers.HasFlag(ShortcutModifiers.Win))
            parts.Add("Win");
        parts.Add(Key);
        return string.Join('+', parts);
    }
}

/// <summary>
/// Reads a shortcut written as "Ctrl+Alt+T" (ADR-0028 §1). The key is a letter, a digit, F1–F12 or Space; at least Ctrl, Alt or Win must be held, so
/// the shortcut never steals ordinary typing. Shift alone is not enough.
/// </summary>
public static class ShortcutRules
{
    /// <summary>The shortcut, or null when the text is not one Lunar accepts.</summary>
    public static Shortcut? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 40)
            return null;
        var modifiers = ShortcutModifiers.None;
        string? key = null;
        foreach (var raw in text.Split('+'))
        {
            var part = raw.Trim();
            var modifier = part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => ShortcutModifiers.Ctrl,
                "ALT" => ShortcutModifiers.Alt,
                "SHIFT" => ShortcutModifiers.Shift,
                "WIN" or "WINDOWS" => ShortcutModifiers.Win,
                _ => ShortcutModifiers.None
            };
            if (modifier != ShortcutModifiers.None)
            {
                if (modifiers.HasFlag(modifier) || key is not null)
                    return null;
                modifiers |= modifier;
                continue;
            }

            if (key is not null)
                return null;
            key = NormalizeKey(part);
            if (key is null)
                return null;
        }

        if (key is null || (modifiers & (ShortcutModifiers.Ctrl | ShortcutModifiers.Alt | ShortcutModifiers.Win)) == 0)
            return null;
        return new Shortcut(modifiers, key);
    }

    private static string? NormalizeKey(string part)
    {
        if (part.Length == 1 && char.IsAsciiLetterOrDigit(part[0]))
            return part.ToUpperInvariant();
        if (part.Equals("Space", StringComparison.OrdinalIgnoreCase))
            return "Space";
        if (part.Length is 2 or 3 && (part[0] is 'F' or 'f') && int.TryParse(part.AsSpan(1), out var n) && n is >= 1 and <= 12 && part[1] != '0')
            return "F" + n;
        return null;
    }
}
