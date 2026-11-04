using System.Text.RegularExpressions;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Domain.Shared;

namespace Lunar.Conversion.Pipeline;

/// <summary>
/// A private working folder for one conversion job (PRD FR-CON-04, ARCHITECTURE section 7.4). Everything a job writes while it works
/// goes here, never next to the user's files, so a job that stops half way (cancelled, failed, disk full, the app killed) leaves the
/// user's folders as they were. The folder is unique per job and named by a random identifier, never by anything from the user's
/// files; Lunar creates every file in it itself, under names it makes up.
///
/// Cleanup has three layers: <see cref="Dispose"/> deletes the folder when the job ends; a locked file (an antivirus scan, say) is
/// retried a few times and, if it still cannot go, reported to the log by event only and left for the next layer;
/// <see cref="RecoverAbandoned"/> removes every folder a previous run left behind, at start-up. The workspace also keeps a small
/// journal of the temporary part files the output step created in the user's destination folder, so a hard kill between "copied" and
/// "renamed" still gets those cleaned up. The journal holds paths, lives only as long as the folder, and is never logged.
///
/// The root should be under the user's own profile (<c>%LocalAppData%\Lunar\Temp</c>), whose default permissions keep it private.
/// </summary>
public sealed partial class TempWorkspace : IDisposable
{
    private const string JournalName = "journal.txt";
    private const string FolderPrefix = "job-";
    private const int DeleteAttempts = 4;

    private readonly IDiagnosticLog? _log;
    private readonly object _gate = new();
    private readonly string _journalPath;
    private bool _disposed;

    private TempWorkspace(string directory, IDiagnosticLog? log)
    {
        Directory = directory;
        _journalPath = Path.Combine(directory, JournalName);
        _log = log;
    }

    /// <summary>Full path of this job's folder.</summary>
    public string Directory { get; }

    /// <summary>Creates a new, empty working folder under <paramref name="rootDirectory"/> (which is created if needed).</summary>
    public static TempWorkspace Create(string rootDirectory, IDiagnosticLog? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var directory = Path.Combine(Path.GetFullPath(rootDirectory), FolderPrefix + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        return new TempWorkspace(directory, log);
    }

    /// <summary>A path inside this folder for a new file, named by a fresh identifier plus the given extension (such as ".png").</summary>
    public string NewFilePath(string extension)
    {
        ArgumentException.ThrowIfNullOrEmpty(extension);
        if (extension[0] != '.' || extension.Length is < 2 or > 16 || extension.AsSpan(1).IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789") >= 0)
            throw new ArgumentException("An extension is a dot and a few lower-case letters or digits.", nameof(extension));
        lock (_gate)
            ObjectDisposedException.ThrowIf(_disposed, this);
        return Path.Combine(Directory, Guid.NewGuid().ToString("N") + extension);
    }

    /// <summary>True when <paramref name="path"/> is a file directly inside this folder (no ".." tricks, no other place).</summary>
    public bool Owns(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            var full = Path.GetFullPath(path);
            return string.Equals(Path.GetDirectoryName(full), Directory, StringComparison.OrdinalIgnoreCase) && full != Directory;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes down, before the file is created, that a temporary part file may appear at <paramref name="partPath"/> so recovery can
    /// remove it if the app dies. Only names the output step makes (see <see cref="IsPartFileName"/>) are accepted.
    /// </summary>
    internal void RecordPart(string partPath)
    {
        if (!IsPartFile(partPath))
            throw new ArgumentException("Not a part file made by Lunar.", nameof(partPath));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            File.AppendAllText(_journalPath, partPath + Environment.NewLine);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        Remove(Directory, _log);
    }

    /// <summary>
    /// Deletes every working folder under <paramref name="rootDirectory"/>, and the part files their journals name, left by an earlier
    /// run. Call it once at start-up, before any job exists (Lunar runs one instance, so no folder there belongs to a live job).
    /// Returns how many folders were removed.
    /// </summary>
    public static int RecoverAbandoned(string rootDirectory, IDiagnosticLog? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        if (!System.IO.Directory.Exists(rootDirectory))
            return 0;

        var removed = 0;
        foreach (var folder in System.IO.Directory.EnumerateDirectories(rootDirectory, FolderPrefix + "*"))
        {
            // Only folders that look like ours: the prefix and 32 hex digits, and not a link to somewhere else.
            var name = Path.GetFileName(folder);
            if (!WorkspaceName().IsMatch(name) || (new DirectoryInfo(folder).Attributes & FileAttributes.ReparsePoint) != 0)
                continue;

            if (Remove(folder, log))
                removed++;
        }

        return removed;
    }

    /// <summary>True for the file names the output step gives its temporary copies: "~" and 32 hex digits and ".lunar-part".</summary>
    internal static bool IsPartFileName(string fileName) => PartName().IsMatch(fileName);

    internal static string NewPartFileName() => "~" + Guid.NewGuid().ToString("N") + ".lunar-part";

    // A journal line is trusted only when it is an absolute path to a file with a part name, so a damaged or tampered journal
    // cannot make Lunar delete anything else.
    private static bool IsPartFile(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path) && IsPartFileName(Path.GetFileName(path));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool Remove(string folder, IDiagnosticLog? log)
    {
        DeleteJournalledParts(folder, log);
        for (var attempt = 1; attempt <= DeleteAttempts; attempt++)
        {
            try
            {
                if (!System.IO.Directory.Exists(folder))
                    return true;
                System.IO.Directory.Delete(folder, recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == DeleteAttempts)
                {
                    log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "conversion.workspace.cleanup_failed", "Conversion", "Cleanup", ErrorCode.IoFailure, ExceptionType: ex.GetType()));
                    return false;
                }

                Thread.Sleep(50 * attempt); // a scanner or indexer may be holding the file for a moment
            }
        }

        return false;
    }

    private static void DeleteJournalledParts(string folder, IDiagnosticLog? log)
    {
        var journal = Path.Combine(folder, JournalName);
        try
        {
            if (!File.Exists(journal))
                return;
            foreach (var line in File.ReadLines(journal))
            {
                var path = line.Trim();
                if (path.Length == 0 || !IsPartFile(path))
                    continue;
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "conversion.part.cleanup_failed", "Conversion", "Cleanup", ErrorCode.IoFailure, ExceptionType: ex.GetType()));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "conversion.journal.unreadable", "Conversion", "Cleanup", ErrorCode.IoFailure, ExceptionType: ex.GetType()));
        }
    }

    [GeneratedRegex(@"^job-[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex WorkspaceName();

    [GeneratedRegex(@"^~[0-9a-f]{32}\.lunar-part$", RegexOptions.CultureInvariant)]
    private static partial Regex PartName();
}
