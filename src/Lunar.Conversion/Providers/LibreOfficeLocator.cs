using System.Diagnostics;

namespace Lunar.Conversion.Providers;

/// <summary>An installed LibreOffice that passed the checks: where its launcher is and which version it is.</summary>
/// <param name="ExecutablePath">Full path of <c>soffice.exe</c>.</param>
/// <param name="Version">The version its file says.</param>
public sealed record LibreOfficeInfo(string ExecutablePath, Version Version);

/// <summary>Finds the user's own LibreOffice (BE-25). Never installs, downloads or starts anything.</summary>
public interface ILibreOfficeLocator
{
    /// <summary>The installed LibreOffice, or null when there is none Lunar may use.</summary>
    LibreOfficeInfo? Locate();
}

/// <summary>
/// Finds an installed LibreOffice by looking only where its installer puts it: <c>soffice.exe</c> in the <c>LibreOffice\program</c> folder
/// under Program Files (64-bit and 32-bit). Those folders are writable only with administrator rights, which is why nothing else is
/// searched: not PATH, not the current folder, not a folder a document or setting names, because any of those could point at a
/// program put there by something else.
///
/// A candidate is accepted only when it is a full local path, the file really exists and is not a link to elsewhere (nor is its
/// folder), its file description says LibreOffice, and its major version is at least <see cref="MinimumMajorVersion"/> (the first
/// release line whose headless conversion Lunar's arguments are written for). The file is read for its version only; it is not run.
/// </summary>
public sealed class LibreOfficeLocator : ILibreOfficeLocator
{
    /// <summary>The oldest LibreOffice line Lunar will use.</summary>
    public const int MinimumMajorVersion = 7;

    private const string ExecutableName = "soffice.exe";

    private readonly string[] _programFolders;
    private readonly Func<string, (string? Product, Version? Version)> _readVersion;

    /// <summary>Looks in the two Program Files locations of this PC.</summary>
    public LibreOfficeLocator()
        : this(DefaultProgramFolders(), ReadVersion)
    {
    }

    /// <param name="programFolders">The folders that may hold <c>soffice.exe</c> (for tests).</param>
    /// <param name="readVersion">Reads the product name and version of a file (for tests).</param>
    internal LibreOfficeLocator(IEnumerable<string> programFolders, Func<string, (string? Product, Version? Version)> readVersion)
    {
        ArgumentNullException.ThrowIfNull(programFolders);
        ArgumentNullException.ThrowIfNull(readVersion);
        _programFolders = [.. programFolders];
        _readVersion = readVersion;
    }

    public LibreOfficeInfo? Locate()
    {
        foreach (var folder in _programFolders)
        {
            if (Check(folder) is { } found)
                return found;
        }

        return null;
    }

    private LibreOfficeInfo? Check(string folder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder) || folder.StartsWith(@"\\", StringComparison.Ordinal))
                return null;

            var path = Path.Combine(folder, ExecutableName);
            var file = new FileInfo(path);
            var directory = new DirectoryInfo(folder);
            if (!file.Exists || !directory.Exists)
                return null;
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                return null;

            var (product, version) = _readVersion(path);
            if (product is null || !product.Contains("LibreOffice", StringComparison.OrdinalIgnoreCase))
                return null;
            if (version is null || version.Major < MinimumMajorVersion)
                return null;

            return new LibreOfficeInfo(path, version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null; // a folder that cannot be read is a folder without LibreOffice
        }
    }

    private static IEnumerable<string> DefaultProgramFolders()
    {
        foreach (var special in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var root = Environment.GetFolderPath(special);
            if (!string.IsNullOrEmpty(root))
                yield return Path.Combine(root, "LibreOffice", "program");
        }
    }

    private static (string? Product, Version? Version) ReadVersion(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        var name = info.ProductName ?? info.FileDescription;
        return (name, new Version(info.ProductMajorPart, info.ProductMinorPart, info.ProductBuildPart, info.ProductPrivatePart));
    }
}
