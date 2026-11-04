using System.Buffers.Binary;
using Lunar.Application.Abstractions.Conversion;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Conversion.Models;
using Lunar.Conversion.Output;
using Lunar.Conversion.Pipeline;
using Lunar.Conversion.Providers;
using Lunar.Desktop.Composition;
using Lunar.Domain.Conversion;
using Lunar.Windows.IntegrationTests.Support;
using Lunar.Windows.Pdf;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit;

namespace Lunar.Desktop.UITests.Composition;

// BE-24: the three layers working together on the real operating-system PDF renderer: a PDF made by Lunar's own PDF provider, or by the
// PDF writer directly, is drawn through the container's rasterizer by the conversion provider and published by the output step. This is
// Windows evidence for the whole PDF-to-picture route; it uses synthetic pictures only.
public sealed class PdfConversionCompositionTests : IDisposable
{
    private sealed class NullLog : IDiagnosticLog
    {
        public void Write(DiagnosticEvent diagnosticEvent)
        {
        }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lunar-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _services;
    private readonly TempWorkspace _workspace;
    private readonly ConversionLimits _limits = new();

    public PdfConversionCompositionTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "in"));
        Directory.CreateDirectory(Path.Combine(_root, "out"));
        // The real log is replaced so the test never writes into the user's %LocalAppData%\Lunar\Logs.
        _services = ServiceRegistration.BuildServiceProvider(s => s.AddSingleton<IDiagnosticLog>(new NullLog()));
        _workspace = TempWorkspace.Create(Path.Combine(_root, "work"));
    }

    public void Dispose()
    {
        _workspace.Dispose();
        _services.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string In(string name) => Path.Combine(_root, "in", name);

    private string Out => Path.Combine(_root, "out");

    private static (byte R, byte G, byte B) ColorOfPage(int number) => ((byte)(30 + (number * 40 % 200)), (byte)(200 - (number * 30 % 150)), (byte)(60 + (number * 55 % 180)));

    private string MakePdf(string name, int pages)
    {
        var path = In(name);
        using var stream = new FileStream(path, FileMode.CreateNew);
        using var document = SKDocument.CreatePdf(stream);
        for (var page = 1; page <= pages; page++)
        {
            var canvas = document.BeginPage(300, 200);
            var (r, g, b) = ColorOfPage(page);
            canvas.Clear(new SKColor(r, g, b));
            using var black = new SKPaint { Color = SKColors.Black };
            canvas.DrawRect(0, 150, 50, 50, black); // bottom left corner
            document.EndPage();
        }

        document.Close();
        return path;
    }

    private PlannedConversion Job(string source, FileFormat target, string baseName) => new(
        0, source, FileFormat.Pdf, new FileInfo(source).Length, File.GetLastWriteTimeUtc(source),
        ConversionPolicy.Resolve(FileFormat.Pdf, target), Out, baseName, FileFormats.Extension(target), false, false);

    private static uint PixelsPerMetre(string pngPath)
    {
        var bytes = File.ReadAllBytes(pngPath);
        for (var position = 8; position + 12 <= bytes.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position));
            if (System.Text.Encoding.ASCII.GetString(bytes, position + 4, 4) == "pHYs")
                return BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position + 8));
            position += 12 + length;
        }

        return 0;
    }

    [Fact]
    public void TheContainerGivesTheWindowsRasterizerAsOneSingleton()
    {
        var first = _services.GetRequiredService<IPdfPageRasterizer>();

        Assert.IsType<WindowsPdfPageRasterizer>(first);
        Assert.Same(first, _services.GetRequiredService<IPdfPageRasterizer>());
    }

    [Fact]
    public async Task PagesOfARealPdfBecomeNumberedPngsWithTheRightColoursSizeAndResolution()
    {
        var source = MakePdf("five.pdf", 5);
        var provider = new PdfRasterizationProvider(_services.GetRequiredService<IPdfPageRasterizer>());
        PageSelection.TryParse("2, 4-5", out var pages, out _);

        var drawn = await provider.RasterizeAsync(source, null, FileFormat.Png, new PdfRasterizationOptions { Dpi = 150, Pages = pages },
            new ImageEncodingOptions(), _workspace, _limits, null, Ct);

        Assert.True(drawn.IsSuccess, drawn.Error?.SafeMessage);
        Assert.Equal(5, drawn.Value.DocumentPageCount);
        Assert.Equal([2, 4, 5], drawn.Value.Pages.Select(p => p.PageNumber));
        Assert.All(drawn.Value.Pages, p => Assert.InRange(p.WidthPixels, 623, 627)); // 300 pt at 150 pixels per inch = 625
        Assert.All(drawn.Value.Pages, p => Assert.InRange(p.HeightPixels, 413, 421)); // 200 pt = 417
        foreach (var page in drawn.Value.Pages)
        {
            using var bitmap = SKBitmap.Decode(page.StagedPath);
            var body = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 4);
            var expected = ColorOfPage(page.PageNumber);
            Assert.True(Math.Abs(body.Red - expected.R) <= 6 && Math.Abs(body.Green - expected.G) <= 6 && Math.Abs(body.Blue - expected.B) <= 6,
                $"page {page.PageNumber}: got ({body.Red},{body.Green},{body.Blue}) expected {expected}");
            var corner = bitmap.GetPixel(10, bitmap.Height - 10);
            Assert.True(corner.Red < 10 && corner.Green < 10 && corner.Blue < 10, "The black square must be at the bottom left.");
            Assert.Equal(5906u, PixelsPerMetre(page.StagedPath)); // 150 pixels per inch; the screen's own resolution was replaced
        }

        var commit = await new OutputCommitter().CommitAsync(Job(source, FileFormat.Png, "five"), _workspace, drawn.Value.StagedFiles, drawn.Value.Numbering, _limits, Ct);

        Assert.True(commit.IsSuccess, commit.Error?.SafeMessage);
        Assert.Equal(["five_page_002.png", "five_page_004.png", "five_page_005.png"], commit.Value.Select(Path.GetFileName));
        Assert.Equal(3, Directory.GetFiles(Out).Length);
    }

    [Fact]
    public async Task APdfMadeByLunarFromAPictureComesBackAsThePictureWithItsColours()
    {
        // picture -> PDF (BE-23) -> pictures (BE-24): the round trip keeps the quadrants, and the source is read only.
        using var bitmap = new SKBitmap(new SKImageInfo(120, 80, SKColorType.Rgba8888, SKAlphaType.Opaque));
        for (var y = 0; y < 80; y++)
        {
            for (var x = 0; x < 120; x++)
                bitmap.SetPixel(x, y, (x < 60, y < 40) switch { (true, true) => SKColors.Red, (false, true) => SKColors.Lime, (true, false) => SKColors.Blue, _ => SKColors.Yellow });
        }

        var pngPath = In("quadrants.png");
        using (var image = SKImage.FromBitmap(bitmap))
            File.WriteAllBytes(pngPath, image.Encode(SKEncodedImageFormat.Png, 100).ToArray());
        var pdfPath = Path.Combine(_workspace.Directory, "made.pdf");
        var made = await new PdfCreationProvider().CreateAsync(pngPath, FileFormat.Png, pdfPath, new PageLayoutOptions(), _limits, Ct);
        Assert.True(made.IsSuccess, made.Error?.SafeMessage);
        var before = File.ReadAllBytes(pdfPath);

        var provider = new PdfRasterizationProvider(_services.GetRequiredService<IPdfPageRasterizer>());
        var drawn = await provider.RasterizeAsync(pdfPath, null, FileFormat.Jpg, new PdfRasterizationOptions { Dpi = 150 },
            new ImageEncodingOptions { JpegQuality = 100 }, _workspace, _limits, null, Ct);

        Assert.True(drawn.IsSuccess, drawn.Error?.SafeMessage);
        var page = Assert.Single(drawn.Value.Pages);
        using var result = SKBitmap.Decode(page.StagedPath);
        var corners = new[] { (result.Width / 4, result.Height / 4, SKColors.Red), (3 * result.Width / 4, result.Height / 4, SKColors.Lime), (result.Width / 4, 3 * result.Height / 4, SKColors.Blue), (3 * result.Width / 4, 3 * result.Height / 4, SKColors.Yellow) };
        foreach (var (x, y, expected) in corners)
        {
            var c = result.GetPixel(x, y);
            Assert.True(Math.Abs(c.Red - expected.Red) <= 40 && Math.Abs(c.Green - expected.Green) <= 40 && Math.Abs(c.Blue - expected.Blue) <= 40, $"({x},{y}): got ({c.Red},{c.Green},{c.Blue})");
        }

        Assert.Equal(before, File.ReadAllBytes(pdfPath));
    }

    [Fact]
    public async Task APageTooLargeForTheChosenResolutionStopsTheRealJobBeforeDrawing()
    {
        // A 100 x 100 inch page: 30000 x 30000 pixels at 300 pixels per inch.
        var path = In("poster.pdf");
        using (var stream = new FileStream(path, FileMode.CreateNew))
        using (var document = SKDocument.CreatePdf(stream))
        {
            document.BeginPage(7200, 7200).Clear(SKColors.White);
            document.EndPage();
            document.Close();
        }

        var provider = new PdfRasterizationProvider(_services.GetRequiredService<IPdfPageRasterizer>());

        var result = await provider.RasterizeAsync(path, null, FileFormat.Png, new PdfRasterizationOptions { Dpi = 300 },
            new ImageEncodingOptions(), _workspace, _limits, null, Ct);

        Assert.True(result.IsFailure);
        Assert.Contains("lower resolution", result.Error!.SafeMessage, StringComparison.Ordinal);
        Assert.All(Directory.GetFiles(_workspace.Directory), f => Assert.EndsWith("journal.txt", f, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AHundredPagePdfIsDrawnPageByPageAndPublishedWithThreeDigitNumbers()
    {
        var source = MakePdf("hundred.pdf", 100);
        var progress = new List<int>();
        var provider = new PdfRasterizationProvider(_services.GetRequiredService<IPdfPageRasterizer>());
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var drawn = await provider.RasterizeAsync(source, null, FileFormat.Png, new PdfRasterizationOptions { Dpi = 150 },
            new ImageEncodingOptions(), _workspace, _limits, new Reporter(progress), Ct);

        Assert.True(drawn.IsSuccess, drawn.Error?.SafeMessage);
        Assert.Equal(100, drawn.Value.Pages.Count);
        Assert.Equal(Enumerable.Range(1, 100), drawn.Value.Pages.Select(p => p.PageNumber));
        Assert.Equal(101, progress.Count);
        Assert.Equal(100, progress[^1]);
        foreach (var page in drawn.Value.Pages.Where(p => p.PageNumber is 1 or 37 or 100))
        {
            using var bitmap = SKBitmap.Decode(page.StagedPath);
            var body = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 4);
            var expected = ColorOfPage(page.PageNumber);
            Assert.True(Math.Abs(body.Red - expected.R) <= 6 && Math.Abs(body.Green - expected.G) <= 6 && Math.Abs(body.Blue - expected.B) <= 6, $"page {page.PageNumber}");
        }

        var commit = await new OutputCommitter().CommitAsync(Job(source, FileFormat.Png, "hundred"), _workspace, drawn.Value.StagedFiles, drawn.Value.Numbering, _limits, Ct);
        Assert.True(commit.IsSuccess, commit.Error?.SafeMessage);
        Assert.Equal("hundred_page_001.png", Path.GetFileName(commit.Value[0]));
        Assert.Equal("hundred_page_100.png", Path.GetFileName(commit.Value[^1]));
        Assert.Equal(100, Directory.GetFiles(Out).Length);
        Assert.True(watch.Elapsed < TimeSpan.FromMinutes(2), $"100 pages took {watch.Elapsed}.");
    }

    [Fact]
    public async Task AProtectedPdfNeedsItsPasswordAndThenConvertsLikeAnyOther()
    {
        var path = In("protected.pdf");
        File.WriteAllBytes(path, PdfFixtures.Build(3, userPassword: "s3cret-pw"));
        var provider = new PdfRasterizationProvider(_services.GetRequiredService<IPdfPageRasterizer>());
        var options = new PdfRasterizationOptions { Dpi = 72 };

        var without = await provider.RasterizeAsync(path, null, FileFormat.Png, options, new ImageEncodingOptions(), _workspace, _limits, null, Ct);
        var wrong = await provider.RasterizeAsync(path, "nope", FileFormat.Png, options, new ImageEncodingOptions(), _workspace, _limits, null, Ct);
        var right = await provider.RasterizeAsync(path, "s3cret-pw", FileFormat.Png, options, new ImageEncodingOptions(), _workspace, _limits, null, Ct);

        Assert.Equal(Lunar.Domain.Shared.ErrorCode.AccessDenied, without.Error!.Code);
        Assert.Contains("protected by a password", without.Error.SafeMessage, StringComparison.Ordinal);
        Assert.Equal(Lunar.Domain.Shared.ErrorCode.AccessDenied, wrong.Error!.Code);
        Assert.Contains("didn't open", wrong.Error.SafeMessage, StringComparison.Ordinal);
        Assert.True(right.IsSuccess, right.Error?.SafeMessage);
        Assert.Equal(3, right.Value.Pages.Count);
        Assert.DoesNotContain("s3cret", without.Error.SafeMessage + wrong.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADamagedPdfIsRefusedCleanlyByTheWholeRoute()
    {
        var good = PdfFixtures.Build(3);
        var path = In("damaged.pdf");
        File.WriteAllBytes(path, [.. good.Take(good.Length / 2), .. System.Text.Encoding.ASCII.GetBytes("\n%%EOF\n")]);
        var provider = new PdfRasterizationProvider(_services.GetRequiredService<IPdfPageRasterizer>());

        var result = await provider.RasterizeAsync(path, null, FileFormat.Png, new PdfRasterizationOptions(), new ImageEncodingOptions(), _workspace, _limits, null, Ct);

        Assert.True(result.IsFailure);
        Assert.Equal(Lunar.Domain.Shared.ErrorCode.InvalidInput, result.Error!.Code);
        Assert.All(Directory.GetFiles(_workspace.Directory), f => Assert.EndsWith("journal.txt", f, StringComparison.Ordinal));
    }

    private sealed class Reporter(List<int> seen) : IProgress<JobProgress>
    {
        public void Report(JobProgress value) => seen.Add(value.PagesCompleted ?? -1);
    }
}
