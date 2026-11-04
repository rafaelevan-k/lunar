using Lunar.Domain.Conversion;
using Lunar.Domain.Shared;

namespace Lunar.Application.Conversion.Models;

/// <summary>Why a file was not accepted, so the UI can say what to do and a test can tell the cases apart.</summary>
public enum RejectionReason
{
    /// <summary>The path is empty, relative, or has characters, names or parts Windows treats specially (devices, streams, "..").</summary>
    UnsafePath,

    /// <summary>The path is on a network share, which v1 does not read.</summary>
    NetworkPath,

    /// <summary>The path or a name in it is longer than allowed.</summary>
    PathTooLong,

    /// <summary>The path is not an existing file (missing, or a folder).</summary>
    NotAFile,

    /// <summary>The file exists but cannot be read now (in use, or no permission).</summary>
    Unreadable,

    /// <summary>The file lives only in the cloud and is not on this PC.</summary>
    OnlineOnly,

    /// <summary>The extension is not one of the supported formats.</summary>
    UnsupportedType,

    /// <summary>The file has no content.</summary>
    Empty,

    /// <summary>The file is larger than the limit.</summary>
    TooLarge,

    /// <summary>The content is a different format than the extension says.</summary>
    SignatureMismatch,

    /// <summary>The content is damaged or cut short.</summary>
    Corrupt,

    /// <summary>The picture has more pixels than the limit (or none).</summary>
    TooManyPixels,

    /// <summary>The Word package is larger, more compressed or has more entries than the limits allow.</summary>
    ArchiveLimits,

    /// <summary>The Word file carries macros, which a .docx cannot legitimately have.</summary>
    HasMacros,

    /// <summary>The file is an old or password-protected Word file, not a normal .docx.</summary>
    EncryptedOrLegacy,

    /// <summary>The same file was listed twice.</summary>
    Duplicate,

    /// <summary>The route from this format to the chosen one does not exist in v1.</summary>
    RouteUnsupported,

    /// <summary>The route needs a program that is not installed.</summary>
    RendererMissing,

    /// <summary>Overwrite was chosen, but the output would replace the input file itself.</summary>
    OutputWouldReplaceOriginal,

    /// <summary>No free output name could be found.</summary>
    NoFreeName
}

/// <summary>
/// One file that will be converted: where it comes from, what it is, which route it takes and exactly where the results go.
/// Planning happened at <see cref="PlannedAt"/> and read the file as it was then; the step that converts re-checks
/// <see cref="SourceLength"/> and <see cref="SourceLastWriteUtc"/> so a file swapped after planning is not trusted.
/// </summary>
/// <param name="Index">Position of the file in the request (zero based), stable for progress reporting.</param>
/// <param name="SourcePath">Full path of the input file. It is only ever opened for reading.</param>
/// <param name="SourceFormat">The format the content was confirmed to be.</param>
/// <param name="SourceLength">Size in bytes when it was planned.</param>
/// <param name="SourceLastWriteUtc">Last write time when it was planned.</param>
/// <param name="Route">How and with what fidelity it is converted.</param>
/// <param name="OutputDirectory">Full path of the folder for the result.</param>
/// <param name="OutputBaseName">The sanitized, unique name without extension; for a route with one output per page, the part before "_page_".</param>
/// <param name="OutputExtension">The extension with the dot, from the target format.</param>
/// <param name="WasRenamed">True when the name was changed to avoid a collision.</param>
/// <param name="ReplacesExisting">True when an existing output file will be replaced (only when the user chose Overwrite).</param>
public sealed record PlannedConversion(
    int Index,
    string SourcePath,
    FileFormat SourceFormat,
    long SourceLength,
    DateTime SourceLastWriteUtc,
    ConversionRoute Route,
    string OutputDirectory,
    string OutputBaseName,
    string OutputExtension,
    bool WasRenamed,
    bool ReplacesExisting)
{
    /// <summary>How to convert (JPEG quality, page layout, PDF resolution and pages, PDF password). Set when the job is queued; the defaults otherwise.</summary>
    public ConversionChoices Choices { get; init; } = ConversionChoices.Default;

    /// <summary>The caps this job runs under: the ones planning used. The steps that run later enforce the rest with them.</summary>
    public ConversionLimits Limits { get; init; } = new();

    /// <summary>The file name of the result for a route with a single output; null when each page is its own file.</summary>
    public string? OutputFileName => Route.ProducesOneOutputPerPage ? null : OutputBaseName + OutputExtension;

    /// <summary>
    /// Full path of the result for a route with a single output; null when each page is its own file. Joined as text (the
    /// Application layer touches no file API), with a backslash unless the folder already ends in one (a drive root).
    /// </summary>
    public string? OutputPath => OutputFileName is { } name
        ? (OutputDirectory.EndsWith('\\') ? OutputDirectory + name : OutputDirectory + "\\" + name)
        : null;
}

/// <summary>A file left alone because its output name was taken and the user chose to skip.</summary>
public sealed record SkippedFile(int Index, string FileName);

/// <summary>A file that was not accepted, with a message that says what to do. The message never contains a path or file content.</summary>
public sealed record RejectedFile(int Index, string FileName, RejectionReason Reason, OperationError Error);

/// <summary>
/// The outcome of planning a request: what will run, what was skipped on purpose and what was refused and why. A partly
/// accepted batch is reported as it is; nothing is hidden or silently dropped. Each file of the request appears in exactly one of
/// the three lists.
/// </summary>
/// <param name="Jobs">The files to convert, in request order.</param>
/// <param name="Skipped">Files left alone by the Skip policy.</param>
/// <param name="Rejected">Files that cannot be converted, each with its reason.</param>
/// <param name="Limits">The caps in force, for the steps that enforce what planning cannot see (page count, size after unpacking).</param>
/// <param name="PlannedAt">When the files were read.</param>
public sealed record ConversionPlan(
    IReadOnlyList<PlannedConversion> Jobs,
    IReadOnlyList<SkippedFile> Skipped,
    IReadOnlyList<RejectedFile> Rejected,
    ConversionLimits Limits,
    DateTimeOffset PlannedAt)
{
    /// <summary>True when nothing will be converted.</summary>
    public bool IsEmpty => Jobs.Count == 0;

    /// <summary>True when at least one job needs an installed layout engine.</summary>
    public bool RequiresExternalRenderer => Jobs.Any(j => j.Route.RequiresExternalRenderer);
}
