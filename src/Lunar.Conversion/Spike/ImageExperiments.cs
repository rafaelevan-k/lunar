using SkiaSharp;

namespace Lunar.Conversion.Spike;

internal static class ImageExperiments
{
    internal static void Save(SKBitmap bitmap, string path, SKEncodedImageFormat format, int quality = 100)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality) ?? throw new InvalidOperationException("Encode failed");
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        data.SaveTo(output);
    }

    internal static void Run(string workspace)
    {
        using var source = new SKBitmap(640, 320, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(source))
        using (var paint = new SKPaint())
        {
            canvas.Clear(SKColors.Transparent);
            paint.Color = SKColors.Red; canvas.DrawRect(0, 0, 280, 160, paint);
            paint.Color = SKColors.Blue; canvas.DrawRect(360, 0, 280, 160, paint);
            paint.Color = SKColors.Green; canvas.DrawRect(0, 160, 280, 160, paint);
            paint.Color = SKColors.Yellow; canvas.DrawRect(360, 160, 280, 160, paint);
        }
        var png = Path.Combine(workspace, "fixture-alpha-\u00e9-\u6d4b\u8bd5.png");
        Save(source, png, SKEncodedImageFormat.Png);
        var originalHash = Program.Hash(png);
        using var decoded = SKBitmap.Decode(png) ?? throw new InvalidOperationException("PNG decode failed");
        var mismatch = 0;
        for (var y = 0; y < source.Height; y++)
            for (var x = 0; x < source.Width; x++)
                if (source.GetPixel(x, y) != decoded.GetPixel(x, y)) mismatch++;
        Program.Check(mismatch == 0, "PNG pixel roundtrip mismatch");
        Program.Record("PNG-UnicodeAlphaRoundtrip", "PASS", new { Width = 640, Height = 320, PixelsCompared = 204800, Mismatched = mismatch });

        using var opaque = new SKBitmap(source.Width, source.Height);
        using (var canvas = new SKCanvas(opaque)) { canvas.Clear(SKColors.White); canvas.DrawBitmap(source, 0, 0, SKSamplingOptions.Default); }
        var jpeg = Path.Combine(workspace, "output-alpha-white.jpg");
        Save(opaque, jpeg, SKEncodedImageFormat.Jpeg, 90);
        using var jpegDecoded = SKBitmap.Decode(jpeg) ?? throw new InvalidOperationException("JPEG decode failed");
        var white = jpegDecoded.GetPixel(320, 20);
        Program.Check(white.Red >= 250 && white.Green >= 250 && white.Blue >= 250, "Alpha background not white");
        Program.Record("PNG-JPEG-WhiteBackground", "PASS", new { Quality = 90, Sample = new[] { (int)white.Red, (int)white.Green, (int)white.Blue }, Lossy = true });

        // Add a minimal EXIF APP1 orientation=6 segment to a synthetic JPEG, not a user image.
        var payload = new byte[] { 69,120,105,102,0,0,73,73,42,0,8,0,0,0,1,0,18,1,3,0,1,0,0,0,6,0,0,0,0,0,0,0 };
        var bytes = File.ReadAllBytes(jpeg);
        var exif = Path.Combine(workspace, "fixture-orientation-6.jpg");
        using (var output = new FileStream(exif, FileMode.CreateNew))
        {
            output.Write(bytes, 0, 2);
            output.Write(new byte[] { 255, 225, 0, 34 });
            output.Write(payload);
            output.Write(bytes, 2, bytes.Length - 2);
        }
        using var codec = SKCodec.Create(exif) ?? throw new InvalidOperationException("EXIF JPEG decode failed");
        Program.Check(codec.EncodedOrigin == SKEncodedOrigin.RightTop, "EXIF orientation not detected");
        using var raw = SKBitmap.Decode(codec) ?? throw new InvalidOperationException("JPEG pixel decode failed");
        using var oriented = new SKBitmap(raw.Height, raw.Width);
        using (var canvas = new SKCanvas(oriented))
        {
            canvas.Translate(oriented.Width, 0);
            canvas.RotateDegrees(90);
            canvas.DrawBitmap(raw, 0, 0, SKSamplingOptions.Default);
        }
        Save(oriented, Path.Combine(workspace, "output-oriented.png"), SKEncodedImageFormat.Png);
        var rotatedCorner = oriented.GetPixel(20, 20);
        Program.Check(rotatedCorner.Green > 90 && rotatedCorner.Red < 20 && rotatedCorner.Blue < 20, "Rotation corner incorrect");
        Program.Check(Program.Hash(png) == originalHash, "Source PNG changed");
        Program.Record("JPEG-EXIF-Orientation6", "PASS", new { ReportedOrigin = codec.EncodedOrigin.ToString(),
            Width = oriented.Width, Height = oriented.Height, ExplicitOrientationTransformRequired = true, OriginalPngUnchanged = true });
        using var garbage = SKData.CreateCopy(new byte[] { 1, 2, 3, 4, 5 });
        using var invalid = SKCodec.Create(garbage);
        Program.Check(invalid is null, "Corrupt image accepted");
        Program.Record("CorruptImageRejected", "PASS", new { SyntheticBytes = 5, Boundary = "One corrupt fixture, not an adversarial corpus" });
    }
}
