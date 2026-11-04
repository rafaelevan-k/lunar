using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Conversion.Models;
using Lunar.Conversion.Output;
using Lunar.Conversion.Providers;
using Lunar.Conversion.Validation;
using Lunar.Domain.Conversion;
using Lunar.Domain.Shared;

namespace Lunar.Conversion.Pipeline;

/// <summary>
/// Does one planned conversion from start to finish (BE-27): the executor the queue (BE-21) calls. It owns no conversion logic; it
/// chooses the provider the route names, gives it a private working folder (BE-26), and hands the result to the output step, so the
/// pieces built before it become one operation with one set of guarantees:
/// - The input is only ever read. Before anything is done it is checked against the plan (size and last write time), because a file can
///   be swapped between planning and now; a changed file is refused with advice to add it again.
/// - Everything the job makes goes into its own working folder, which is deleted when the job ends, however it ends. The user's folder
///   is touched only by the output step, all or nothing. A job that fails or is cancelled therefore leaves no file anywhere.
/// - Routes by mode: a copy under a new name (same encoding), PNG/JPEG pictures, a picture in a PDF or Word file, PDF pages as pictures.
///   A route that needs the installed layout engine (Word to PDF or pictures) is refused here with a clear message until that engine
///   is wired (BE-25, ADR-0005); the planner already refuses it when no engine is available.
/// - Progress is reported only where a page count is known (PDF pages); other routes show activity without a number.
/// - Expected problems come back as typed failures with a message that holds no path, no file name, no content and no password; an
///   unexpected exception becomes a fixed message, with only its type written to the log.
/// - Leftover working folders of an earlier run (the app was killed) are removed once, before the first job of this run makes its own
///   folder (<see cref="RecoverAbandonedAsync"/>; the first job waits for it, and the app may also call it at start-up).
/// </summary>
public sealed class ConversionOrchestrator : IConversionJobExecutor
{
    private readonly string _workRoot;
    private readonly ImageConversionProvider _images;
    private readonly PdfCreationProvider _pdfs;
    private readonly DocxEmbeddingProvider _docx;
    private readonly PdfRasterizationProvider _rasterizer;
    private readonly OutputCommitter _committer;
    private readonly IDiagnosticLog? _log;
    private readonly Lazy<Task<int>> _recovery;

    /// <param name="workRoot">Where working folders are made; under the user's profile (such as <c>%LocalAppData%\Lunar\Temp</c>) so its permissions keep it private.</param>
    public ConversionOrchestrator(
        string workRoot,
        ImageConversionProvider images,
        PdfCreationProvider pdfs,
        DocxEmbeddingProvider docx,
        PdfRasterizationProvider rasterizer,
        OutputCommitter committer,
        IDiagnosticLog? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workRoot);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(pdfs);
        ArgumentNullException.ThrowIfNull(docx);
        ArgumentNullException.ThrowIfNull(rasterizer);
        ArgumentNullException.ThrowIfNull(committer);
        _workRoot = Path.GetFullPath(workRoot);
        _images = images;
        _pdfs = pdfs;
        _docx = docx;
        _rasterizer = rasterizer;
        _committer = committer;
        _log = log;
        _recovery = new Lazy<Task<int>>(() => Task.Run(() => TempWorkspace.RecoverAbandoned(_workRoot, _log)), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Removes the working folders (and the part files their journals name) that an earlier run left behind. It runs once, off the
    /// caller's thread, however often it is called; later calls return the first call's count. It is safe to skip: the first job
    /// calls it itself.
    /// </summary>
    public Task<int> RecoverAbandonedAsync() => _recovery.Value;

    public async Task<Result<IReadOnlyList<string>>> ExecuteAsync(PlannedConversion job, IProgress<JobProgress> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(progress);
        if (ct.IsCancellationRequested)
            return Result.Cancelled<IReadOnlyList<string>>();

        try
        {
            // Recovery finishes before this run makes any working folder of its own, so it can never remove a live one.
            await RecoverAbandonedAsync().WaitAsync(ct).ConfigureAwait(false);

            var unchanged = CheckSourceUnchanged(job);
            if (unchanged is not null)
                return Result.Failure<IReadOnlyList<string>>(unchanged);

            using var workspace = TempWorkspace.Create(_workRoot, _log);
            return await RunAsync(job, workspace, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled<IReadOnlyList<string>>();
        }
        catch (Exception ex) when (IoErrors.IsIoProblem(ex))
        {
            return Result.Failure<IReadOnlyList<string>>(IoErrors.Map(ex));
        }
        catch (Exception ex)
        {
            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Error, "conversion.job.fault", "Conversion", "Orchestrate", ErrorCode.Unexpected, ExceptionType: ex.GetType()));
            return Fail(ErrorCode.Unexpected, "The conversion stopped unexpectedly. Your original file is untouched.", retryable: true);
        }
    }

