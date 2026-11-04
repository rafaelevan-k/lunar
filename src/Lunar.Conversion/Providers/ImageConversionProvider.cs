using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Conversion.Models;
using Lunar.Conversion.Output;
using Lunar.Conversion.Validation;
using Lunar.Domain.Conversion;
using Lunar.Domain.Shared;
using SkiaSharp;

namespace Lunar.Conversion.Providers;

/// <summary>What a picture conversion produced, for the notice the user sees.</summary>
/// <param name="Width">Width of the result in pixels (after any rotation).</param>
/// <param name="Height">Height of the result in pixels (after any rotation).</param>
/// <param name="SourceHadTransparency">True when the source can hold transparency; to a JPEG it was painted onto the background colour.</param>
/// <param name="OrientationApplied">True when the source carried a rotation or mirroring tag and the pixels were turned to match, so the result needs no tag.</param>
/// <param name="OutputBytes">Size of the written file.</param>
public sealed record ImageConversionResult(int Width, int Height, bool SourceHadTransparency, bool OrientationApplied, long OutputBytes);

/// <summary>
/// The picture-to-picture routes of the matrix (BE-22, PRD section 5): PNG to JPG/JPEG, and JPG/JPEG to PNG, with SkiaSharp
/// (ADR-0003). Copies between names of one encoding (PNG to PNG, JPG to JPEG) are not conversions and are not done here.
///
/// What it does, in order: re-checks the input exactly as planning did (a file can change between planning and now); reads the
/// picture size from the header and refuses a picture over the pixel limit before any pixel is decoded; decodes to 8-bit sRGB (a picture
/// with another colour profile is converted to sRGB, and the result is tagged sRGB, not the original profile); turns the pixels by the
/// EXIF orientation of the source so the result needs no orientation tag; for a JPEG target paints transparent and half-transparent
/// pixels over the chosen background colour (white by default) in one pass together with the rotation; encodes (JPEG at the chosen quality,
/// PNG lossless) and writes the file with "create new", so it never replaces anything.
///
/// Honest limits: a result is 8 bits per channel (a 16-bit PNG loses its extra precision); JPEG to PNG keeps the decoded pixels but not
/// the compression artefacts' history, so it is "pixel preserving", not a copy of the original; no metadata (EXIF, text chunks, profile) is carried over
/// except what is needed to show the picture the same way; an animated or multi-frame source gives its first frame. Peak memory is about
/// eight bytes per pixel (two bitmaps) when a rotation or a transparency flattening is needed, four otherwise. The decoder cannot be
/// interrupted: a cancel is noticed between the steps, not inside one. A partly written output is deleted on any failure.
/// </summary>
public sealed class ImageConversionProvider
{
    private readonly IDiagnosticLog? _log;

    public ImageConversionProvider(IDiagnosticLog? log = null) => _log = log;

    /// <summary>
    /// Converts <paramref name="sourcePath"/> to <paramref name="outputPath"/> (a new file, normally inside the job's working folder).
    /// Expected problems come back as typed failures with a message and no path; cancellation as <see cref="Result.Cancelled{T}"/>.
    /// </summary>
    public Task<Result<ImageConversionResult>> ConvertAsync(
        string sourcePath,
        FileFormat sourceFormat,
        string outputPath,
        FileFormat targetFormat,
        ImageEncodingOptions options,
        ConversionLimits limits,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(outputPath);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(limits);
        if (ct.IsCancellationRequested)
            return Task.FromResult(Result.Cancelled<ImageConversionResult>());

        // Decoding and encoding are CPU work on the pool, never on the caller's thread.
        return Task.Run(() => Convert(sourcePath, sourceFormat, outputPath, targetFormat, options, limits, ct), CancellationToken.None);
    }

