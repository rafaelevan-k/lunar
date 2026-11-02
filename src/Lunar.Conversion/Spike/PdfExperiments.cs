using SkiaSharp;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;

namespace Lunar.Conversion.Spike;

internal static class PdfExperiments
{
    internal static async Task Run(string workspace)
    {
        var input = Path.Combine(workspace, "fixture-two-pages.pdf");
        using (var stream = new FileStream(input, FileMode.CreateNew))
        using (var document = SKDocument.CreatePdf(stream))
        using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true })
        using (var face = SKTypeface.FromFamilyName("Arial"))
        using (var font = new SKFont(face, 20))
        using (var image = SKBitmap.Decode(Path.Combine(workspace, "fixture-alpha-\u00e9-\u6d4b\u8bd5.png")))
        {
            var page = document.BeginPage(612, 792);
            page.Clear(SKColors.White);
            page.DrawText("Lunar image PDF fixture", 36, 72, SKTextAlign.Left, font, paint);
            page.DrawBitmap(image, new SKRect(36, 136, 576, 406), SKSamplingOptions.Default);
            document.EndPage();
            page = document.BeginPage(612, 792);
            page.Clear(SKColors.White);
            page.DrawText("Lunar vector PDF fixture", 36, 72, SKTextAlign.Left, font, paint);
            paint.Color = SKColors.Blue; page.DrawRect(72, 144, 180, 120, paint);
            paint.Color = new SKColor(255, 0, 0, 128); page.DrawCircle(252, 264, 90, paint);
            document.EndPage();
            document.Close();
            Program.Record("Image-PDF-Creation", "PASS", new { Pages = 2, Points = new[] { 612, 792 }, Font = face.FamilyName,
                Boundary = "Synthetic image and vector PDF; not arbitrary document fidelity" });
        }
        var before = Program.Hash(input);
        await Render(input, workspace, "pdf-150", 150, 2);
        await Render(input, workspace, "pdf-300", 300, 2);
        Program.Check(Program.Hash(input) == before, "Original PDF changed");
        Program.Record("OriginalPdfPreserved", "PASS", new { SHA256Unchanged = true });

        var many = Path.Combine(workspace, "fixture-100-pages.pdf");
        using (var stream = new FileStream(many, FileMode.CreateNew))
        using (var document = SKDocument.CreatePdf(stream))
        using (var paint = new SKPaint { Color = SKColors.Black })
        using (var face = SKTypeface.FromFamilyName("Arial"))
        using (var font = new SKFont(face, 18))
        {
            for (var i = 1; i <= 100; i++)
            {
                var page = document.BeginPage(612, 792);
                page.Clear(SKColors.White); page.DrawText($"Synthetic page {i}", 36, 72, SKTextAlign.Left, font, paint);
                document.EndPage();
            }
            document.Close();
        }
        using (var stream = File.OpenRead(many))
        using (var random = stream.AsRandomAccessStream())
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            var document = await PdfDocument.LoadFromStreamAsync(random).AsTask(timeout.Token);
            Program.Check(document.PageCount == 100, "100-page PDF count mismatch");
            Program.Record("PDF-100PageOpen", "PASS", new { document.PageCount,
                Boundary = "Open/page-count only; not 100-page rasterization, memory stress or cancellation acceptance" });
        }
        try
        {
            using var stream = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 });
            using var random = stream.AsRandomAccessStream();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await PdfDocument.LoadFromStreamAsync(random).AsTask(timeout.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Program.Record("CorruptPdfRejected", "PASS", new { ErrorType = ex.GetType().Name, HResult = $"0x{ex.HResult:X8}" });
            return;
        }
        throw new InvalidOperationException("Corrupt PDF was not rejected");
    }

    internal static async Task Render(string input, string workspace, string prefix, uint dpi, uint expectedPages)
    {
        using var stream = File.OpenRead(input);
        using var random = stream.AsRandomAccessStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var document = await PdfDocument.LoadFromStreamAsync(random).AsTask(timeout.Token);
        Program.Check(document.PageCount == expectedPages, "PDF page count mismatch");
        for (uint i = 0; i < document.PageCount; i++)
        {
            using var page = document.GetPage(i);
            var width = (uint)Math.Round(page.Size.Width * dpi / 96.0);
            var height = (uint)Math.Round(page.Size.Height * dpi / 96.0);
            Program.Check(width > 0 && height > 0 && (ulong)width * height <= 9_000_000, "Synthetic render exceeds cap");
            // WinRT destinations are DIPs; observed native PNG size also applies host DPI.
            // The experiment normalizes this explicitly and asserts actual output pixels.
            var destinationWidth = (uint)Math.Round(width * 96.0 / Program.SystemDpi);
            var destinationHeight = (uint)Math.Round(height * 96.0 / Program.SystemDpi);
            var path = Path.Combine(workspace, $"{prefix}-page-{i + 1:000}.png");
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite))
            using (var destination = output.AsRandomAccessStream())
            {
                await page.RenderToStreamAsync(destination, new PdfPageRenderOptions
                { DestinationWidth = destinationWidth, DestinationHeight = destinationHeight }).AsTask(timeout.Token);
                output.Flush();
            }
            using var decoded = SKBitmap.Decode(path) ?? throw new InvalidOperationException("Raster decode failed");
            Program.Record("PDF-RasterDimensions", "INFO", new { Fixture = prefix, Page = i + 1,
                PageDipWidth = page.Size.Width, PageDipHeight = page.Size.Height,
                ExpectedPixelWidth = width, ExpectedPixelHeight = height, DestinationDipWidth = destinationWidth,
                DestinationDipHeight = destinationHeight, ActualWidth = decoded.Width, ActualHeight = decoded.Height });
            Program.Check(decoded.Width == width && decoded.Height == height, "Raster dimensions differ");
            if (prefix.StartsWith("pdf-", StringComparison.Ordinal) && i == 0)
            {
                var px = (int)Math.Round(50 * dpi / 72.0);
                var py = (int)Math.Round(150 * dpi / 72.0);
                var red = decoded.GetPixel(px, py);
                Program.Check(red.Red > 245 && red.Green < 15 && red.Blue < 15, "Raster image anchor differs");
            }
            Program.Record("PDF-PageRaster", "PASS", new { Fixture = prefix, Page = i + 1, Dpi = dpi,
                Width = decoded.Width, Height = decoded.Height, InputReadOnly = true });
            await NormalizeResolution(path, dpi, timeout.Token);
        }
    }

    // A separate synthetic output preserves the native result for comparison and failed-run evidence.
    private static async Task NormalizeResolution(string input, uint dpi, CancellationToken ct)
    {
        using var source = File.OpenRead(input);
        using var sourceRandom = source.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(sourceRandom).AsTask(ct);
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
            new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask(ct);
        var bytes = pixels.DetachPixelData();
        Program.Check(bytes.LongLength <= 36_000_000, "Synthetic PNG buffer exceeds cap");
        var outputPath = Path.Combine(Path.GetDirectoryName(input)!, Path.GetFileNameWithoutExtension(input) + "-normalized.png");
        using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.ReadWrite))
        using (var outputRandom = output.AsRandomAccessStream())
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outputRandom).AsTask(ct);
            // Synthetic OS-rendered sRGB pixels only; this does not establish ICC preservation.
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
                decoder.PixelWidth, decoder.PixelHeight, dpi, dpi, bytes);
            await encoder.FlushAsync().AsTask(ct);
        }
        using var result = File.OpenRead(outputPath);
        using var resultRandom = result.AsRandomAccessStream();
        var verified = await BitmapDecoder.CreateAsync(resultRandom).AsTask(ct);
        var resultPixels = await verified.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
            new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask(ct);
        Program.Check(verified.PixelWidth == decoder.PixelWidth && verified.PixelHeight == decoder.PixelHeight,
            "Normalized PNG geometry changed");
        Program.Check(Math.Abs(verified.DpiX - dpi) < 0.02 && Math.Abs(verified.DpiY - dpi) < 0.02,
            "Normalized PNG density differs");
        Program.Check(bytes.AsSpan().SequenceEqual(resultPixels.DetachPixelData()), "Normalized PNG pixels changed");
        Program.Record("PNG-ResolutionNormalization", "PASS", new { NativeDpiX = decoder.DpiX,
            NativeDpiY = decoder.DpiY, RequestedDpi = dpi, ActualDpiX = verified.DpiX,
            ActualDpiY = verified.DpiY, verified.PixelWidth, verified.PixelHeight, PixelsUnchanged = true,
            Boundary = "Synthetic sRGB PNG only; other DPI/OS/ICC contexts remain unverified" });
    }
}
