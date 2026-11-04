using System.Text.RegularExpressions;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Conversion.Models;
using Lunar.Conversion.Pipeline;
using Lunar.Conversion.Validation;
using Lunar.Domain.Conversion;
using Lunar.Domain.Shared;

namespace Lunar.Conversion.Output;

/// <summary>
/// Moves the finished results of one job from its private working folder into the user's folder, all or nothing (ARCHITECTURE section
/// 7.4, PRD FR-CON-04/05). The user's own files are only ever touched at the very end, and only by renaming:
/// 1. <b>Check</b> every staged result: it is a file inside the job's working folder, and it really is a valid file of the target format
///    within the limits (a result cut short by a full disk or a crash fails here), so a bad result is discarded before anything is written.
/// 2. <b>Make room first</b>: the free space of the destination volume is compared with what is needed.
/// 3. <b>Copy</b> each result to a temporary part file in the destination folder (a name Lunar makes up, on the same volume as the
///    final name, written through to the disk). A full disk, a cancel or any error shows up here, while no final name exists yet; the parts
///    are deleted and the folder is as it was.
/// 4. <b>Publish</b> by renaming each part to its final name, which cannot overwrite (a rename fails if the name exists, so a file
///    that appeared meanwhile is never lost): if a name is taken at this moment, the whole set moves to the next free "name (n)". A
///    result is therefore either absent or complete, never half-written. Replacing an existing output happens only when the plan said the
///    user chose Overwrite for that name, only for the first name tried, and last. If publishing fails part way, what this job published is
///    renamed back and removed.
///
/// The input file is never opened for writing, and a name equal to the input's path is treated as taken. Cancellation is honoured while
/// checking and copying, not while publishing (a few renames). The one thing that cannot be undone: if Overwrite replaced earlier outputs
/// and a later rename then fails, those replaced files stay replaced. Messages carry no path.
/// </summary>
public sealed partial class OutputCommitter
{
    private const int MaxCounter = 9_999;
    private const long SpaceMargin = 1024 * 1024;

    private readonly IOutputFileSystem _fileSystem;
    private readonly IDiagnosticLog? _log;

    public OutputCommitter(IDiagnosticLog? log = null)
        : this(new PhysicalOutputFileSystem(), log)
    {
    }