    private Result<ImageConversionResult> Convert(
        string sourcePath, FileFormat sourceFormat, string outputPath, FileFormat targetFormat, ImageEncodingOptions options, ConversionLimits limits, CancellationToken ct)
    {
        if (!Enum.IsDefined(sourceFormat) || !Enum.IsDefined(targetFormat) || ConversionPolicy.Resolve(sourceFormat, targetFormat).Mode != ConversionMode.Native)
            return Fail(ErrorCode.Unsupported, "This provider only converts between PNG and JPEG pictures.");

        try
        {
            options.Validated();
        }
        catch (ArgumentOutOfRangeException)
        {
            return Fail(ErrorCode.InvalidInput, "The JPEG quality must be between 1 and 100.");
        }

        if (PathRules.Check(sourcePath, limits.MaxPathChars) != PathProblem.None || PathRules.Check(outputPath, limits.MaxPathChars) != PathProblem.None)
            return Fail(ErrorCode.InvalidInput, "A path for this conversion is not usable.");

        // The file may have changed since planning: look at it again, the same way.
        var (_, refusal) = SourceFileValidator.Inspect(sourcePath, sourceFormat, limits);
        if (refusal is not null)
            return Result.Failure<ImageConversionResult>(refusal.ToError());

        var created = false;
        try
        {
            using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            using var codec = SKCodec.Create(stream);
            if (codec is null)
                return Fail(ErrorCode.InvalidInput, "This picture is damaged or in a form Lunar can't read. Try getting a fresh copy of it.");

            var info = codec.Info;
            if (info.Width < 1 || info.Height < 1 || (long)info.Width * info.Height > limits.MaxImagePixels)
                return Fail(ErrorCode.InvalidInput, $"This picture is larger than {limits.MaxImagePixels / 1_000_000} megapixels, which is more than Lunar converts.");

            var hasAlpha = info.AlphaType != SKAlphaType.Opaque;
            var toJpeg = targetFormat is FileFormat.Jpg or FileFormat.Jpeg;
            var origin = codec.EncodedOrigin;
            ct.ThrowIfCancellationRequested();

            // Decode to 8-bit sRGB; anything with transparency is decoded premultiplied, which is what blending needs.
            var decodeInfo = new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, hasAlpha ? SKAlphaType.Premul : SKAlphaType.Opaque, SKColorSpace.CreateSrgb());
            using var decoded = new SKBitmap(decodeInfo);
            var status = codec.GetPixels(decodeInfo, decoded.GetPixels());
            if (status != SKCodecResult.Success)
            {
                return Fail(ErrorCode.InvalidInput, status == SKCodecResult.IncompleteInput
                    ? "This picture is cut short. Try getting a fresh copy of it."
                    : "This picture is damaged or in a form Lunar can't read. Try getting a fresh copy of it.");
            }

            ct.ThrowIfCancellationRequested();

            var rotate = origin != SKEncodedOrigin.TopLeft;
            var flatten = toJpeg && hasAlpha;
            using var rendered = rotate || flatten ? Render(decoded, origin, flatten ? options.Background : null) : null;
            var final = rendered ?? decoded;
            ct.ThrowIfCancellationRequested();

            using var image = SKImage.FromBitmap(final);
            using var data = toJpeg ? image.Encode(SKEncodedImageFormat.Jpeg, options.JpegQuality) : image.Encode(SKEncodedImageFormat.Png, 100);
            if (data is null)
                return Fail(ErrorCode.Unexpected, "The picture could not be encoded.");
            ct.ThrowIfCancellationRequested();

            try
            {
                using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                created = true;
                data.SaveTo(output);
                output.Flush(flushToDisk: true);
            }
            catch (Exception ex) when (IoErrors.IsIoProblem(ex))
            {
                // Everything that goes wrong here is on the output side: a full disk, a folder that is gone, a name already taken.
                DeletePartial(outputPath, created);
                return Result.Failure<ImageConversionResult>(IoErrors.Map(ex));
            }

            return Result.Success(new ImageConversionResult(final.Width, final.Height, hasAlpha, rotate, data.Size));
        }
        catch (OperationCanceledException)
        {
            DeletePartial(outputPath, created);
            return Result.Cancelled<ImageConversionResult>();
        }
        catch (Exception ex) when (IoErrors.IsIoProblem(ex))
        {
            // The only file reads are the input's, so this is the input side.
            DeletePartial(outputPath, created);
            return ex is FileNotFoundException or DirectoryNotFoundException
                ? Fail(ErrorCode.InvalidInput, "Lunar can't read this picture any more. It may have been moved or deleted.")
                : Fail(ErrorCode.InvalidInput, "Lunar can't read this picture. Close any program that is using it, check that you may open it, and try again.");
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or BadImageFormatException)
        {
            DeletePartial(outputPath, created);
            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Error, "conversion.image.engine_missing", "Conversion", "Image", ErrorCode.DependencyMissing, ExceptionType: ex.GetType()));
            return Fail(ErrorCode.DependencyMissing, "The picture engine Lunar needs could not be loaded, so pictures can't be converted. Reinstalling Lunar may fix this.");
        }
        catch (Exception ex) when (ex is OutOfMemoryException or InsufficientMemoryException)
        {
            DeletePartial(outputPath, created);
            return Result.Failure<ImageConversionResult>(new OperationError(ErrorCode.Unexpected, "There wasn't enough memory to convert this picture. Close other programs or try a smaller picture.", retryable: true));
        }
        catch (Exception ex)
        {
            DeletePartial(outputPath, created);
            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Error, "conversion.image.fault", "Conversion", "Image", ErrorCode.Unexpected, ExceptionType: ex.GetType()));
            return Fail(ErrorCode.Unexpected, "The picture could not be converted.");
        }
    }

    // Turns the pixels by the EXIF orientation and, for a JPEG target, paints them over the background colour: one pass, one extra bitmap.
    private static SKBitmap Render(SKBitmap source, SKEncodedOrigin origin, RgbColor? background)
    {
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var width = swap ? source.Height : source.Width;
        var height = swap ? source.Width : source.Height;
        var alpha = background is null ? source.AlphaType : SKAlphaType.Opaque;
        var target = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, alpha, source.ColorSpace));
        try
        {
            using var canvas = new SKCanvas(target);
            canvas.Clear(background is { } color ? new SKColor(color.R, color.G, color.B) : SKColors.Transparent);
            canvas.SetMatrix(MatrixFor(origin, source.Width, source.Height));
            canvas.DrawBitmap(source, 0, 0, SKSamplingOptions.Default);
            return target;
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    // Maps a source point (x, y) to where it lands once the picture is shown the right way up (EXIF orientation values 1 to 8).
    private static SKMatrix MatrixFor(SKEncodedOrigin origin, int w, int h) => origin switch
    {
        SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
        SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
        SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
        SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
        _ => SKMatrix.Identity
    };

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
            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "conversion.image.cleanup_failed", "Conversion", "Image", ErrorCode.IoFailure, ExceptionType: ex.GetType()));
        }
    }

    private static Result<ImageConversionResult> Fail(ErrorCode code, string message) =>
        Result.Failure<ImageConversionResult>(new OperationError(code, message));
}
