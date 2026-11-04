using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Conversion.Models;
using Lunar.Conversion.Output;
using Lunar.Domain.Conversion;
using Lunar.Domain.Shared;
using SkiaSharp;

namespace Lunar.Conversion.Providers;

/// <summary>
/// Puts a PNG or JPEG picture on the page of a PDF (BE-23, PRD section 5): "image to PDF" is one page holding the picture, with the page
/// size, margin and fit chosen in <see cref="PageLayoutOptions"/>. Written with the PDF writer of SkiaSharp (ADR-0003).
///
/// What the file is, said plainly: the picture is embedded as a picture. The PDF has no text, nothing is recognised from the picture,
/// and nothing in it can be selected or searched; the document description says so. A JPEG is passed through as its own data (no
/// second compression and no resampling); a PNG is stored losslessly, with its transparency kept. A picture with an EXIF rotation
/// tag is shown upright by turning it on the page, not by changing its pixels. Nothing about the picture's name, place or origin is
/// written; the creation time and "Lunar" as the creating program are.
///
/// The file is created with "create new", so it never replaces anything, and a partly written file is deleted on any failure. The
/// writer cannot be interrupted: a cancel is noticed between the steps. Limits: a page side of at most 200 inches.
/// </summary>
public sealed class PdfCreationProvider
{
    /// <summary>The longest page side a PDF allows (200 inches).</summary>
    public const double MaxPagePoints = 14400;

    private readonly IDiagnosticLog? _log;
    private readonly TimeProvider _time;

    public PdfCreationProvider(IDiagnosticLog? log = null, TimeProvider? time = null)
    {
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    public Task<Result<PictureDocumentResult>> CreateAsync(
        string sourcePath,
        FileFormat sourceFormat,
        string outputPath,
        PageLayoutOptions layout,
        ConversionLimits limits,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(outputPath);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(limits);
        if (ct.IsCancellationRequested)
            return Task.FromResult(Result.Cancelled<PictureDocumentResult>());

        return Task.Run(() => Create(sourcePath, sourceFormat, outputPath, layout, limits, ct), CancellationToken.None);
    }

    private Result<PictureDocumentResult> Create(string sourcePath, FileFormat sourceFormat, string outputPath, PageLayoutOptions layout, ConversionLimits limits, CancellationToken ct)
    {
        var opened = PictureSource.Open(sourcePath, sourceFormat, FileFormat.Pdf, outputPath, layout, limits);
        if (opened.IsFailure)
            return Result.Failure<PictureDocumentResult>(opened.Error!);

        var picture = opened.Value;
        var geometry = PageLayout.Compute(layout, picture.ShownWidth, picture.ShownHeight, MaxPagePoints);
        var created = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            // An upright picture goes in as its own data (a JPEG without a second compression). A picture with a rotation tag is decoded exactly
            // as stored and turned on the page by the matrix below, so the tag is never applied twice by a library that also honours it.
            using var image = LoadImage(sourcePath, picture);
            if (image is null)
                return PictureSource.Fail<PictureDocumentResult>(ErrorCode.InvalidInput, "This picture is damaged or in a form Lunar can't read. Try getting a fresh copy of it.");
            ct.ThrowIfCancellationRequested();

            var now = _time.GetUtcNow().UtcDateTime;
            var metadata = new SKDocumentPdfMetadata
            {
                Creator = "Lunar",
                Subject = "A picture converted by Lunar. The page holds the picture only; no text was recognised, so there is nothing to select or search.",
                Creation = now,
                Modified = now
            };

            try
            {
                using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                created = true;
                using (var document = SKDocument.CreatePdf(output, metadata))
                {
                    var canvas = document.BeginPage((float)geometry.PageWidth, (float)geometry.PageHeight);
                    canvas.ClipRect(new SKRect((float)geometry.BoxX, (float)geometry.BoxY, (float)(geometry.BoxX + geometry.BoxWidth), (float)(geometry.BoxY + geometry.BoxHeight)));

                    // Put the picture's centre in the centre of its place on the page, scaled from pixels to points, turned upright.
                    canvas.Translate((float)(geometry.DrawX + (geometry.DrawWidth / 2)), (float)(geometry.DrawY + (geometry.DrawHeight / 2)));
                    var scale = (float)(geometry.DrawWidth / picture.ShownWidth);
                    canvas.Scale(scale, scale);
                    canvas.Translate(-picture.ShownWidth / 2f, -picture.ShownHeight / 2f);
                    var orientation = PictureRendering.OrientationMatrix(picture.Origin, picture.Width, picture.Height);
                    canvas.Concat(in orientation);
                    canvas.DrawImage(image, 0, 0, SKSamplingOptions.Default);

                    document.EndPage();
                    document.Close();
                }

                output.Flush(flushToDisk: true);
            }
            catch (Exception ex) when (IoErrors.IsIoProblem(ex))
            {
                DeletePartial(outputPath, created);
                return Result.Failure<PictureDocumentResult>(IoErrors.Map(ex));
            }

            ct.ThrowIfCancellationRequested();
            return Result.Success(new PictureDocumentResult(
                geometry.PageWidth, geometry.PageHeight, picture.ShownWidth, picture.ShownHeight, geometry.IsCropped, new FileInfo(outputPath).Length));
        }
        catch (OperationCanceledException)
        {
            DeletePartial(outputPath, created);
            return Result.Cancelled<PictureDocumentResult>();
        }
        catch (Exception ex) when (IoErrors.IsIoProblem(ex))
        {
            DeletePartial(outputPath, created);
            return Result.Failure<PictureDocumentResult>(IoErrors.Map(ex));
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or BadImageFormatException)
        {
            DeletePartial(outputPath, created);
            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Error, "conversion.pdf.engine_missing", "Conversion", "Pdf", ErrorCode.DependencyMissing, ExceptionType: ex.GetType()));
            return PictureSource.Fail<PictureDocumentResult>(ErrorCode.DependencyMissing, "The picture engine Lunar needs could not be loaded, so PDFs can't be made. Reinstalling Lunar may fix this.");
        }
        catch (Exception ex) when (ex is OutOfMemoryException or InsufficientMemoryException)
        {
            DeletePartial(outputPath, created);
            return Result.Failure<PictureDocumentResult>(new OperationError(ErrorCode.Unexpected, "There wasn't enough memory to make this PDF. Close other programs or try a smaller picture.", retryable: true));
        }
        catch (Exception ex)
        {
            DeletePartial(outputPath, created);
            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Error, "conversion.pdf.fault", "Conversion", "Pdf", ErrorCode.Unexpected, ExceptionType: ex.GetType()));
            return PictureSource.Fail<PictureDocumentResult>(ErrorCode.Unexpected, "The PDF could not be made.");
        }
    }

    private static SKImage? LoadImage(string sourcePath, PictureInfo picture)
    {
        if (picture.Origin == SKEncodedOrigin.TopLeft)
        {
            using var data = SKData.Create(sourcePath);
            return data is null ? null : SKImage.FromEncodedData(data);
        }

        using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        using var codec = SKCodec.Create(stream);
        if (codec is null)
            return null;
        using var decoded = PictureRendering.DecodeRaw(codec);
        return decoded is null ? null : SKImage.FromBitmap(decoded);
    }

    private void DeletePartial(string outputPath, bool created)
    {
        if (!created)
            return;
        try
        {
            File.Delete(outputPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "conversion.pdf.cleanup_failed", "Conversion", "Pdf", ErrorCode.IoFailure, ExceptionType: ex.GetType()));
        }
    }
}
