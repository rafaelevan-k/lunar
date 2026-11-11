using System.Globalization;

namespace Lunar.Domain.Updates;

/// <summary>
/// A released Lunar version, <c>major.minor.patch</c> (ADR-0025). Release tags are written <c>vX.Y.Z</c>. A tag with a suffix such as <c>-beta</c> is
/// not a release Lunar offers, so it does not parse.
/// </summary>
public readonly record struct AppVersion(int Major, int Minor, int Patch) : IComparable<AppVersion>
{
    public static bool operator <(AppVersion left, AppVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(AppVersion left, AppVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(AppVersion left, AppVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(AppVersion left, AppVersion right) => left.CompareTo(right) >= 0;

    /// <summary>
    /// Reads <c>1.2.3</c> or <c>v1.2.3</c>; <c>1.2</c> counts as <c>1.2.0</c>, and a fourth part is allowed only when it is 0 (the file version
    /// <c>1.2.3.0</c>). Anything else (letters, a suffix, a negative or too large number) is refused.
    /// </summary>
    public static bool TryParse(string? text, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 32)
            return false;

        var span = text.Trim();
        if (span.StartsWith('v') || span.StartsWith('V'))
            span = span[1..];

        var parts = span.Split('.');
        if (parts.Length is < 2 or > 4)
            return false;

        var numbers = new int[4];
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length is 0 or > 6 || !part.All(char.IsAsciiDigit)
                || !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                return false;
        }

        if (parts.Length == 4 && numbers[3] != 0)
            return false;

        version = new AppVersion(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    /// <summary>The running assembly's version (its fourth part is ignored).</summary>
    public static AppVersion From(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return new AppVersion(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build));
    }

    public int CompareTo(AppVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0)
            return major;
        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
}

/// <summary>The fixed limits of the update check (ADR-0025).</summary>
public static class UpdateRules
{
    /// <summary>The first check waits a little after start, so it never competes with Lunar's own start.</summary>
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    /// <summary>The installer is about 70 MB; anything above this is refused before a byte is written.</summary>
    public const long MaxInstallerBytes = 300L * 1024 * 1024;

    /// <summary>The release asset Lunar installs.</summary>
    public const string InstallerAssetName = "Lunar.msi";

    /// <summary>64 lowercase or uppercase hexadecimal characters.</summary>
    public static bool IsSha256Hex(string? value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}
