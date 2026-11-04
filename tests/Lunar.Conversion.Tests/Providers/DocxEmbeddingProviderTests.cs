using System.IO.Compression;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Lunar.Application.Conversion.Models;
using Lunar.Conversion.Providers;
using Lunar.Conversion.Tests.Support;
using Lunar.Conversion.Validation;
using Lunar.Domain.Conversion;
using Lunar.Domain.Shared;
using SkiaSharp;
using Xunit;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Lunar.Conversion.Tests.Providers;

// BE-23: picture into a Word document. The package is read back with the Open XML SDK and checked against its schema; how Word or
// LibreOffice lays the page out is not checked here (no renderer; see the evidence).
public sealed class DocxEmbeddingProviderTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly DocxEmbeddingProvider _provider = new();
    private readonly ConversionLimits _limits = new();

    public void Dispose() => _folder.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string OutputPath(string name) => Path.Combine(_folder.Out, name);

    private Task<Result<PictureDocumentResult>> Create(string source, FileFormat from, string output, PageLayoutOptions? layout = null, ConversionLimits? limits = null, CancellationToken? ct = null) =>
        _provider.CreateAsync(source, from, output, layout ?? new PageLayoutOptions(), limits ?? _limits, ct ?? Ct);

    private static void AssertOk(Result<PictureDocumentResult> result) => Assert.True(result.IsSuccess, result.Error?.SafeMessage);

    private static void AssertNear(double expected, double actual, string what = "value", double tolerance = 0.5) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"{what}: expected {expected} but got {actual}.");

    private static (byte R, byte G, byte B, byte A) Px(int r, int g, int b, int a) => ((byte)r, (byte)g, (byte)b, (byte)a);

    private string PngFile(int w = 96, int h = 48, string name = "picture.png") =>
        _folder.Write(name, PictureFixtures.Rgba(w, h, (x, _) => x < w / 2 ? Px(255, 0, 0, 255) : Px(0, 0, 255, 128)));

    private static void AssertSchemaValid(string path)
    {
        using var document = WordprocessingDocument.Open(path, false);
        var errors = new OpenXmlValidator().Validate(document).Select(e => $"{e.Description} @ {e.Path?.XPath}").ToList();
        Assert.True(errors.Count == 0, "Schema errors: " + string.Join("; ", errors));
    }

    private static DW.Extent Extent(WordprocessingDocument document) => document.MainDocumentPart!.Document!.Body!.Descendants<DW.Extent>().Single();

    private static W.PageSize SectionSize(WordprocessingDocument document) => document.MainDocumentPart!.Document!.Body!.Descendants<W.PageSize>().Single();

    private static W.PageMargin SectionMargin(WordprocessingDocument document) => document.MainDocumentPart!.Document!.Body!.Descendants<W.PageMargin>().Single();

    // ---- what the file is --------------------------------------------------------------------------------------------

    [Fact]
    public async Task APictureBecomesADocumentHoldingOnlyThatPictureAndNoText()
    {
        var output = OutputPath("out.docx");

        var result = await Create(PngFile(200, 100), FileFormat.Png, output);

        AssertOk(result);
        AssertSchemaValid(output);
        using var document = WordprocessingDocument.Open(output, false);
        var body = document.MainDocumentPart!.Document!.Body!;
        Assert.Single(body.Elements<W.Paragraph>());
        Assert.Empty(body.Descendants<W.Text>());             // not one character of text: nothing was recognised
        Assert.Single(body.Descendants<W.Drawing>());
        Assert.Single(document.MainDocumentPart.ImageParts);
        Assert.Empty(document.MainDocumentPart.ExternalRelationships);
        Assert.Empty(document.MainDocumentPart.HyperlinkRelationships);
        Assert.Equal(new FileInfo(output).Length, result.Value.OutputBytes);
        Assert.Null(SourceFileValidator.Inspect(output, FileFormat.Docx, _limits).Failure); // a file the rest of Lunar accepts as a Word document
    }

    [Fact]
    public async Task ThePictureAndTheDocumentSayThereIsNoTextToEdit()
    {
        var output = OutputPath("said.docx");

        AssertOk(await Create(PngFile(), FileFormat.Png, output));

        using var document = WordprocessingDocument.Open(output, false);
        var alt = document.MainDocumentPart!.Document!.Body!.Descendants<DW.DocProperties>().Single().Description?.Value;
        Assert.Contains("no text was recognised", alt, StringComparison.Ordinal);
        Assert.Contains("no text was recognised", document.PackageProperties.Description, StringComparison.Ordinal);
        Assert.Equal("Lunar", document.PackageProperties.Creator);
    }

    [Fact]
    public async Task NothingAboutTheInputsNameOrPlaceIsWritten()
    {
        var source = _folder.Write("secret-holiday-plans.png", PictureFixtures.Rgba(32, 32, (_, _) => Px(1, 2, 3, 255)));
        var output = OutputPath("anything.docx");

        AssertOk(await Create(source, FileFormat.Png, output));

        using var zip = ZipFile.OpenRead(output);
        foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".xml", StringComparison.Ordinal) || e.FullName.EndsWith(".rels", StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(entry.Open());
            var text = reader.ReadToEnd();
            Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("holiday", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(_folder.Root, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task AnUprightPngIsEmbeddedAsItsOwnBytes()
    {
        var png = PictureFixtures.Rgba(64, 32, (x, _) => x < 32 ? Px(255, 0, 0, 255) : Px(0, 0, 255, 128));
        var source = _folder.Write("exact.png", png);
        var output = OutputPath("exact.docx");

        AssertOk(await Create(source, FileFormat.Png, output));

        using var document = WordprocessingDocument.Open(output, false);
        var part = document.MainDocumentPart!.ImageParts.Single();
        Assert.Equal("image/png", part.ContentType);
        using var stream = part.GetStream();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        Assert.Equal(png, memory.ToArray());
    }

    [Fact]
    public async Task AnUprightJpegIsEmbeddedAsItsOwnBytesWithoutRecompression()
    {
        var jpeg = PictureFixtures.SkiaJpeg(120, 80, (x, y) => ((byte)(x * 2), (byte)(y * 3), 100), quality: 90);
        var source = _folder.Write("exact.jpg", jpeg);
        var output = OutputPath("exact-jpg.docx");

        AssertOk(await Create(source, FileFormat.Jpg, output));

        using var document = WordprocessingDocument.Open(output, false);
        var part = document.MainDocumentPart!.ImageParts.Single();
        Assert.Equal("image/jpeg", part.ContentType);
        using var stream = part.GetStream();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        Assert.Equal(jpeg, memory.ToArray());
    }

    // ---- turned pictures ---------------------------------------------------------------------------------------------

    public static TheoryData<int, string> Orientations => new()
    {
        { 2, "GRYB" }, { 3, "YBGR" }, { 4, "BYRG" }, { 5, "RBGY" }, { 6, "BRYG" }, { 7, "YGBR" }, { 8, "GYRB" }
    };

    [Theory]
    [MemberData(nameof(Orientations))]
    public async Task ATurnedPictureIsStoredUprightSoEveryViewerShowsItTheSameWay(int orientation, string corners)
    {
        var source = _folder.Write($"turned{orientation}.jpg", PictureFixtures.QuadrantJpeg(64, 32, orientation));
        var output = OutputPath($"turned{orientation}.docx");

        var result = await Create(source, FileFormat.Jpg, output);

        AssertOk(result);
        AssertSchemaValid(output);
        using var document = WordprocessingDocument.Open(output, false);
        var part = document.MainDocumentPart!.ImageParts.Single();
        Assert.Equal("image/png", part.ContentType);
        using var stream = part.GetStream();
        using var bitmap = SKBitmap.Decode(stream);
        var (width, height) = orientation >= 5 ? (32, 64) : (64, 32);
        Assert.Equal((width, height), (bitmap.Width, bitmap.Height));
        Assert.Equal((width, height), (result.Value.PictureWidth, result.Value.PictureHeight));

        var colors = new Dictionary<char, (byte R, byte G, byte B)> { ['R'] = PictureFixtures.Red, ['G'] = PictureFixtures.Green, ['B'] = PictureFixtures.Blue, ['Y'] = PictureFixtures.Yellow };
        var samples = new[] { (width / 4, height / 4), (3 * width / 4, height / 4), (width / 4, 3 * height / 4), (3 * width / 4, 3 * height / 4) };
        for (var i = 0; i < 4; i++)
        {
            var c = bitmap.GetPixel(samples[i].Item1, samples[i].Item2);
            var e = colors[corners[i]];
            Assert.True(Math.Abs(c.Red - e.R) <= 14 && Math.Abs(c.Green - e.G) <= 14 && Math.Abs(c.Blue - e.B) <= 14,
                $"orientation {orientation} corner {i}: expected {corners[i]} but got ({c.Red},{c.Green},{c.Blue}).");
        }
    }

    // ---- page layout -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAutomaticPageIsThePictureAtNinetySixPixelsPerInchWithRoomForTheLine()
    {
        var output = OutputPath("auto.docx");

        var result = await Create(PngFile(400, 200), FileFormat.Png, output, new PageLayoutOptions { MarginPoints = 10 });

        AssertOk(result);
        using var document = WordprocessingDocument.Open(output, false);
        // picture 300 x 150 pt; page = picture + 2 x 10 margin, plus 4 pt below so the line does not spill
        AssertNear(320 * 20, SectionSize(document).Width!.Value, "page width twips", 1);
        AssertNear(174 * 20, SectionSize(document).Height!.Value, "page height twips", 1);
        AssertNear(300 * 12700, Extent(document).Cx!.Value, "extent cx", 100);
        AssertNear(150 * 12700, Extent(document).Cy!.Value, "extent cy", 100);
        AssertNear(10 * 20, SectionMargin(document).Left!.Value, "left margin twips", 1);
        AssertNear(14 * 20, SectionMargin(document).Bottom!.Value, "bottom margin twips", 1);
        Assert.Equal(W.PageOrientationValues.Landscape, SectionSize(document).Orient?.Value); // 320 x 174 pt is wider than tall
    }

    [Theory]
    [InlineData(PageSize.A4, 100, 200, 11906, 16838, false)]
    [InlineData(PageSize.A4, 200, 100, 16838, 11906, true)]
    [InlineData(PageSize.Letter, 100, 200, 12240, 15840, false)]
    [InlineData(PageSize.Letter, 300, 100, 15840, 12240, true)]
    public async Task AFixedPageHasTheRightSizeAndTurnsSidewaysForAWidePicture(PageSize size, int w, int h, int twipsWide, int twipsHigh, bool landscape)
    {
        var output = OutputPath($"{size}-{w}x{h}.docx");

        AssertOk(await Create(PngFile(w, h, $"p{size}{w}x{h}.png"), FileFormat.Png, output, new PageLayoutOptions { Size = size, MarginPoints = 36 }));

        AssertSchemaValid(output);
        using var document = WordprocessingDocument.Open(output, false);
        AssertNear(twipsWide, SectionSize(document).Width!.Value, "width twips", 1);
        AssertNear(twipsHigh, SectionSize(document).Height!.Value, "height twips", 1);
        Assert.Equal(landscape, SectionSize(document).Orient?.Value == W.PageOrientationValues.Landscape);
        AssertNear(720, SectionMargin(document).Left!.Value, "margin twips", 1);
    }

    [Fact]
    public async Task FitKeepsTheWholePictureInsideTheMarginsWithTheShapeIntactAndCentresItDownTheMiddle()
    {
        var output = OutputPath("fit.docx");

        var result = await Create(PngFile(100, 100), FileFormat.Png, output, new PageLayoutOptions { Size = PageSize.Letter, MarginPoints = 36, Fit = ImageFit.Fit });

        AssertOk(result);
        Assert.False(result.Value.PictureCropped);
        using var document = WordprocessingDocument.Open(output, false);
        var extent = Extent(document);
        Assert.Equal(extent.Cx!.Value, extent.Cy!.Value, 2.0); // a square stays square
        Assert.True(extent.Cx.Value <= 540 * 12700 + 100);
        Assert.True(extent.Cy.Value <= (720 - 4) * 12700 + 100);
        Assert.Empty(document.MainDocumentPart!.Document!.Body!.Descendants<DocumentFormat.OpenXml.Drawing.SourceRectangle>());
        var before = document.MainDocumentPart.Document.Body.Descendants<W.SpacingBetweenLines>().Single().Before!.Value;
        Assert.True(int.Parse(before ?? "0", System.Globalization.CultureInfo.InvariantCulture) > 0, "A fixed page centres the picture with space before it.");
    }

    [Fact]
    public async Task CoverFillsTheAreaAndCropsTheOverflowOffTheSidesOfThePicture()
    {
        var output = OutputPath("cover.docx");

        var result = await Create(PngFile(100, 100), FileFormat.Png, output, new PageLayoutOptions { Size = PageSize.Letter, MarginPoints = 36, Fit = ImageFit.Cover });

        AssertOk(result);
        Assert.True(result.Value.PictureCropped);
        AssertSchemaValid(output);
        using var document = WordprocessingDocument.Open(output, false);
        var crop = document.MainDocumentPart!.Document!.Body!.Descendants<DocumentFormat.OpenXml.Drawing.SourceRectangle>().Single();
        Assert.True(crop.Left!.Value > 0 && crop.Left.Value == crop.Right!.Value, "Left and right are cut by the same amount.");
        Assert.True((crop.Top?.Value ?? 0) == 0 && (crop.Bottom?.Value ?? 0) == 0, "A square covering a tall box is cut at the sides only.");
        AssertNear(540 * 12700, Extent(document).Cx!.Value, "shown width", 100);
        AssertNear((720 - 4) * 12700, Extent(document).Cy!.Value, "shown height", 100);
    }

    [Fact]
    public async Task ThePageNeverExceedsWhatWordAllows()
    {
        var output = OutputPath("huge.docx");
        var source = _folder.Write("huge.png", PictureFixtures.Rgb(4000, 3000, (_, _) => (10, 20, 30)));

        AssertOk(await Create(source, FileFormat.Png, output));

        using var document = WordprocessingDocument.Open(output, false);
        Assert.True(SectionSize(document).Width!.Value <= 1584 * 20 + 1);
        Assert.True(SectionSize(document).Height!.Value <= (1584 + 4) * 20 + 1);
    }

    // ---- refusals ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(FileFormat.Pdf)]
    [InlineData(FileFormat.Docx)]
    [InlineData((FileFormat)99)]
    public async Task OnlyPicturesAreAcceptedAsInput(FileFormat from)
    {
        var output = OutputPath("never.docx");

        var result = await Create(PngFile(), from, output);

        Assert.Equal(ErrorCode.Unsupported, result.Error!.Code);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task AnImpossibleMarginIsRefusedAndNothingIsWritten()
    {
        var output = OutputPath("margin.docx");

        var result = await Create(PngFile(), FileFormat.Png, output, new PageLayoutOptions { MarginPoints = -3 });

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task APictureOverThePixelLimitIsRefusedBeforeItIsDecoded()
    {
        var bomb = _folder.Write("bomb.png", Fixtures.Png(65_535, 65_535));
        var output = OutputPath("bomb.docx");

        var result = await Create(bomb, FileFormat.Png, output);

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.Contains("megapixels", result.Error.SafeMessage, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task APictureSwappedForAProgramAfterPlanningIsRefused()
    {
        var source = PngFile();
        File.WriteAllBytes(source, [0x4D, 0x5A, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]);

        var result = await Create(source, FileFormat.Png, OutputPath("swapped.docx"));

        Assert.True(result.IsFailure);
        Assert.False(File.Exists(OutputPath("swapped.docx")));
    }

    [Fact]
    public async Task AnExistingOutputIsNeverReplaced()
    {
        var output = _folder.Write("taken.docx", [7, 7, 7], _folder.Out);

        var result = await Create(PngFile(), FileFormat.Png, output);

        Assert.Equal(ErrorCode.IoFailure, result.Error!.Code);
        Assert.Equal([7, 7, 7], File.ReadAllBytes(output));
    }

    [Fact]
    public async Task AFolderThatIsNotThereIsAnOutputError()
    {
        var output = Path.Combine(_folder.Root, "nowhere", "a.docx");

        var result = await Create(PngFile(), FileFormat.Png, output);

        Assert.Equal(ErrorCode.IoFailure, result.Error!.Code);
        Assert.False(Directory.Exists(Path.Combine(_folder.Root, "nowhere")));
    }

    [Fact]
    public async Task ACancelBeforeStartReturnsCancelledAndWritesNothing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var output = OutputPath("cancelled.docx");

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

        var ok = await Create(source, FileFormat.Png, OutputPath("secret-ok.docx"));
        var again = await Create(source, FileFormat.Png, OutputPath("secret-ok.docx"));
        var tooLong = await Create(source, FileFormat.Png, OutputPath("secret-no.docx"), limits: _limits with { MaxPathChars = 40 });

        AssertOk(ok);
        Assert.Equal(bytes, File.ReadAllBytes(source));
        Assert.Equal(time, File.GetLastWriteTimeUtc(source));
        foreach (var failure in new[] { again, tooLong })
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
        var output = OutputPath("результат-結果-" + new string('y', 100) + ".docx");

        AssertOk(await Create(source, FileFormat.Png, output));

        AssertSchemaValid(output);
    }

    [Fact]
    public async Task ManyAtOnceAllProduceValidIndependentDocuments()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Create(PngFile(40 + i, 30, $"m{i}.png"), FileFormat.Png, OutputPath($"m{i}.docx"))));

        Assert.All(results, AssertOk);
        for (var i = 0; i < 6; i++)
            AssertSchemaValid(OutputPath($"m{i}.docx"));
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
