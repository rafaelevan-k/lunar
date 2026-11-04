using Lunar.Application.Conversion.Models;
using SkiaSharp;

namespace Lunar.Conversion.Providers;

/// <summary>
/// The picture operations the providers share: how an EXIF orientation turns the pixels, and painting a picture upright (and, for a
/// target that cannot hold transparency, over a background colour) in one pass.
/// </summary>
internal static class PictureRendering
{
    /// <summary>True for the four orientations that exchange the picture's width and height (EXIF 5 to 8).</summary>
    public static bool SwapsSides(SKEncodedOrigin origin) =>
        origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

    /// <summary>Maps a source point (x, y) to where it lands once the picture is shown the right way up (EXIF orientation values 1 to 8).</summary>
    public static SKMatrix OrientationMatrix(SKEncodedOrigin origin, int w, int h) => origin switch
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

    /// <summary>
    /// Decodes the picture exactly as stored (not turned) to 8-bit sRGB, premultiplied when it can hold transparency. Null when the data is
    /// damaged or cut short. The caller disposes the bitmap.
    /// </summary>
    public static SKBitmap? DecodeRaw(SKCodec codec)
    {
        var info = codec.Info;
        var decodeInfo = new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, info.AlphaType != SKAlphaType.Opaque ? SKAlphaType.Premul : SKAlphaType.Opaque, SKColorSpace.CreateSrgb());
        var bitmap = new SKBitmap(decodeInfo);
        if (codec.GetPixels(decodeInfo, bitmap.GetPixels()) == SKCodecResult.Success)
            return bitmap;
        bitmap.Dispose();
        return null;
    }

    /// <summary>
    /// Turns the pixels by the orientation and, when a <paramref name="background"/> is given, paints them over that colour (the result
    /// then has no transparency): one pass, one extra bitmap. The caller disposes the result.
    /// </summary>
    public static SKBitmap Render(SKBitmap source, SKEncodedOrigin origin, RgbColor? background)
    {
        var swap = SwapsSides(origin);
        var width = swap ? source.Height : source.Width;
        var height = swap ? source.Width : source.Height;
        var alpha = background is null ? source.AlphaType : SKAlphaType.Opaque;
        var target = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, alpha, source.ColorSpace));
        try
        {
            using var canvas = new SKCanvas(target);
            canvas.Clear(background is { } color ? new SKColor(color.R, color.G, color.B) : SKColors.Transparent);
            canvas.SetMatrix(OrientationMatrix(origin, source.Width, source.Height));
            canvas.DrawBitmap(source, 0, 0, SKSamplingOptions.Default);
            return target;
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }
}
