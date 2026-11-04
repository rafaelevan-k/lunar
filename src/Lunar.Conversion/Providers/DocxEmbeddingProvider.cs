using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Conversion.Models;
using Lunar.Conversion.Output;
using Lunar.Domain.Conversion;
using Lunar.Domain.Shared;
using SkiaSharp;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Lunar.Conversion.Providers;

/// <summary>
/// Puts a PNG or JPEG picture into a Word document (BE-23, PRD section 5): one page holding the picture as an inline picture, laid out
/// with the page size, margin and fit of <see cref="PageLayoutOptions"/>. Written with the Open XML SDK (ADR-0003).
///
/// What the file is, said plainly: the document contains the picture and nothing else. No text is recognised from it (there is no OCR),
/// so there is no text to edit; the picture's alternative text and the document description say so. A picture that needs no turning is
/// embedded as its own bytes, unchanged; one with an EXIF rotation tag is turned upright and stored as a PNG, so every viewer shows it
/// the same way. Nothing external is referenced (no links, fields, macros or fonts), and nothing about the picture's name or place is written.
///
/// Layout is a request, not a guarantee: Word, LibreOffice and other viewers lay a page out slightly differently, and this task has not
/// been checked in a real renderer (the document-renderer evidence is deferred under ADR-0005). The picture is kept a few points inside
/// the page so the page does not spill onto a second one. The file is created with "create new" and a partly written file is deleted
/// on any failure. The writer cannot be interrupted: a cancel is noticed between the steps.
/// </summary>
public sealed class DocxEmbeddingProvider
{
    /// <summary>The longest page side Word allows (22 inches).</summary>
    public const double MaxPagePoints = 1584;

    // Room kept below the picture so the line that holds it does not outgrow the page.
    private const double SafetyPoints = 4;
    private const string Description = "A picture converted by Lunar. This document contains the picture only; no text was recognised, so there is no text to edit.";

    private readonly IDiagnosticLog? _log;
    private readonly TimeProvider _time;

    public DocxEmbeddingProvider(IDiagnosticLog? log = null, TimeProvider? time = null)
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
        var opened = PictureSource.Open(sourcePath, sourceFormat, FileFormat.Docx, outputPath, layout, limits);
        if (opened.IsFailure)
            return Result.Failure<PictureDocumentResult>(opened.Error!);

