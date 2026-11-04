using Lunar.Application.Abstractions.Conversion;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Conversion.Models;
using Lunar.Conversion.Output;
using Lunar.Conversion.Pipeline;
using Lunar.Conversion.Validation;
using Lunar.Domain.Conversion;
using Lunar.Domain.Shared;
using SkiaSharp;

namespace Lunar.Conversion.Providers;

/// <summary>One drawn page of a PDF, waiting in the job's working folder.</summary>
/// <param name="StagedPath">The picture file, inside the job's working folder.</param>
/// <param name="PageNumber">The page's own number in the PDF (counting from 1), whatever pages were chosen.</param>
/// <param name="WidthPixels">Width of the picture.</param>
/// <param name="HeightPixels">Height of the picture.</param>
public sealed record RasterizedPage(string StagedPath, int PageNumber, int WidthPixels, int HeightPixels);

/// <summary>What drawing the chosen pages of a PDF produced.</summary>
/// <param name="DocumentPageCount">Pages in the whole PDF (not only the chosen ones); it sets how wide the page numbers in the names are.</param>
/// <param name="Pages">The chosen pages in ascending order.</param>
public sealed record PdfRasterizationResult(int DocumentPageCount, IReadOnlyList<RasterizedPage> Pages)
{
    /// <summary>The picture files in page order, ready for the output step.</summary>
    public IReadOnlyList<string> StagedFiles => [.. Pages.Select(p => p.StagedPath)];

    /// <summary>The numbering the output step needs so the names keep the PDF's own page numbers.</summary>
    public PageNumbering Numbering => new([.. Pages.Select(p => p.PageNumber)], DocumentPageCount);
}

/// <summary>
/// Turns the chosen pages of a PDF into pictures, one file per page (BE-24, PRD section 5). The drawing is done by the operating system's
/// renderer behind <see cref="IPdfPageRasterizer"/>; this class decides what is asked of it and keeps every limit:
/// - The PDF is checked as planning checked it, then opened (a password stays in memory for the call and is passed on, never kept or logged).
/// - The chosen pages are resolved against the real page count; a page that does not exist, or more pages than the limit, is refused with the
///   numbers in the message. The limit applies to the pages converted, so a few pages of a huge PDF are fine.
/// - Every chosen page's size at the chosen resolution is worked out and checked against the pixel limit BEFORE any page is drawn; a page
///   that would be too large is refused by number with advice to pick a lower resolution. The resolution is never lowered silently.
/// - Pages are drawn one after another into the job's working folder. PNG results get the chosen resolution written into the file (the
///   operating system stamps the screen's); JPEG results are made on white paper at the chosen quality. Progress is reported per finished page
///   because the page count is known.
/// - Any failure or cancel removes every picture made so far. The PDF is only ever read.
/// Memory is one page picture at a time (about 4 bytes per pixel, more while a JPEG is made).
/// </summary>
public sealed class PdfRasterizationProvider
{
    private readonly IPdfPageRasterizer _rasterizer;
    private readonly IDiagnosticLog? _log;

    public PdfRasterizationProvider(IPdfPageRasterizer rasterizer, IDiagnosticLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(rasterizer);
        _rasterizer = rasterizer;
        _log = log;
    }