    private async Task<Result<IReadOnlyList<string>>> RunAsync(PlannedConversion job, TempWorkspace workspace, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var limits = job.Limits;
        var choices = job.Choices;
        try
        {
            choices.Validated();
            limits.Validated();
        }
        catch (ArgumentException)
        {
            return Fail(ErrorCode.InvalidInput, "One of the conversion choices is not valid.");
        }

        var route = job.Route;
        switch (route.Mode)
        {
            case ConversionMode.SameFormat:
                {
                    var staged = workspace.NewFilePath(job.OutputExtension);
                    var copied = await CopyAsync(job, staged, limits, ct).ConfigureAwait(false);
                    return copied.IsSuccess ? await _committer.CommitAsync(job, workspace, [staged], limits, ct).ConfigureAwait(false) : Carry(copied);
                }

            case ConversionMode.Native:
                {
                    var staged = workspace.NewFilePath(job.OutputExtension);
                    var made = await _images.ConvertAsync(job.SourcePath, job.SourceFormat, staged, route.Target, choices.Image, limits, ct).ConfigureAwait(false);
                    return made.IsSuccess ? await _committer.CommitAsync(job, workspace, [staged], limits, ct).ConfigureAwait(false) : Carry(made);
                }

            case ConversionMode.Embed:
                {
                    var staged = workspace.NewFilePath(job.OutputExtension);
                    var made = route.Target switch
                    {
                        FileFormat.Pdf => await _pdfs.CreateAsync(job.SourcePath, job.SourceFormat, staged, choices.Layout, limits, ct).ConfigureAwait(false),
                        FileFormat.Docx => await _docx.CreateAsync(job.SourcePath, job.SourceFormat, staged, choices.Layout, limits, ct).ConfigureAwait(false),
                        _ => Result.Failure<PictureDocumentResult>(new OperationError(ErrorCode.Unsupported, "Pictures can only be put into a PDF or a Word file."))
                    };
                    return made.IsSuccess ? await _committer.CommitAsync(job, workspace, [staged], limits, ct).ConfigureAwait(false) : Carry(made);
                }

            case ConversionMode.Rasterize:
                {
                    var pages = await _rasterizer.RasterizeAsync(
                        job.SourcePath, choices.PdfPassword, route.Target, choices.Pdf, choices.Image, workspace, limits, progress, ct).ConfigureAwait(false);
                    return pages.IsSuccess
                        ? await _committer.CommitAsync(job, workspace, pages.Value.StagedFiles, pages.Value.Numbering, limits, ct).ConfigureAwait(false)
                        : Carry(pages);
                }

            case ConversionMode.ExternalRenderer:
                return Fail(ErrorCode.DependencyMissing,
                    "Converting Word documents needs LibreOffice, and Lunar can't use it yet in this version. Your original file is untouched.");

            default:
                return Fail(ErrorCode.Unsupported, "Converting this kind of file to the chosen format isn't available in this version.");
        }
    }

    // A file changed after planning is not trusted: the plan checked what the file was then, not what it is now.
    private static OperationError? CheckSourceUnchanged(PlannedConversion job)
    {
        try
        {
            var info = new FileInfo(job.SourcePath);
            if (!info.Exists)
                return new OperationError(ErrorCode.InvalidInput, "Lunar can't find this file any more. It may have been moved or deleted.");
            if (info.Length != job.SourceLength || info.LastWriteTimeUtc != job.SourceLastWriteUtc)
                return new OperationError(ErrorCode.InvalidInput, "This file changed after it was added. Add it again to convert the new version.");
            return null;
        }
        catch (Exception ex) when (IoErrors.IsIoProblem(ex))
        {
            return new OperationError(ErrorCode.AccessDenied, "Lunar can't read this file. Check that it isn't open in another program that locks it.", retryable: true);
        }
    }

    // Same-encoding routes copy the bytes: the input is checked as planning checked it, then streamed into the working folder.
    private static async Task<Result> CopyAsync(PlannedConversion job, string stagedPath, ConversionLimits limits, CancellationToken ct)
    {
        if (PathRules.Check(job.SourcePath, limits.MaxPathChars) != PathProblem.None)
            return Result.Failure(new OperationError(ErrorCode.InvalidInput, "The path of this file is not usable."));
        var (_, refusal) = SourceFileValidator.Inspect(job.SourcePath, job.SourceFormat, limits);
        if (refusal is not null)
            return Result.Failure(refusal.ToError());

        await using var source = new FileStream(job.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var target = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        await source.CopyToAsync(target, 81920, ct).ConfigureAwait(false);
        await target.FlushAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    // A failed or cancelled step carries its outcome up unchanged; its own cleanup has already run.
    private static Result<IReadOnlyList<string>> Carry<T>(Result<T> step) =>
        step.IsCancelled ? Result.Cancelled<IReadOnlyList<string>>() : Result.Failure<IReadOnlyList<string>>(step.Error!);

    private static Result<IReadOnlyList<string>> Carry(Result step) =>
        step.IsCancelled ? Result.Cancelled<IReadOnlyList<string>>() : Result.Failure<IReadOnlyList<string>>(step.Error!);

    private static Result<IReadOnlyList<string>> Fail(ErrorCode code, string message, bool retryable = false) =>
        Result.Failure<IReadOnlyList<string>>(new OperationError(code, message, retryable));
}