        var picture = opened.Value;
        var placement = Place(layout, picture);
        var created = false;
        try
        {
            ct.ThrowIfCancellationRequested();

            // An upright picture is embedded exactly as it is; a turned one is stored upright as a PNG.
            var turned = picture.Origin != SKEncodedOrigin.TopLeft;
            byte[]? upright = turned ? RenderUpright(sourcePath, picture) : null;
            if (turned && upright is null)
                return PictureSource.Fail<PictureDocumentResult>(ErrorCode.InvalidInput, "This picture is damaged or in a form Lunar can't read. Try getting a fresh copy of it.");
            ct.ThrowIfCancellationRequested();

            try
            {
                using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                created = true;
                var now = _time.GetUtcNow().UtcDateTime;
                using (var package = WordprocessingDocument.Create(output, WordprocessingDocumentType.Document, autoSave: true))
                {
                    package.PackageProperties.Creator = "Lunar";
                    package.PackageProperties.Description = Description;
                    package.PackageProperties.Created = now;
                    package.PackageProperties.Modified = now;
                    Write(package, sourcePath, sourceFormat, upright, placement);
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
                placement.PageWidth, placement.PageHeight, picture.ShownWidth, picture.ShownHeight, placement.Cropped, new FileInfo(outputPath).Length));
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
            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Error, "conversion.docx.engine_missing", "Conversion", "Docx", ErrorCode.DependencyMissing, ExceptionType: ex.GetType()));
            return PictureSource.Fail<PictureDocumentResult>(ErrorCode.DependencyMissing, "The picture engine Lunar needs could not be loaded, so Word documents can't be made. Reinstalling Lunar may fix this.");
        }
        catch (Exception ex) when (ex is OutOfMemoryException or InsufficientMemoryException)
        {
            DeletePartial(outputPath, created);
            return Result.Failure<PictureDocumentResult>(new OperationError(ErrorCode.Unexpected, "There wasn't enough memory to make this document. Close other programs or try a smaller picture.", retryable: true));
        }
        catch (Exception ex)
        {
            DeletePartial(outputPath, created);
            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Error, "conversion.docx.fault", "Conversion", "Docx", ErrorCode.Unexpected, ExceptionType: ex.GetType()));
            return PictureSource.Fail<PictureDocumentResult>(ErrorCode.Unexpected, "The Word document could not be made.");
        }
    }

    // ---- placement ---------------------------------------------------------------------------------------------------

    private sealed record Placement(
        double PageWidth, double PageHeight, double MarginLeft, double MarginTop, double MarginRight, double MarginBottom,
        double ExtentWidth, double ExtentHeight, double SpaceBefore, double CropX, double CropY, bool Cropped);

    // The shared page arithmetic, adjusted for how a line of Word holds a picture: the picture sits a few points inside the area so the
    // line does not outgrow the page, and on a fixed page it is centred downwards with space before the paragraph.
    private static Placement Place(PageLayoutOptions layout, PictureInfo picture)
    {
        var g = PageLayout.Compute(layout, picture.ShownWidth, picture.ShownHeight, MaxPagePoints);
        var margin = layout.MarginPoints;
        if (layout.Size == PageSize.Auto)
        {
            return new Placement(g.PageWidth, g.PageHeight + SafetyPoints, margin, margin, margin, margin + SafetyPoints,
                g.DrawWidth, g.DrawHeight, 0, 0, 0, false);
        }

        var availableHeight = g.BoxHeight - SafetyPoints;
        var fit = layout.Fit == ImageFit.Fit;
        var scale = fit
            ? Math.Min(g.BoxWidth / picture.ShownWidth, availableHeight / picture.ShownHeight)
            : Math.Max(g.BoxWidth / picture.ShownWidth, availableHeight / picture.ShownHeight);
        var drawWidth = picture.ShownWidth * scale;
        var drawHeight = picture.ShownHeight * scale;

        if (fit)
        {
            return new Placement(g.PageWidth, g.PageHeight, margin, margin, margin, margin, drawWidth, drawHeight,
                Math.Max(0, (availableHeight - drawHeight) / 2), 0, 0, false);
        }

        // Cover: the picture shown is the area itself, and the overflow is cut off by cropping the picture on each side.
        var cropX = drawWidth > g.BoxWidth ? (drawWidth - g.BoxWidth) / 2 / drawWidth : 0;
        var cropY = drawHeight > availableHeight ? (drawHeight - availableHeight) / 2 / drawHeight : 0;
        return new Placement(g.PageWidth, g.PageHeight, margin, margin, margin, margin, Math.Min(drawWidth, g.BoxWidth), Math.Min(drawHeight, availableHeight),
            0, cropX, cropY, cropX > 0 || cropY > 0);
    }

    // ---- the picture bytes -------------------------------------------------------------------------------------------

    private static byte[]? RenderUpright(string sourcePath, PictureInfo picture)
    {
        using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        using var codec = SKCodec.Create(stream);
        if (codec is null)
            return null;

        using var decoded = PictureRendering.DecodeRaw(codec);
        if (decoded is null)
            return null;

        using var turned = PictureRendering.Render(decoded, picture.Origin, background: null);
        using var image = SKImage.FromBitmap(turned);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray();
    }

    // ---- the document ------------------------------------------------------------------------------------------------

    private static void Write(WordprocessingDocument package, string sourcePath, FileFormat sourceFormat, byte[]? upright, Placement p)
    {
        var main = package.AddMainDocumentPart();
        var imagePart = main.AddImagePart(upright is not null || sourceFormat == FileFormat.Png ? ImagePartType.Png : ImagePartType.Jpeg);
        if (upright is not null)
        {
            using var memory = new MemoryStream(upright);
            imagePart.FeedData(memory);
        }
        else
        {
            using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
            imagePart.FeedData(source);
        }

        var relationshipId = main.GetIdOfPart(imagePart);
        var cx = ToEmu(p.ExtentWidth);
        var cy = ToEmu(p.ExtentHeight);

        var blipFill = new PIC.BlipFill(new A.Blip { Embed = relationshipId });
        if (p.CropX > 0 || p.CropY > 0)
        {
            blipFill.Append(new A.SourceRectangle
            {
                Left = ToThousandthsPercent(p.CropX),
                Right = ToThousandthsPercent(p.CropX),
                Top = ToThousandthsPercent(p.CropY),
                Bottom = ToThousandthsPercent(p.CropY)
            });
        }

        blipFill.Append(new A.Stretch(new A.FillRectangle()));

        var inline = new DW.Inline(
            new DW.Extent { Cx = cx, Cy = cy },
            new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
            new DW.DocProperties { Id = 1U, Name = "Picture 1", Description = Description },
            new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
            new A.Graphic(new A.GraphicData(new PIC.Picture(
                new PIC.NonVisualPictureProperties(new PIC.NonVisualDrawingProperties { Id = 0U, Name = "Picture" }, new PIC.NonVisualPictureDrawingProperties()),
                blipFill,
                new PIC.ShapeProperties(
                    new A.Transform2D(new A.Offset { X = 0L, Y = 0L }, new A.Extents { Cx = cx, Cy = cy }),
                    new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
        {
            DistanceFromTop = 0U,
            DistanceFromBottom = 0U,
            DistanceFromLeft = 0U,
            DistanceFromRight = 0U
        };

        // The paragraph and its mark use a one-point font so the line is exactly as tall as the picture.
        var paragraph = new W.Paragraph(
            new W.ParagraphProperties(
                new W.SpacingBetweenLines { Before = ToTwips(p.SpaceBefore).ToString(System.Globalization.CultureInfo.InvariantCulture), After = "0", Line = "240", LineRule = W.LineSpacingRuleValues.Auto },
                new W.Justification { Val = W.JustificationValues.Center },
                new W.ParagraphMarkRunProperties(new W.FontSize { Val = "2" })),
            new W.Run(new W.RunProperties(new W.FontSize { Val = "2" }), new W.Drawing(inline)));

        var landscape = p.PageWidth > p.PageHeight;
        var pageSize = new W.PageSize { Width = (uint)ToTwips(p.PageWidth), Height = (uint)ToTwips(p.PageHeight) };
        if (landscape)
            pageSize.Orient = W.PageOrientationValues.Landscape;

        main.Document = new W.Document(new W.Body(
            paragraph,
            new W.SectionProperties(
                pageSize,
                new W.PageMargin
                {
                    Top = ToTwips(p.MarginTop),
                    Bottom = ToTwips(p.MarginBottom),
                    Left = (uint)ToTwips(p.MarginLeft),
                    Right = (uint)ToTwips(p.MarginRight),
                    Header = 0U,
                    Footer = 0U,
                    Gutter = 0U
                })));
        main.Document.Save();
    }

    private static long ToEmu(double points) => (long)Math.Max(1, Math.Round(points * 12700));

    private static int ToTwips(double points) => (int)Math.Round(points * 20);

    private static int ToThousandthsPercent(double fraction) => (int)Math.Round(Math.Clamp(fraction, 0, 0.99) * 100_000);

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
            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "conversion.docx.cleanup_failed", "Conversion", "Docx", ErrorCode.IoFailure, ExceptionType: ex.GetType()));
        }
    }
}
