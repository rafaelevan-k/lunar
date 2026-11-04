using Lunar.Application.Conversion.Models;
using Lunar.Conversion.Output;
using Lunar.Conversion.Validation;
using Lunar.Domain.Conversion;
using Lunar.Domain.Shared;
using SkiaSharp;

namespace Lunar.Conversion.Providers;

/// <summary>What the providers that put a picture into a document need to know about it.</summary>
/// <param name="Width">Stored width in pixels, before any rotation.</param>
/// <param name="Height">Stored height in pixels, before any rotation.</param>
/// <param name="Origin">The EXIF orientation of the picture (top left when there is none).</param>
internal sealed record PictureInfo(int Width, int Height, SKEncodedOrigin Origin)
{
    /// <summary>Width as the picture is seen once turned upright.</summary>
    public int ShownWidth => PictureRendering.SwapsSides(Origin) ? Height : Width;

    /// <summary>Height as the picture is seen once turned upright.</summary>
    public int ShownHeight => PictureRendering.SwapsSides(Origin) ? Width : Height;
}

/// <summary>What a picture put into a PDF or a Word document produced, for the notice the user sees.</summary>
/// <param name="PageWidthPoints">Width of the page in points (1/72 inch).</param>
/// <param name="PageHeightPoints">Height of the page in points.</param>
/// <param name="PictureWidth">Width of the picture in pixels as it is seen (after any rotation).</param>
/// <param name="PictureHeight">Height of the picture in pixels as it is seen (after any rotation).</param>
/// <param name="PictureCropped">True when part of the picture is cut off by the page (cover fit).</param>
/// <param name="OutputBytes">Size of the written file.</param>
public sealed record PictureDocumentResult(double PageWidthPoints, double PageHeightPoints, int PictureWidth, int PictureHeight, bool PictureCropped, long OutputBytes);

/// <summary>
/// The checks and reading every "picture into a document" provider starts with: the route is one of the matrix's embed routes, the
/// paths are usable, the file is looked at again exactly as planning looked at it (it may have changed since), and the picture's size and
/// orientation are read from its header, with the pixel limit applied before anything is decoded.
/// </summary>
internal static class PictureSource
{
    public static Result<PictureInfo> Open(
        string sourcePath, FileFormat sourceFormat, FileFormat targetFormat, string outputPath, PageLayoutOptions layout, ConversionLimits limits)
    {
        var expectedTarget = targetFormat;
        if (!Enum.IsDefined(sourceFormat) || !Enum.IsDefined(targetFormat)
            || ConversionPolicy.Resolve(sourceFormat, targetFormat).Mode != ConversionMode.Embed || expectedTarget is not (FileFormat.Pdf or FileFormat.Docx))
        {
            return Fail(ErrorCode.Unsupported, "This provider only puts a PNG or JPEG picture into a PDF or a Word document.");
        }

        try
        {
            layout.Validated();
        }
        catch (ArgumentOutOfRangeException)
        {
            return Fail(ErrorCode.InvalidInput, "The page choices are not valid. The margin must be between 0 and 144 points.");
        }

        if (PathRules.Check(sourcePath, limits.MaxPathChars) != PathProblem.None || PathRules.Check(outputPath, limits.MaxPathChars) != PathProblem.None)
            return Fail(ErrorCode.InvalidInput, "A path for this conversion is not usable.");

        var (_, refusal) = SourceFileValidator.Inspect(sourcePath, sourceFormat, limits);
        if (refusal is not null)
            return Result.Failure<PictureInfo>(refusal.ToError());

        try
        {
            using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            using var codec = SKCodec.Create(stream);
            if (codec is null)
                return Fail(ErrorCode.InvalidInput, "This picture is damaged or in a form Lunar can't read. Try getting a fresh copy of it.");

            var info = codec.Info;
            if (info.Width < 1 || info.Height < 1 || (long)info.Width * info.Height > limits.MaxImagePixels)
                return Fail(ErrorCode.InvalidInput, $"This picture is larger than {limits.MaxImagePixels / 1_000_000} megapixels, which is more than Lunar converts.");

            return Result.Success(new PictureInfo(info.Width, info.Height, codec.EncodedOrigin));
        }
        catch (Exception ex) when (IoErrors.IsIoProblem(ex))
        {
            return ex is FileNotFoundException or DirectoryNotFoundException
                ? Fail(ErrorCode.InvalidInput, "Lunar can't read this picture any more. It may have been moved or deleted.")
                : Fail(ErrorCode.InvalidInput, "Lunar can't read this picture. Close any program that is using it, check that you may open it, and try again.");
        }
    }

    public static Result<T> Fail<T>(ErrorCode code, string message) => Result.Failure<T>(new OperationError(code, message));

    private static Result<PictureInfo> Fail(ErrorCode code, string message) => Fail<PictureInfo>(code, message);
}
