using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Lunar.Application.Conversion.Models;
using Lunar.Conversion.Providers;
using Lunar.Conversion.Tests.Support;
using Lunar.Conversion.Validation;
using Lunar.Domain.Conversion;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Conversion.Tests.Providers;

// BE-23: picture into a PDF page. The structure of the PDF is read directly; how a real viewer draws it is checked separately (manual
// Windows evidence), because these tests have no PDF renderer.
public sealed partial class PdfCreationProviderTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly PdfCreationProvider _provider = new();
    private readonly ConversionLimits _limits = new();

    public void Dispose() => _folder.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string OutputPath(string name) => Path.Combine(_folder.Out, name);

    private Task<Result<PictureDocumentResult>> Create(string source, FileFormat from, string output, PageLayoutOptions? layout = null, ConversionLimits? limits = null, CancellationToken? ct = null) =>
        _provider.CreateAsync(source, from, output, layout ?? new PageLayoutOptions(), limits ?? _limits, ct ?? Ct);

    private static string Text(string path) => Encoding.Latin1.GetString(File.ReadAllBytes(path));

    private static (double Width, double Height) MediaBox(string pdfText)
    {
        var m = MediaBoxPattern().Match(pdfText);
        Assert.True(m.Success, "No MediaBox found.");
        return (double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(@"/MediaBox\s*\[\s*0\s+0\s+([0-9.]+)\s+([0-9.]+)\s*\]")]
    private static partial Regex MediaBoxPattern();

    [GeneratedRegex(@"/Type\s*/Page(?![A-Za-z])")]
    private static partial Regex PagePattern();

    // Skia writes the page box in whole points, so a size is right to within half a point.
    private static void AssertNear(double expected, double actual, string what = "value", double tolerance = 0.6) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"{what}: expected {expected} but got {actual}.");

    private static void AssertOk(Result<PictureDocumentResult> result) => Assert.True(result.IsSuccess, result.Error?.SafeMessage);

    private string PngFile(int w = 96, int h = 48, string name = "picture.png") =>
        _folder.Write(name, PictureFixtures.Rgba(w, h, (x, _) => x < w / 2 ? Px(255, 0, 0, 255) : Px(0, 0, 255, 128)));

    private static (byte R, byte G, byte B, byte A) Px(int r, int g, int b, int a) => ((byte)r, (byte)g, (byte)b, (byte)a);

    // ---- what the file is --------------------------------------------------------------------------------------------

    [Fact]
    public async Task APictureBecomesOnePageHoldingOnePictureAndNoText()
    {
        var source = _folder.Write("opaque.png", PictureFixtures.Rgb(200, 100, (x, _) => ((byte)x, 40, 90)));
        var output = OutputPath("out.pdf");

        var result = await Create(source, FileFormat.Png, output);

        AssertOk(result);
        var text = Text(output);
        Assert.StartsWith("%PDF-", text, StringComparison.Ordinal);
        Assert.Single(PagePattern().Matches(text));
        Assert.Single(Regex.Matches(text, @"/Subtype\s*/Image"));
        Assert.DoesNotContain("/Type /Font", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/Type/Font", text, StringComparison.Ordinal);
        Assert.Null(SourceFileValidator.Inspect(output, FileFormat.Pdf, _limits).Failure); // a file the rest of Lunar accepts as a PDF
        Assert.Equal(new FileInfo(output).Length, result.Value.OutputBytes);
    }

    [Fact]
    public async Task TheDocumentSaysItHoldsOnlyAPictureAndNoRecognisedText()
    {
        var output = OutputPath("said.pdf");

        AssertOk(await Create(PngFile(), FileFormat.Png, output));

        var text = Text(output);
        Assert.Contains("Lunar", text, StringComparison.Ordinal);
        Assert.Contains("no text was recognised", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingAboutTheInputsNameOrPlaceIsWritten()
    {
        var source = _folder.Write("secret-holiday-plans.png", PictureFixtures.Rgba(32, 32, (_, _) => Px(1, 2, 3, 255)));
        var output = OutputPath("anything.pdf");

        AssertOk(await Create(source, FileFormat.Png, output));

        var text = Text(output);
        Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("holiday", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_folder.Root, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AJpegIsPassedThroughWithoutASecondCompression()
    {
        var jpeg = PictureFixtures.SkiaJpeg(120, 80, (x, y) => ((byte)(x * 2), (byte)(y * 3), 100), quality: 90);
        var source = _folder.Write("photo.jpg", jpeg);
        var output = OutputPath("photo.pdf");

        AssertOk(await Create(source, FileFormat.Jpg, output));

        var pdf = File.ReadAllBytes(output);
        Assert.Contains("/DCTDecode", Encoding.Latin1.GetString(pdf), StringComparison.Ordinal);
        Assert.True(pdf.AsSpan().IndexOf(jpeg) >= 0, "The JPEG's own bytes are not in the PDF as they are.");
    }

    [Fact]
    public async Task APngIsStoredLosslesslyWithItsTransparency()
    {
        var output = OutputPath("alpha.pdf");

        AssertOk(await Create(PngFile(), FileFormat.Png, output));

        var text = Text(output);
        Assert.Contains("/FlateDecode", text, StringComparison.Ordinal);
        Assert.Contains("/SMask", text, StringComparison.Ordinal); // the alpha channel, kept (its own small image)
        Assert.DoesNotContain("/DCTDecode", text, StringComparison.Ordinal);
    }

    // ---- page layout -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAutomaticPageIsThePictureAtNinetySixPixelsPerInch()
    {
        var output = OutputPath("auto.pdf");

        var result = await Create(PngFile(400, 200), FileFormat.Png, output);

        AssertOk(result);
        var (w, h) = MediaBox(Text(output));
        AssertNear(300, w, "page width");
        AssertNear(150, h, "page height");
        AssertNear(300, result.Value.PageWidthPoints);
        Assert.Equal((400, 200), (result.Value.PictureWidth, result.Value.PictureHeight));
        Assert.False(result.Value.PictureCropped);
    }

    [Theory]
    [InlineData(PageSize.A4, 100, 200, 595.28, 841.89)]
    [InlineData(PageSize.A4, 200, 100, 841.89, 595.28)]
    [InlineData(PageSize.Letter, 100, 200, 612, 792)]
    [InlineData(PageSize.Letter, 300, 100, 792, 612)]
    public async Task AFixedPageTurnsSidewaysForAWidePicture(PageSize size, int w, int h, double pageWidth, double pageHeight)
    {
        var output = OutputPath($"{size}-{w}x{h}.pdf");

        AssertOk(await Create(PngFile(w, h, $"p{size}{w}x{h}.png"), FileFormat.Png, output, new PageLayoutOptions { Size = size, MarginPoints = 18 }));

        var (width, height) = MediaBox(Text(output));
        AssertNear(pageWidth, width, "page width");
        AssertNear(pageHeight, height, "page height");
    }

    [Fact]
    public async Task CoverReportsThatThePictureIsCutAndFitDoesNot()
    {
        var source = PngFile(100, 100);

        var cover = await Create(source, FileFormat.Png, OutputPath("cover.pdf"), new PageLayoutOptions { Size = PageSize.Letter, MarginPoints = 36, Fit = ImageFit.Cover });
        var fit = await Create(source, FileFormat.Png, OutputPath("fit.pdf"), new PageLayoutOptions { Size = PageSize.Letter, MarginPoints = 36, Fit = ImageFit.Fit });

        AssertOk(cover);
        AssertOk(fit);
        Assert.True(cover.Value.PictureCropped);
        Assert.False(fit.Value.PictureCropped);
    }

    [Fact]
    public async Task ThePageOfARotatedPictureFollowsItsUprightShape()
    {
        // stored 64 x 32 with the tag "turn 90 degrees": seen as 32 x 64
        var jpeg = PictureFixtures.QuadrantJpeg(64, 32, orientation: 6);
        var source = _folder.Write("turned.jpg", jpeg);
        var output = OutputPath("turned.pdf");

        var result = await Create(source, FileFormat.Jpg, output);

        AssertOk(result);
        Assert.Equal((32, 64), (result.Value.PictureWidth, result.Value.PictureHeight));
        var (w, h) = MediaBox(Text(output));
        AssertNear(0.5, w / h, "page shape", 0.01);
        Assert.Null(SourceFileValidator.Inspect(output, FileFormat.Pdf, _limits).Failure);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task EveryOrientationMakesAValidSinglePagePdf(int orientation)
    {
        var source = _folder.Write($"o{orientation}.jpg", PictureFixtures.QuadrantJpeg(64, 32, orientation));
        var output = OutputPath($"o{orientation}.pdf");

        var result = await Create(source, FileFormat.Jpg, output, new PageLayoutOptions { Size = PageSize.A4, MarginPoints = 20 });

        AssertOk(result);
        Assert.Single(PagePattern().Matches(Text(output)));
        Assert.Equal(orientation is >= 5 ? (32, 64) : (64, 32), (result.Value.PictureWidth, result.Value.PictureHeight));
    }

    // ---- refusals ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(FileFormat.Pdf)]
    [InlineData(FileFormat.Docx)]
    [InlineData((FileFormat)99)]
    public async Task OnlyPicturesAreAcceptedAsInput(FileFormat from)
    {
        var output = OutputPath("never.pdf");

        var result = await Create(PngFile(), from, output);

        Assert.Equal(ErrorCode.Unsupported, result.Error!.Code);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task AnImpossibleMarginIsRefusedAndNothingIsWritten()
    {
        var output = OutputPath("margin.pdf");

        var result = await Create(PngFile(), FileFormat.Png, output, new PageLayoutOptions { MarginPoints = 999 });

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.Contains("margin", result.Error.SafeMessage, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task APictureOverThePixelLimitIsRefusedBeforeItIsDecoded()
    {
        var bomb = _folder.Write("bomb.png", Fixtures.Png(65_535, 65_535));
        var output = OutputPath("bomb.pdf");

        var result = await Create(bomb, FileFormat.Png, output);

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.Contains("megapixels", result.Error.SafeMessage, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task APngWithDamagedDataFailsCleanlyAndLeavesNoFile()
    {
        var source = _folder.Write("damaged.png", Fixtures.Png());
        var output = OutputPath("damaged.pdf");

        var result = await Create(source, FileFormat.Png, output);

        // Structure is right, data is not: whichever stage notices, there is a typed failure and no file, or a complete valid PDF.
        if (result.IsFailure)
            Assert.False(File.Exists(output));
        else
            Assert.Null(SourceFileValidator.Inspect(output, FileFormat.Pdf, _limits).Failure);
    }

    [Fact]
    public async Task APictureSwappedForAProgramAfterPlanningIsRefused()
    {
        var source = PngFile();
        File.WriteAllBytes(source, [0x4D, 0x5A, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]);

        var result = await Create(source, FileFormat.Png, OutputPath("swapped.pdf"));

        Assert.True(result.IsFailure);
        Assert.False(File.Exists(OutputPath("swapped.pdf")));
    }

    [Fact]
    public async Task AnExistingOutputIsNeverReplaced()
    {
        var output = _folder.Write("taken.pdf", [7, 7, 7], _folder.Out);

        var result = await Create(PngFile(), FileFormat.Png, output);

        Assert.Equal(ErrorCode.IoFailure, result.Error!.Code);
        Assert.Equal([7, 7, 7], File.ReadAllBytes(output));
    }

    [Fact]
    public async Task AFolderThatIsNotThereIsAnOutputError()
    {
        var output = Path.Combine(_folder.Root, "nowhere", "a.pdf");

        var result = await Create(PngFile(), FileFormat.Png, output);

        Assert.Equal(ErrorCode.IoFailure, result.Error!.Code);
        Assert.False(Directory.Exists(Path.Combine(_folder.Root, "nowhere")));
    }

    [Fact]
    public async Task ACancelBeforeStartReturnsCancelledAndWritesNothing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var output = OutputPath("cancelled.pdf");

        var result = await Create(PngFile(), FileFormat.Png, output, ct: cts.Token);

        Assert.True(result.IsCancelled);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task TheInputIsNeverChangedAndNoMessageNamesAPath()
    {
        var source = _folder.Write("secret-input.png", PictureFixtures.Rgba(32, 32, (_, _) => Px(9, 9, 9, 255)));
        var bytes = File.ReadAllBytes(source);
        var time = File.GetLastWriteTimeUtc(source);

        var ok = await Create(source, FileFormat.Png, OutputPath("secret-ok.pdf"));
        var again = await Create(source, FileFormat.Png, OutputPath("secret-ok.pdf"));
        var wrong = await Create(source, FileFormat.Png, OutputPath("secret-no.docx"), limits: _limits with { MaxPathChars = 40 });

        AssertOk(ok);
        Assert.Equal(bytes, File.ReadAllBytes(source));
        Assert.Equal(time, File.GetLastWriteTimeUtc(source));
        foreach (var failure in new[] { again, wrong })
        {
            Assert.True(failure.IsFailure);
            Assert.DoesNotContain("secret", failure.Error!.SafeMessage, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@":\", failure.Error.SafeMessage, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task UnicodeAndLongNamesWork()
    {
        var source = _folder.Write("фото-ürün-写真-😀-" + new string('x', 100) + ".png", PictureFixtures.Rgba(32, 32, (_, _) => Px(9, 9, 9, 255)));
        var output = OutputPath("результат-結果-" + new string('y', 100) + ".pdf");

        AssertOk(await Create(source, FileFormat.Png, output));

        Assert.True(File.Exists(output));
    }

    [Fact]
    public async Task ALargePictureAndManyAtOnceAllWork()
    {
        var large = _folder.Write("large.png", PictureFixtures.Rgb(3000, 2000, (x, y) => ((byte)(x / 12), (byte)(y / 8), 90)));
        var big = await Create(large, FileFormat.Png, OutputPath("large.pdf"), new PageLayoutOptions { Size = PageSize.A4, MarginPoints = 36 });
        AssertOk(big);

        var many = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Create(PngFile(40 + i, 30, $"m{i}.png"), FileFormat.Png, OutputPath($"m{i}.pdf"))));
        Assert.All(many, AssertOk);
        Assert.Equal(6, Directory.GetFiles(_folder.Out, "m*.pdf").Length);
    }

    [Fact]
    public async Task TheArgumentsAreRequired()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _provider.CreateAsync(null!, FileFormat.Png, "x", new PageLayoutOptions(), _limits, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _provider.CreateAsync("x", FileFormat.Png, null!, new PageLayoutOptions(), _limits, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _provider.CreateAsync("x", FileFormat.Png, "y", null!, _limits, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _provider.CreateAsync("x", FileFormat.Png, "y", new PageLayoutOptions(), null!, Ct));
    }
}
