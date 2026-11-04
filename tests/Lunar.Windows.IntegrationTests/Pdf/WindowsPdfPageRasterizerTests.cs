using Lunar.Application.Abstractions.Conversion;
using Lunar.Application.Conversion.Models;
using Lunar.Domain.Shared;
using Lunar.Windows.IntegrationTests.Support;
using Lunar.Windows.Pdf;
using Windows.Graphics.Imaging;
using Xunit;

namespace Lunar.Windows.IntegrationTests.Pdf;

// BE-24: the real operating-system PDF renderer behind the port. PDFs are written by hand (see PdfFixtures) and the drawn pages are read
// back with the operating system's own image decoder, so nothing here is a fake: this is the Windows evidence for the adapter.
public sealed class WindowsPdfPageRasterizerTests : IAsyncLifetime
{
    private readonly List<IAsyncDisposable> _toDispose = [];
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "lunar-pdf-" + Guid.NewGuid().ToString("N"));

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var item in _toDispose)
            await item.DisposeAsync();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<IPdfDocument> Open(byte[] pdf, string? password = null, IPdfPageRasterizer? rasterizer = null)
    {
        var stream = new MemoryStream(pdf);
        var result = await (rasterizer ?? new WindowsPdfPageRasterizer()).OpenAsync(stream, password, Ct);
        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        _toDispose.Add(result.Value);
        return result.Value;
    }

    private static async Task<(int Width, int Height, byte[] Bgra)> Decode(Stream png)
    {
        png.Position = 0;
        var decoder = await BitmapDecoder.CreateAsync(png.AsRandomAccessStream());
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return ((int)decoder.PixelWidth, (int)decoder.PixelHeight, data.DetachPixelData());
    }

    private static (byte R, byte G, byte B) At((int Width, int Height, byte[] Bgra) image, int x, int y)
    {
        var i = ((y * image.Width) + x) * 4;
        return (image.Bgra[i + 2], image.Bgra[i + 1], image.Bgra[i]);
    }

    private static void AssertNear((byte R, byte G, byte B) actual, (byte R, byte G, byte B) expected, int tolerance, string what)
    {
        Assert.True(
            Math.Abs(actual.R - expected.R) <= tolerance && Math.Abs(actual.G - expected.G) <= tolerance && Math.Abs(actual.B - expected.B) <= tolerance,
            $"{what}: expected about {expected} but got {actual}.");
    }

    // ---- opening -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ADocumentReportsItsPageCountAndPageSizesInPoints()
    {
        var document = await Open(PdfFixtures.Build(3, size: i => i == 1 ? (300, 500) : (612, 792)));

        Assert.Equal(3, document.PageCount);
        var first = document.GetPageSize(0).Value;
        var second = document.GetPageSize(1).Value;
        Assert.Equal((612.0, 792.0), (Math.Round(first.WidthPoints, 1), Math.Round(first.HeightPoints, 1)));
        Assert.Equal((300.0, 500.0), (Math.Round(second.WidthPoints, 1), Math.Round(second.HeightPoints, 1)));
    }

    [Fact]
    public async Task APageThatDoesNotExistIsRefused()
    {
        var document = await Open(PdfFixtures.Build(2));
        using var destination = new MemoryStream();

        Assert.True(document.GetPageSize(2).IsFailure);
        Assert.True(document.GetPageSize(-1).IsFailure);
        Assert.Equal(ErrorCode.InvalidInput, (await document.RenderPageAsync(5, 100, 100, destination, Ct)).Error!.Code);
    }

    [Fact]
    public async Task NonPdfAndDamagedDataAreInvalidInputNotACrash()
    {
        var rasterizer = new WindowsPdfPageRasterizer();
        var damaged = PdfFixtures.Build(2);
        var cases = new[]
        {
            new byte[] { 1, 2, 3, 4, 5 },
            "just some text"u8.ToArray(),
            damaged[..(damaged.Length / 2)],
            [.. damaged.Take(100), .. new byte[500]]
        };

        foreach (var bytes in cases)
        {
            using var stream = new MemoryStream(bytes);

            var result = await rasterizer.OpenAsync(stream, null, Ct);

            Assert.True(result.IsFailure);
            Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
            Assert.False(string.IsNullOrWhiteSpace(result.Error.SafeMessage));
        }
    }

    [Fact]
    public async Task ACallerStreamIsNeverClosedByTheRasterizer()
    {
        var stream = new MemoryStream(PdfFixtures.Build(1));
        var opened = await new WindowsPdfPageRasterizer().OpenAsync(stream, null, Ct);
        var document = opened.Value;
        using var destination = new MemoryStream();
        Assert.True((await document.RenderPageAsync(0, 200, 260, destination, Ct)).IsSuccess);

        await document.DisposeAsync();

        Assert.True(stream.CanRead);   // still open: the caller owns it
        Assert.True(destination.CanWrite);
    }

    [Fact]
    public async Task AnUnreadableStreamIsRefused()
    {
        var rasterizer = new WindowsPdfPageRasterizer();
        using var notSeekable = new NonSeekable(new MemoryStream(PdfFixtures.Build(1)));

        var result = await rasterizer.OpenAsync(notSeekable, null, Ct);

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        await Assert.ThrowsAsync<ArgumentNullException>(() => rasterizer.OpenAsync(null!, null, Ct));
    }

    [Fact]
    public async Task ACancelBeforeOpenIsCancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var stream = new MemoryStream(PdfFixtures.Build(1));

        var result = await new WindowsPdfPageRasterizer().OpenAsync(stream, null, cts.Token);

        Assert.True(result.IsCancelled);
    }

    // ---- passwords ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AProtectedPdfNeedsItsPasswordAndTheMessagesTellWhichCaseItIs()
    {
        var protectedPdf = PdfFixtures.Build(2, userPassword: "s3cret-pw");
        var rasterizer = new WindowsPdfPageRasterizer();

        using var none = new MemoryStream(protectedPdf);
        var withoutPassword = await rasterizer.OpenAsync(none, null, Ct);
        using var empty = new MemoryStream(protectedPdf);
        var emptyPassword = await rasterizer.OpenAsync(empty, "", Ct);
        using var wrong = new MemoryStream(protectedPdf);
        var wrongPassword = await rasterizer.OpenAsync(wrong, "not-it", Ct);

        foreach (var result in new[] { withoutPassword, emptyPassword, wrongPassword })
        {
            Assert.True(result.IsFailure);
            Assert.Equal(ErrorCode.AccessDenied, result.Error!.Code);
            Assert.DoesNotContain("s3cret", result.Error.SafeMessage, StringComparison.Ordinal);
            Assert.DoesNotContain("not-it", result.Error.SafeMessage, StringComparison.Ordinal);
        }

        Assert.Contains("protected by a password", withoutPassword.Error!.SafeMessage, StringComparison.Ordinal);
        Assert.Contains("protected by a password", emptyPassword.Error!.SafeMessage, StringComparison.Ordinal);
        Assert.Contains("didn't open", wrongPassword.Error!.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRightPasswordOpensAndDrawsAProtectedPdf()
    {
        var document = await Open(PdfFixtures.Build(2, userPassword: "s3cret-pw"), "s3cret-pw");
        using var destination = new MemoryStream();

        var result = await document.RenderPageAsync(1, 306, 396, destination, Ct);

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        var image = await Decode(destination);
        AssertNear(At(image, image.Width / 2, image.Height / 2), PdfFixtures.PageColor(1), 4, "protected page body");
    }

    [Fact]
    public async Task AnUnprotectedPdfIgnoresAPasswordThatWasNotNeeded()
    {
        var document = await Open(PdfFixtures.Build(1), "unneeded");

        Assert.Equal(1, document.PageCount);
    }

    // ---- drawing -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(306, 396)]
    [InlineData(612, 792)]
    [InlineData(1275, 1650)]
    [InlineData(2550, 3300)]
    public async Task APageIsDrawnAtTheRequestedPixelSizeWhateverTheScreenScaleIs(int width, int height)
    {
        var document = await Open(PdfFixtures.Build(1));
        using var destination = new MemoryStream();

        var result = await document.RenderPageAsync(0, width, height, destination, Ct);

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        var image = await Decode(destination);
        Assert.Equal((image.Width, image.Height), (result.Value.WidthPixels, result.Value.HeightPixels));
        Assert.InRange(image.Width, width - 3, width + 3);
        Assert.InRange(image.Height, height - 3, height + 3);
    }

    [Fact]
    public async Task APageIsDrawnOnWhitePaperWithTheRightColoursAndNotTurned()
    {
        var document = await Open(PdfFixtures.Build(1));
        using var destination = new MemoryStream();

        Assert.True((await document.RenderPageAsync(0, 612, 792, destination, Ct)).IsSuccess);

        var image = await Decode(destination);
        AssertNear(At(image, image.Width / 2, image.Height / 4), PdfFixtures.PageColor(0), 4, "page colour");
        AssertNear(At(image, image.Width / 16, image.Height - (image.Height / 16)), (0, 0, 0), 4, "black square is at the bottom left");
        AssertNear(At(image, image.Width / 16, image.Height / 16), PdfFixtures.PageColor(0), 4, "top left is not black");
    }

    [Fact]
    public async Task PagesComeOutInTheOrderAskedForWithTheirOwnColours()
    {
        var document = await Open(PdfFixtures.Build(5));
        foreach (var index in new[] { 4, 0, 2 })
        {
            using var destination = new MemoryStream();

            Assert.True((await document.RenderPageAsync(index, 200, 260, destination, Ct)).IsSuccess);

            var image = await Decode(destination);
            AssertNear(At(image, image.Width / 2, image.Height / 4), PdfFixtures.PageColor(index), 4, $"page {index + 1}");
        }
    }

    [Fact]
    public async Task ADestinationThatAlreadyHoldsDataIsOverwrittenNotAppendedTo()
    {
        var document = await Open(PdfFixtures.Build(1));
        using var destination = new MemoryStream();
        destination.Write(new byte[100_000]);

        Assert.True((await document.RenderPageAsync(0, 200, 260, destination, Ct)).IsSuccess);

        var image = await Decode(destination);
        Assert.InRange(image.Width, 197, 203);
    }

    [Fact]
    public async Task WorkAtAHighResolutionOnABigPageIsRefusedBeforeTheEngineIsAsked()
    {
        var document = await Open(PdfFixtures.Build(1));
        using var destination = new MemoryStream();

        foreach (var (w, h) in new[] { (0, 100), (100, 0), (-5, 10), (40_000, 10), (20_000, 20_000) })
        {
            var result = await document.RenderPageAsync(0, w, h, destination, Ct);

            Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        }

        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public async Task AnUnusableDestinationIsRefused()
    {
        var document = await Open(PdfFixtures.Build(1));
        using var readOnly = new MemoryStream([], writable: false);

        var result = await document.RenderPageAsync(0, 100, 100, readOnly, Ct);

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        await Assert.ThrowsAsync<ArgumentNullException>(() => document.RenderPageAsync(0, 100, 100, null!, Ct));
    }

    [Fact]
    public async Task ACancelBeforeDrawingIsCancelledAndLeavesTheDestinationUntouched()
    {
        var document = await Open(PdfFixtures.Build(1));
        using var destination = new MemoryStream();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await document.RenderPageAsync(0, 612, 792, destination, cts.Token);

        Assert.True(result.IsCancelled);
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public async Task ACancelWhileDrawingReturnsOnlyOnceTheEngineHasStopped()
    {
        // A page that is slow enough to cancel in the middle of: 100 megapixels is the largest the adapter accepts.
        var document = await Open(PdfFixtures.Build(1, 2000, 2000));
        using var destination = new MemoryStream();
        using var cts = new CancellationTokenSource();

        var drawing = document.RenderPageAsync(0, 10_000, 10_000, destination, cts.Token);
        await Task.Delay(30, Ct);
        await cts.CancelAsync();
        var result = await drawing;

        // Either the engine was quick (a success) or it was cancelled; in both cases the stream can be closed and used again right away.
        Assert.True(result.IsSuccess || result.IsCancelled, result.Error?.SafeMessage);
        destination.SetLength(0);
        var again = await document.RenderPageAsync(0, 100, 100, destination, Ct);
        Assert.True(again.IsSuccess, again.Error?.SafeMessage);
    }

    [Fact]
    public async Task AHundredPagePdfOpensAndAnyPageCanBeDrawn()
    {
        var document = await Open(PdfFixtures.Build(100));
        Assert.Equal(100, document.PageCount);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        for (var index = 0; index < 100; index++)
        {
            using var destination = new MemoryStream();
            var result = await document.RenderPageAsync(index, 306, 396, destination, Ct);
            Assert.True(result.IsSuccess, $"page {index + 1}: {result.Error?.SafeMessage}");
            var image = await Decode(destination);
            AssertNear(At(image, image.Width / 2, image.Height / 4), PdfFixtures.PageColor(index), 4, $"page {index + 1}");
        }

        Assert.True(watch.Elapsed < TimeSpan.FromMinutes(2), $"100 pages took {watch.Elapsed}.");
    }

    [Fact]
    public async Task SeveralDocumentsAtOnceDoNotDisturbEachOther()
    {
        var tasks = Enumerable.Range(0, 4).Select(async i =>
        {
            var document = await Open(PdfFixtures.Build(3));
            using var destination = new MemoryStream();
            var result = await document.RenderPageAsync(i % 3, 306, 396, destination, Ct);
            Assert.True(result.IsSuccess, result.Error?.SafeMessage);
            var image = await Decode(destination);
            AssertNear(At(image, image.Width / 2, image.Height / 4), PdfFixtures.PageColor(i % 3), 4, $"document {i}");
        }).ToArray();

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task DisposingTwiceIsHarmlessAndADisposedDocumentRefusesWork()
    {
        var stream = new MemoryStream(PdfFixtures.Build(1));
        var document = (await new WindowsPdfPageRasterizer().OpenAsync(stream, null, Ct)).Value;

        await document.DisposeAsync();
        await document.DisposeAsync();

        using var destination = new MemoryStream();
        var drawn = await document.RenderPageAsync(0, 100, 100, destination, Ct);
        Assert.Equal(ErrorCode.InvalidInput, drawn.Error!.Code);
        Assert.True(document.GetPageSize(0).IsFailure);
    }

    [Fact]
    public async Task APageSizeIsReportedInPointsForUnusualPages()
    {
        var document = await Open(PdfFixtures.Build(1, 14400, 144));
        using var destination = new MemoryStream();

        var size = document.GetPageSize(0).Value;

        Assert.Equal(14400, Math.Round(size.WidthPoints));
        Assert.Equal(144, Math.Round(size.HeightPoints));
        Assert.True((await document.RenderPageAsync(0, 1440, 14, destination, Ct)).IsSuccess);
    }

    private sealed class NonSeekable(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