    internal OutputCommitter(IOutputFileSystem fileSystem, IDiagnosticLog? log)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _fileSystem = fileSystem;
        _log = log;
    }

    /// <summary>
    /// Publishes the staged results of <paramref name="job"/>. A route with one output per page takes one staged file per page in page
    /// order; any other route takes exactly one. Returns the full paths of the published files, in order.
    /// </summary>
    public async Task<Result<IReadOnlyList<string>>> CommitAsync(
        PlannedConversion job,
        TempWorkspace workspace,
        IReadOnlyList<string> stagedFiles,
        ConversionLimits limits,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(stagedFiles);
        ArgumentNullException.ThrowIfNull(limits);
        if (ct.IsCancellationRequested)
            return Result.Cancelled<IReadOnlyList<string>>();

        var perPage = job.Route.ProducesOneOutputPerPage;
        if (!job.Route.IsSupported || stagedFiles.Count == 0 || stagedFiles.Count > limits.MaxPages || (!perPage && stagedFiles.Count != 1))
            return Fail(ErrorCode.Unexpected, "The conversion produced an unexpected number of results, so nothing was saved. Your original file is untouched.");

        var directory = job.OutputDirectory;
        if (!_fileSystem.DirectoryExists(directory))
            return Fail(ErrorCode.IoFailure, "The folder for the results is no longer available. Choose another folder and try again.", retryable: true);
        if ((new DirectoryInfo(directory).Attributes & FileAttributes.ReparsePoint) != 0)
            return Fail(ErrorCode.IoFailure, "The folder for the results is now a link to another place, so nothing was saved. Choose the real folder instead.");

        // 1. check what was produced, before anything is written next to the user's files
        long needed = 0;
        foreach (var staged in stagedFiles)
        {
            if (!workspace.Owns(staged) || !File.Exists(staged))
                return Fail(ErrorCode.Unexpected, "A result was missing, so nothing was saved. Your original file is untouched.");

            var (inspected, failure) = SourceFileValidator.Inspect(staged, job.Route.Target, limits);
            if (failure is not null)
                return Fail(ErrorCode.Unexpected, "The result could not be verified, so it was discarded. Your original file is untouched.");
            needed += inspected!.Length;
        }

        // 2. make room first
        if (_fileSystem.GetFreeBytes(directory) is { } free && free < needed + SpaceMargin)
            return Fail(ErrorCode.IoFailure, DiskFullMessage, retryable: true);

        var parts = new List<string>(stagedFiles.Count);
        var published = false;
        try
        {
            // 3. copy to part files
            try
            {
                foreach (var staged in stagedFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    var part = Path.Combine(directory, TempWorkspace.NewPartFileName());
                    workspace.RecordPart(part);
                    parts.Add(part);
                    await _fileSystem.CopyToNewFileAsync(staged, part, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return Result.Cancelled<IReadOnlyList<string>>();
            }
            catch (Exception ex) when (IsIoProblem(ex))
            {
                return Result.Failure<IReadOnlyList<string>>(MapIo(ex));
            }

            if (ct.IsCancellationRequested)
                return Result.Cancelled<IReadOnlyList<string>>();

            // 4. publish by renaming
            var outcome = Publish(job, directory, parts, perPage);
            published = outcome.IsSuccess;
            return outcome;
        }
        finally
        {
            if (!published)
                DeleteParts(parts);
        }
    }

    private Result<IReadOnlyList<string>> Publish(PlannedConversion job, string directory, List<string> parts, bool perPage)
    {
        var (stem, start) = StartingName(job);
        for (var counter = start; counter < start + MaxCounter; counter++)
        {
            var baseName = OutputNaming.WithCounter(stem, counter);
            var names = perPage
                ? Enumerable.Range(1, parts.Count).Select(page => OutputNaming.PageFileName(baseName, page, parts.Count, job.OutputExtension)).ToArray()
                : [baseName + job.OutputExtension];
            var finals = names.Select(name => Path.Combine(directory, name)).ToArray();

            // Which names are free now? Only the first name tried may replace an existing file, and only when the user chose Overwrite.
            var replace = new bool[finals.Length];
            var taken = false;
            for (var i = 0; i < finals.Length && !taken; i++)
            {
                if (_fileSystem.DirectoryExists(finals[i]) || string.Equals(finals[i], job.SourcePath, StringComparison.OrdinalIgnoreCase))
                    taken = true;
                else if (_fileSystem.Exists(finals[i]))
                {
                    if (job.ReplacesExisting && counter == start)
                        replace[i] = true;
                    else
                        taken = true;
                }
            }

            if (taken)
                continue;

            var attempt = TryPublish(parts, finals, replace);
            if (attempt.Raced)
                continue;
            return attempt.Result!;
        }

        return Fail(ErrorCode.IoFailure, "Lunar couldn't find a free name for the result in that folder. Choose another folder.");
    }

    // Creations first (they can be undone), replacements last (they cannot).
    private (Result<IReadOnlyList<string>>? Result, bool Raced) TryPublish(List<string> parts, string[] finals, bool[] replace)
    {
        var created = new List<int>();
        var current = -1;
        try
        {
            for (var i = 0; i < finals.Length; i++)
            {
                if (replace[i])
                    continue;
                current = i;
                _fileSystem.MoveWithoutOverwrite(parts[i], finals[i]);
                created.Add(i);
            }

            for (var i = 0; i < finals.Length; i++)
            {
                if (!replace[i])
                    continue;
                current = i;
                _fileSystem.ReplaceExisting(parts[i], finals[i]);
            }

            return (Result.Success<IReadOnlyList<string>>(finals), false);
        }
        catch (Exception ex) when (IsIoProblem(ex))
        {
            // A rename that fails because the name now exists means another program took it a moment ago: not a failure, the next name.
            var raced = ex is IOException && current >= 0 && !replace[current] && _fileSystem.Exists(finals[current]);

            // Put back what this attempt published: renamed back to its part file, or removed if that is not possible.
            foreach (var i in created)
            {
                try
                {
                    _fileSystem.MoveWithoutOverwrite(finals[i], parts[i]);
                }
                catch (Exception back) when (IsIoProblem(back))
                {
                    _fileSystem.TryDelete(finals[i]);
                    _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "conversion.commit.rollback_removed", "Conversion", "Commit", ErrorCode.IoFailure, ExceptionType: back.GetType()));
                    raced = false;
                }
            }

            return raced ? (null, true) : (Result.Failure<IReadOnlyList<string>>(MapIo(ex)), false);
        }
    }

    private void DeleteParts(IEnumerable<string> parts)
    {
        foreach (var part in parts)
        {
            if (!_fileSystem.TryDelete(part))
                _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "conversion.part.cleanup_failed", "Conversion", "Commit", ErrorCode.IoFailure));
        }
    }

    // A name the plan already moved aside ("photo (2)") continues from its own number instead of becoming "photo (2) (2)".
    private static (string Stem, int Start) StartingName(PlannedConversion job)
    {
        if (job.WasRenamed && Counted().Match(job.OutputBaseName) is { Success: true } match
            && int.TryParse(match.Groups[2].ValueSpan, out var number) && number >= 2)
        {
            return (match.Groups[1].Value, number);
        }

        return (job.OutputBaseName, 1);
    }

    private const string DiskFullMessage = "There is not enough space on the disk to save the result. Free some space and try again; your original file is untouched.";

    private static bool IsIoProblem(Exception ex) => ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException;

    private static OperationError MapIo(Exception ex) => ex switch
    {
        IOException io when IsDiskFull(io) => new OperationError(ErrorCode.IoFailure, DiskFullMessage, retryable: true),
        UnauthorizedAccessException => new OperationError(ErrorCode.AccessDenied, "Lunar can't save to that folder. Check that you may write there, or choose another folder."),
        PathTooLongException => new OperationError(ErrorCode.IoFailure, "The path for the result is too long. Choose a folder closer to the top of a drive."),
        DirectoryNotFoundException => new OperationError(ErrorCode.IoFailure, "The folder for the results is no longer available. Choose another folder and try again.", retryable: true),
        _ => new OperationError(ErrorCode.IoFailure, "The result couldn't be saved. Check the folder and try again; your original file is untouched.", retryable: true)
    };

    // ERROR_HANDLE_DISK_FULL (39) and ERROR_DISK_FULL (112), as the low word of the HRESULT.
    private static bool IsDiskFull(IOException io) => (io.HResult & 0xFFFF) is 39 or 112;

    private static Result<IReadOnlyList<string>> Fail(ErrorCode code, string message, bool retryable = false) =>
        Result.Failure<IReadOnlyList<string>>(new OperationError(code, message, retryable));

    [GeneratedRegex(@"^(.*) \((\d+)\)$", RegexOptions.CultureInvariant)]
    private static partial Regex Counted();
}