    public async Task<Result<PdfRasterizationResult>> RasterizeAsync(
        string sourcePath,
        string? password,
        FileFormat target,
        PdfRasterizationOptions options,
        ImageEncodingOptions imageOptions,
        TempWorkspace workspace,
        ConversionLimits limits,
        IProgress<JobProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(imageOptions);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(limits);
        if (ct.IsCancellationRequested)
            return Result.Cancelled<PdfRasterizationResult>();

        if (!Enum.IsDefined(target) || ConversionPolicy.Resolve(FileFormat.Pdf, target).Mode != ConversionMode.Rasterize)
            return Fail(ErrorCode.Unsupported, "This provider only turns PDF pages into PNG or JPEG pictures.");
        try
        {
            options.Validated();
            imageOptions.Validated();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return Fail(ErrorCode.InvalidInput, ex.ParamName == nameof(PdfRasterizationOptions.Dpi)
                ? "The resolution must be 72, 150 or 300 pixels per inch."
                : "The JPEG quality must be between 1 and 100.");
        }

        if (PathRules.Check(sourcePath, limits.MaxPathChars) != PathProblem.None)
            return Fail(ErrorCode.InvalidInput, "The path of this file is not usable.");
        var (_, refusal) = SourceFileValidator.Inspect(sourcePath, FileFormat.Pdf, limits);
        if (refusal is not null)
            return Result.Failure<PdfRasterizationResult>(refusal.ToError());

        var made = new List<string>();
        Result<PdfRasterizationResult>? outcome = null;
        try
        {
            await using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var opened = await _rasterizer.OpenAsync(stream, password, ct).ConfigureAwait(false);
            if (opened.IsFailure)
                return Result.Failure<PdfRasterizationResult>(opened.Error!);

            await using var document = opened.Value;
            outcome = await RasterizeDocumentAsync(document, target, options, imageOptions, workspace, limits, progress, made, ct).ConfigureAwait(false);
            return outcome;
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled<PdfRasterizationResult>();
        }
        catch (Exception ex) when (IoErrors.IsIoProblem(ex))
        {
            return ex is FileNotFoundException or DirectoryNotFoundException && made.Count == 0
                ? Fail(ErrorCode.InvalidInput, "Lunar can't read this PDF any more. It may have been moved or deleted.")
                : Result.Failure<PdfRasterizationResult>(IoErrors.Map(ex));
        }
        catch (Exception ex) when (ex is OutOfMemoryException or InsufficientMemoryException)
        {
            return Result.Failure<PdfRasterizationResult>(new OperationError(ErrorCode.Unexpected, "There wasn't enough memory to draw these pages. Close other programs or choose a lower resolution.", retryable: true));
        }
        catch (Exception ex)
        {
            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Error, "conversion.pdf.raster_fault", "Conversion", "Rasterize", ErrorCode.Unexpected, ExceptionType: ex.GetType()));
            return Fail(ErrorCode.Unexpected, "The PDF could not be converted.");
        }
        finally
        {
            // Pictures made so far are only wanted when the whole job went through; the caller takes them from the result.
            if (made.Count > 0 && outcome is not { IsSuccess: true })
                DeleteAll(made);
        }
    }

    private async Task<Result<PdfRasterizationResult>> RasterizeDocumentAsync(
        IPdfDocument document,
        FileFormat target,
        PdfRasterizationOptions options,
        ImageEncodingOptions imageOptions,
        TempWorkspace workspace,
        ConversionLimits limits,
        IProgress<JobProgress>? progress,
        List<string> made,
        CancellationToken ct)
    {
        var pageCount = document.PageCount;
        if (pageCount < 1 || pageCount > PageSelection.MaxPageNumber)
            return Fail(ErrorCode.InvalidInput, "This PDF has no pages Lunar can convert.");

        var chosen = options.Pages.Resolve(pageCount);
        if (chosen.IsFailure)
            return Result.Failure<PdfRasterizationResult>(chosen.Error!);
        var numbers = chosen.Value;
        if (numbers.Count > limits.MaxPages)
        {
            return Fail(ErrorCode.InvalidInput,
                $"That is {numbers.Count} pages, and Lunar converts at most {limits.MaxPages} at a time. Choose fewer pages, for example the first {limits.MaxPages}.");
        }

        // Work out every picture's size first, so a page that is too large stops the job before the first page is drawn.
        var sizes = new List<(int Width, int Height)>(numbers.Count);
        foreach (var number in numbers)
        {
            var size = document.GetPageSize(number - 1);
            if (size.IsFailure)
                return Result.Failure<PdfRasterizationResult>(size.Error!);

            var width = (long)Math.Max(1, Math.Round(size.Value.WidthPoints * options.Dpi / 72.0, MidpointRounding.AwayFromZero));
            var height = (long)Math.Max(1, Math.Round(size.Value.HeightPoints * options.Dpi / 72.0, MidpointRounding.AwayFromZero));
            if (width * height > limits.MaxImagePixels || width > WindowsSideLimit || height > WindowsSideLimit)
            {
                return Fail(ErrorCode.InvalidInput,
                    $"Page {number} would be {width} by {height} pixels at {options.Dpi} pixels per inch, which is more than Lunar converts. Choose a lower resolution.");
            }

            sizes.Add(((int)width, (int)height));
        }

        progress?.Report(new JobProgress(0, numbers.Count));
        var pages = new List<RasterizedPage>(numbers.Count);
        for (var i = 0; i < numbers.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var page = await DrawPageAsync(document, numbers[i], sizes[i], target, options, imageOptions, workspace, made, ct).ConfigureAwait(false);
            if (page.IsFailure)
                return Result.Failure<PdfRasterizationResult>(page.Error!);

            pages.Add(page.Value);
            progress?.Report(new JobProgress(i + 1, numbers.Count));
        }

        return Result.Success(new PdfRasterizationResult(pageCount, pages));
    }

    private const int WindowsSideLimit = 32_767;

    private async Task<Result<RasterizedPage>> DrawPageAsync(
        IPdfDocument document,
        int pageNumber,
        (int Width, int Height) size,
        FileFormat target,
        PdfRasterizationOptions options,
        ImageEncodingOptions imageOptions,
        TempWorkspace workspace,
        List<string> made,
        CancellationToken ct)
    {
        var pngPath = workspace.NewFilePath(".png");
        made.Add(pngPath);
        Result<RenderedPage> drawn;
        await using (var file = new FileStream(pngPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous))
        {
            drawn = await document.RenderPageAsync(pageNumber - 1, size.Width, size.Height, file, ct).ConfigureAwait(false);
            if (drawn.IsSuccess)
                await file.FlushAsync(ct).ConfigureAwait(false);
        }

        if (drawn.IsCancelled)
            return Result.Cancelled<RasterizedPage>();
        if (drawn.IsFailure)
            return Result.Failure<RasterizedPage>(drawn.Error!);

        var (width, height) = (drawn.Value.WidthPixels, drawn.Value.HeightPixels);
        var bytes = await File.ReadAllBytesAsync(pngPath, ct).ConfigureAwait(false);
        if (!MatchesHeader(bytes, width, height))
            return Fail<RasterizedPage>(ErrorCode.Unexpected, $"Page {pageNumber} did not come out as a picture of the size reported.");

        if (target == FileFormat.Png)
        {
            byte[] withDensity;
            try
            {
                withDensity = PngDensity.WithDensity(bytes, options.Dpi);
            }
            catch (InvalidDataException)
            {
                return Fail<RasterizedPage>(ErrorCode.Unexpected, $"Page {pageNumber} did not come out as a picture.");
            }

            await File.WriteAllBytesAsync(pngPath, withDensity, ct).ConfigureAwait(false);
            return Result.Success(new RasterizedPage(pngPath, pageNumber, width, height));
        }

        // JPEG: the page is on white paper already; any alpha channel left is painted over white anyway.
        var jpegPath = workspace.NewFilePath(".jpg");
        made.Add(jpegPath);
        ct.ThrowIfCancellationRequested();
        using (var decoded = SKBitmap.Decode(bytes))
        {
            if (decoded is null || decoded.Width != width || decoded.Height != height)
                return Fail<RasterizedPage>(ErrorCode.Unexpected, $"Page {pageNumber} did not come out as a picture.");

            using var flat = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
            using (var canvas = new SKCanvas(flat))
            {
                canvas.Clear(SKColors.White);
                canvas.DrawBitmap(decoded, 0, 0, SKSamplingOptions.Default);
            }

            using var image = SKImage.FromBitmap(flat);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, imageOptions.JpegQuality);
            if (data is null)
                return Fail<RasterizedPage>(ErrorCode.Unexpected, $"Page {pageNumber} could not be encoded.");
            await using var output = new FileStream(jpegPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            data.SaveTo(output);
            await output.FlushAsync(ct).ConfigureAwait(false);
        }

        File.Delete(pngPath);
        made.Remove(pngPath);
        return Result.Success(new RasterizedPage(jpegPath, pageNumber, width, height));
    }

    private static bool MatchesHeader(byte[] png, int width, int height)
    {
        if (png.Length < 33 || png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47)
            return false;
        var w = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16));
        var h = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20));
        return w == width && h == height;
    }

    private void DeleteAll(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "conversion.pdf.cleanup_failed", "Conversion", "Rasterize", ErrorCode.IoFailure, ExceptionType: ex.GetType()));
            }
        }
    }

    private static Result<PdfRasterizationResult> Fail(ErrorCode code, string message) => Fail<PdfRasterizationResult>(code, message);

    private static Result<T> Fail<T>(ErrorCode code, string message) => Result.Failure<T>(new OperationError(code, message));
}
