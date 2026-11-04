using Lunar.Application.Conversion.Models;
using Lunar.Conversion.Providers;
using Lunar.Conversion.Tests.Support;
using Lunar.Conversion.Validation;
using Lunar.Domain.Conversion;
using Lunar.Domain.Shared;
using SkiaSharp;
using Xunit;

namespace Lunar.Conversion.Tests.Providers;

// BE-22: picture-to-picture conversion against pictures whose pixels are known. JPEG is lossy, so colours are compared within a
// tolerance; PNG results are compared exactly.
public sealed class ImageConversionProviderTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly ImageConversionProvider _provider = new();
    private readonly ConversionLimits _limits = new();

    public void Dispose() => _folder.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string OutputPath(string name) => Path.Combine(_folder.Out, name);

    private Task<Result<ImageConversionResult>> Convert(
        string source, FileFormat from, string output, FileFormat to, ImageEncodingOptions? options = null, ConversionLimits? limits = null, CancellationToken? ct = null) =>
        _provider.ConvertAsync(source, from, output, to, options ?? new ImageEncodingOptions(), limits ?? _limits, ct ?? Ct);

    private static void AssertNear(SKColor actual, (byte R, byte G, byte B) expected, int tolerance, string what = "pixel")
    {
        Assert.True(
            Math.Abs(actual.Red - expected.R) <= tolerance && Math.Abs(actual.Green - expected.G) <= tolerance && Math.Abs(actual.Blue - expected.B) <= tolerance,
            $"{what}: expected about ({expected.R},{expected.G},{expected.B}) but got ({actual.Red},{actual.Green},{actual.Blue}).");
    }

    private static void AssertOk(Result<ImageConversionResult> result) => Assert.True(result.IsSuccess, result.Error?.SafeMessage);

    // ---- transparency ------------------------------------------------------------------------------------------------

    // 96 x 32: left third opaque red, middle third fully transparent, right third 50% blue.
    private static byte[] ThreeBands() => PictureFixtures.Rgba(96, 32, (x, _) => x < 32 ? Px(255, 0, 0, 255) : x < 64 ? Px(0, 0, 0, 0) : Px(0, 0, 255, 128));

    private static (byte R, byte G, byte B, byte A) Px(int r, int g, int b, int a) => ((byte)r, (byte)g, (byte)b, (byte)a);

    [Fact]
    public async Task TransparencyIsPaintedOverWhiteByDefault()
    {
        var source = _folder.Write("bands.png", ThreeBands());
        var output = OutputPath("bands.jpg");

        var result = await Convert(source, FileFormat.Png, output, FileFormat.Jpg, new ImageEncodingOptions { JpegQuality = 100 });

        AssertOk(result);
        Assert.True(result.Value.SourceHadTransparency);
        Assert.False(result.Value.OrientationApplied);
        Assert.Equal((96, 32), (result.Value.Width, result.Value.Height));
        AssertNear(PictureFixtures.PixelAt(output, 16, 16), (255, 0, 0), 12, "opaque red");
        AssertNear(PictureFixtures.PixelAt(output, 48, 16), (255, 255, 255), 6, "fully transparent");
        AssertNear(PictureFixtures.PixelAt(output, 80, 16), (127, 127, 255), 12, "half transparent blue over white");
    }

    [Fact]
    public async Task TransparencyIsPaintedOverTheChosenBackground()
    {
        var source = _folder.Write("bands.png", ThreeBands());
        var output = OutputPath("bands-green.jpg");
        var options = new ImageEncodingOptions { JpegQuality = 100, Background = new RgbColor(0, 128, 0) };

        AssertOk(await Convert(source, FileFormat.Png, output, FileFormat.Jpeg, options));

        AssertNear(PictureFixtures.PixelAt(output, 16, 16), (255, 0, 0), 12, "opaque red stays red");
        AssertNear(PictureFixtures.PixelAt(output, 48, 16), (0, 128, 0), 8, "fully transparent shows the background");
        AssertNear(PictureFixtures.PixelAt(output, 80, 16), (0, 64, 127), 14, "half transparent blue over green");
    }

    [Fact]
    public async Task AnOpaquePngIsNotFlattenedAndKeepsItsColours()
    {
        var source = _folder.Write("opaque.png", PictureFixtures.Rgb(64, 32, (x, y) => PictureFixtures.QuadrantColor(x, y, 64, 32)));
        var output = OutputPath("opaque.jpg");

        var result = await Convert(source, FileFormat.Png, output, FileFormat.Jpg, new ImageEncodingOptions { JpegQuality = 100 });

        AssertOk(result);
        Assert.False(result.Value.SourceHadTransparency);
        AssertNear(PictureFixtures.PixelAt(output, 16, 8), PictureFixtures.Red, 12, "top left");
        AssertNear(PictureFixtures.PixelAt(output, 48, 8), PictureFixtures.Green, 12, "top right");
        AssertNear(PictureFixtures.PixelAt(output, 16, 24), PictureFixtures.Blue, 12, "bottom left");
        AssertNear(PictureFixtures.PixelAt(output, 48, 24), PictureFixtures.Yellow, 12, "bottom right");
    }

    [Fact]
    public async Task APalettePngWithATransparentEntryShowsTheBackgroundThere()
    {
        // two palette entries: 0 transparent, 1 opaque red; left half index 0, right half index 1
        byte[] palette = [0, 0, 0, 255, 0, 0];
        byte[] transparency = [0];
        var png = PictureFixtures.Png(64, 32, colorType: 3, bitDepth: 8, row: _ => [.. Enumerable.Repeat((byte)0, 32), .. Enumerable.Repeat((byte)1, 32)], palette, transparency);
        var source = _folder.Write("palette.png", png);
        var output = OutputPath("palette.jpg");

        AssertOk(await Convert(source, FileFormat.Png, output, FileFormat.Jpg, new ImageEncodingOptions { JpegQuality = 100 }));

        AssertNear(PictureFixtures.PixelAt(output, 16, 16), (255, 255, 255), 6, "transparent palette entry");
        AssertNear(PictureFixtures.PixelAt(output, 48, 16), (255, 0, 0), 12, "opaque palette entry");
    }

    [Fact]
    public async Task AGreyPngWithAlphaIsFlattenedToo()
    {
        // colour type 4: grey + alpha. left: grey 128 opaque, right: grey 0 fully transparent
        var png = PictureFixtures.Png(64, 32, colorType: 4, bitDepth: 8, row: _ => [.. Grey(32, 128, 255), .. Grey(32, 0, 0)]);
        var source = _folder.Write("grey-alpha.png", png);
        var output = OutputPath("grey-alpha.jpg");

        AssertOk(await Convert(source, FileFormat.Png, output, FileFormat.Jpg, new ImageEncodingOptions { JpegQuality = 100 }));

        AssertNear(PictureFixtures.PixelAt(output, 16, 16), (128, 128, 128), 8, "opaque grey");
        AssertNear(PictureFixtures.PixelAt(output, 48, 16), (255, 255, 255), 6, "transparent grey");
    }

    private static byte[] Grey(int count, byte grey, byte alpha) => [.. Enumerable.Range(0, count).SelectMany(_ => new[] { grey, alpha })];

    [Fact]
    public async Task ASixteenBitPngIsConvertedAtEightBitsPerChannel()
    {
        // colour type 2, 16 bits: left half pure red (0xFFFF,0,0), right half pure blue
        var png = PictureFixtures.Png(64, 32, colorType: 2, bitDepth: 16, row: _ =>
        [
            .. Enumerable.Range(0, 32).SelectMany(_ => new byte[] { 0xFF, 0xFF, 0, 0, 0, 0 }),
            .. Enumerable.Range(0, 32).SelectMany(_ => new byte[] { 0, 0, 0, 0, 0xFF, 0xFF })
        ]);
        var source = _folder.Write("deep.png", png);
        var output = OutputPath("deep.jpg");

        AssertOk(await Convert(source, FileFormat.Png, output, FileFormat.Jpg, new ImageEncodingOptions { JpegQuality = 100 }));

        AssertNear(PictureFixtures.PixelAt(output, 16, 16), PictureFixtures.Red, 12, "16-bit red");
        AssertNear(PictureFixtures.PixelAt(output, 48, 16), PictureFixtures.Blue, 12, "16-bit blue");
    }

    [Fact]
    public async Task AOneBitGreyPngConvertsBlackAndWhite()
    {
        // 64 pixels wide = 8 bytes per row: left four bytes white (1 bits), right four bytes black
        var png = PictureFixtures.Png(64, 32, colorType: 0, bitDepth: 1, row: _ => [0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0]);
        var source = _folder.Write("bits.png", png);
        var output = OutputPath("bits.jpg");

        AssertOk(await Convert(source, FileFormat.Png, output, FileFormat.Jpg, new ImageEncodingOptions { JpegQuality = 100 }));

        AssertNear(PictureFixtures.PixelAt(output, 16, 16), (255, 255, 255), 6, "white bits");
        AssertNear(PictureFixtures.PixelAt(output, 48, 16), (0, 0, 0), 6, "black bits");
    }

    // ---- JPEG to PNG -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AJpegBecomesAPngWithExactlyTheDecodedPixels()
    {
        var jpeg = PictureFixtures.SkiaJpeg(80, 48, (x, y) => ((byte)(x * 3), (byte)(y * 5), (byte)((x + y) * 2)), quality: 80);
        var source = _folder.Write("gradient.jpg", jpeg);
        var output = OutputPath("gradient.png");

        var result = await Convert(source, FileFormat.Jpg, output, FileFormat.Png);

        AssertOk(result);
        Assert.False(result.Value.SourceHadTransparency);
        using var reference = SKBitmap.Decode(source);
        using var produced = SKBitmap.Decode(output);
        Assert.Equal((reference.Width, reference.Height), (produced.Width, produced.Height));
        var mismatches = 0;
        for (var y = 0; y < reference.Height; y++)
        {
            for (var x = 0; x < reference.Width; x++)
            {
                if (reference.GetPixel(x, y) != produced.GetPixel(x, y))
                    mismatches++;
            }
        }

        Assert.Equal(0, mismatches);
        Assert.Null(SourceFileValidator.Inspect(output, FileFormat.Png, _limits).Failure);
    }

    // ---- orientation -------------------------------------------------------------------------------------------------

    // The corners of the picture as it should be shown for each EXIF orientation (top left, top right, bottom left, bottom right),
    // given a source whose raw corners are red, green, blue, yellow.
    public static TheoryData<int, string, bool> Orientations => new()
    {
        { 1, "RGBY", false },
        { 2, "GRYB", false },
        { 3, "YBGR", false },
        { 4, "BYRG", false },
        { 5, "RBGY", true },
        { 6, "BRYG", true },
        { 7, "YGBR", true },
        { 8, "GYRB", true }
    };

    [Theory]
    [MemberData(nameof(Orientations))]
    public async Task ThePicturesIsTurnedByItsExifOrientationSoTheResultNeedsNoTag(int orientation, string corners, bool swapsSides)
    {
        var source = _folder.Write($"orient-{orientation}.jpg", PictureFixtures.QuadrantJpeg(64, 32, orientation));
        var output = OutputPath($"orient-{orientation}.png");

        var result = await Convert(source, FileFormat.Jpg, output, FileFormat.Png);

        AssertOk(result);
        var (width, height) = swapsSides ? (32, 64) : (64, 32);
        Assert.Equal((width, height), (result.Value.Width, result.Value.Height));
        Assert.Equal(orientation != 1, result.Value.OrientationApplied);
        using var codec = SKCodec.Create(output);
        Assert.Equal(SKEncodedOrigin.TopLeft, codec.EncodedOrigin); // the result carries no rotation tag

        var colors = new Dictionary<char, (byte R, byte G, byte B)> { ['R'] = PictureFixtures.Red, ['G'] = PictureFixtures.Green, ['B'] = PictureFixtures.Blue, ['Y'] = PictureFixtures.Yellow };
        var samples = new[] { (width / 4, height / 4), (3 * width / 4, height / 4), (width / 4, 3 * height / 4), (3 * width / 4, 3 * height / 4) };
        var names = new[] { "top left", "top right", "bottom left", "bottom right" };
        for (var i = 0; i < 4; i++)
            AssertNear(PictureFixtures.PixelAt(output, samples[i].Item1, samples[i].Item2), colors[corners[i]], 14, $"orientation {orientation} {names[i]}");
    }

    [Fact]
    public async Task APictureWithoutAnOrientationTagIsNotTurned()
    {
        var source = _folder.Write("plain.jpg", PictureFixtures.QuadrantJpeg(64, 32, orientation: 0));
        var output = OutputPath("plain.png");

        var result = await Convert(source, FileFormat.Jpg, output, FileFormat.Png);

        AssertOk(result);
        Assert.False(result.Value.OrientationApplied);
        Assert.Equal((64, 32), (result.Value.Width, result.Value.Height));
        AssertNear(PictureFixtures.PixelAt(output, 16, 8), PictureFixtures.Red, 14);
    }

    // ---- quality and names -------------------------------------------------------------------------------------------

    [Fact]
    public async Task LowerQualityGivesASmallerFile()
    {
        var noise = new Random(11);
        var pixels = new (byte, byte, byte)[128 * 128];
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = ((byte)noise.Next(256), (byte)noise.Next(256), (byte)noise.Next(256));
        var source = _folder.Write("noise.png", PictureFixtures.Rgb(128, 128, (x, y) => pixels[(y * 128) + x]));

        var low = await Convert(source, FileFormat.Png, OutputPath("noise-low.jpg"), FileFormat.Jpg, new ImageEncodingOptions { JpegQuality = 20 });
        var high = await Convert(source, FileFormat.Png, OutputPath("noise-high.jpg"), FileFormat.Jpg, new ImageEncodingOptions { JpegQuality = 95 });

        AssertOk(low);
        AssertOk(high);
        Assert.True(low.Value.OutputBytes < high.Value.OutputBytes, $"{low.Value.OutputBytes} bytes at quality 20, {high.Value.OutputBytes} at 95.");
        Assert.Equal(new FileInfo(OutputPath("noise-low.jpg")).Length, low.Value.OutputBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(-5)]
    public async Task AnImpossibleQualityIsRefusedAndNothingIsWritten(int quality)
    {
        var source = Png();
        var output = OutputPath("never.jpg");

        var result = await Convert(source, FileFormat.Png, output, FileFormat.Jpg, new ImageEncodingOptions { JpegQuality = quality });

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void TheDefaultsAreQualityNinetyOnWhite()
    {
        var options = new ImageEncodingOptions();

        Assert.Equal(90, options.JpegQuality);
        Assert.Equal(RgbColor.White, options.Background);
        Assert.Equal("#FFFFFF", RgbColor.White.ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageEncodingOptions { JpegQuality = 0 }.Validated());
        Assert.Same(options, options.Validated());
    }

    [Fact]
    public async Task UnicodeAndLongFileNamesWork()
    {
        var name = "фото-ürün-写真-😀-" + new string('x', 120);
        var source = _folder.Write(name + ".png", ThreeBands());
        var output = OutputPath("результат-結果-" + new string('y', 120) + ".jpg");

        AssertOk(await Convert(source, FileFormat.Png, output, FileFormat.Jpg));

        Assert.True(File.Exists(output));
        Assert.Null(SourceFileValidator.Inspect(output, FileFormat.Jpg, _limits).Failure);
    }

    // ---- refusals ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(FileFormat.Png, FileFormat.Png)]
    [InlineData(FileFormat.Jpg, FileFormat.Jpeg)]
    [InlineData(FileFormat.Png, FileFormat.Pdf)]
    [InlineData(FileFormat.Png, FileFormat.Docx)]
    [InlineData(FileFormat.Pdf, FileFormat.Png)]
    [InlineData(FileFormat.Docx, FileFormat.Jpg)]
    public async Task OnlyTheRoutesThatChangeThePictureEncodingAreDoneHere(FileFormat from, FileFormat to)
    {
        var source = Png();
        var output = OutputPath("never.bin");

        var result = await Convert(source, from, output, to);

        Assert.Equal(ErrorCode.Unsupported, result.Error!.Code);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task AnUnknownFormatIsRefused()
    {
        var result = await Convert(Png(), (FileFormat)99, OutputPath("x.jpg"), FileFormat.Jpg);

        Assert.Equal(ErrorCode.Unsupported, result.Error!.Code);
    }

    [Fact]
    public async Task APictureOverThePixelLimitIsRefusedBeforeItIsDecoded()
    {
        var bomb = _folder.Write("bomb.png", Fixtures.Png(65_535, 65_535));
        var output = OutputPath("bomb.jpg");

        var result = await Convert(bomb, FileFormat.Png, output, FileFormat.Jpg);

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.Contains("megapixels", result.Error.SafeMessage, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task ThePixelLimitIsOnWidthTimesHeightAndConfigurable()
    {
        var source = _folder.Write("ten.png", PictureFixtures.Rgb(10, 10, (_, _) => (1, 2, 3)));
        var limits = new ConversionLimits { MaxImagePixels = 99 };

        Assert.True((await Convert(source, FileFormat.Png, OutputPath("a.jpg"), FileFormat.Jpg, limits: limits)).IsFailure);
        Assert.True((await Convert(source, FileFormat.Png, OutputPath("b.jpg"), FileFormat.Jpg, limits: limits with { MaxImagePixels = 100 })).IsSuccess);
    }

    [Fact]
    public async Task APngWhoseDataIsDamagedFailsCleanlyAndLeavesNoOutput()
    {
        // Structure and checksums are right, the compressed picture data is not.
        var source = _folder.Write("damaged.png", Fixtures.Png());
        var output = OutputPath("damaged.jpg");

        var result = await Convert(source, FileFormat.Png, output, FileFormat.Jpg);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.Contains("picture", result.Error.SafeMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task AJpegCutShortInsideItsDataButEndedWithAnEndMarkerIsEitherRefusedOrConvertedWholeNeverHalfWritten()
    {
        // The decoder (libjpeg) treats an early end marker as the end of the data and fills the rest; whichever way it goes, the
        // result must be a complete, valid picture of the same size, or no file at all.
        var full = PictureFixtures.SkiaJpeg(160, 120, (x, y) => ((byte)(x + y), (byte)(x * 2), (byte)(y * 2)), quality: 90);
        var cut = new byte[(full.Length * 6 / 10) + 2];
        Array.Copy(full, cut, cut.Length - 2);
        cut[^2] = 0xFF;
        cut[^1] = 0xD9;
        var source = _folder.Write("cut.jpg", cut);
        var output = OutputPath("cut.png");

        var result = await Convert(source, FileFormat.Jpg, output, FileFormat.Png);

        if (result.IsFailure)
        {
            Assert.False(File.Exists(output));
        }
        else
        {
            Assert.Equal((160, 120), (result.Value.Width, result.Value.Height));
            Assert.Null(SourceFileValidator.Inspect(output, FileFormat.Png, _limits).Failure);
        }
    }

    [Fact]
    public async Task AJpegCutShortWithoutAnEndMarkerIsRefused()
    {
        var full = PictureFixtures.SkiaJpeg(160, 120, (x, y) => ((byte)(x + y), (byte)(x * 2), (byte)(y * 2)), quality: 90);
        var source = _folder.Write("cut2.jpg", full[..(full.Length / 2)]);
        var output = OutputPath("cut2.png");

        var result = await Convert(source, FileFormat.Jpg, output, FileFormat.Png);

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task APictureThatChangedAfterPlanningIsCheckedAgain()
    {
        var source = _folder.Write("swapped.png", ThreeBands());
        File.WriteAllBytes(source, [0x4D, 0x5A, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]); // now a program

        var result = await Convert(source, FileFormat.Png, OutputPath("swapped.jpg"), FileFormat.Jpg);

        Assert.True(result.IsFailure);
        Assert.False(File.Exists(OutputPath("swapped.jpg")));
    }

    [Fact]
    public async Task AMissingInputIsAnInputError()
    {
        var result = await Convert(Path.Combine(_folder.In, "gone.png"), FileFormat.Png, OutputPath("gone.jpg"), FileFormat.Jpg);

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.False(File.Exists(OutputPath("gone.jpg")));
    }

    [Fact]
    public async Task AnExistingOutputIsNeverReplaced()
    {
        var source = _folder.Write("a.png", ThreeBands());
        var output = _folder.Write("a.jpg", [7, 7, 7], _folder.Out);

        var result = await Convert(source, FileFormat.Png, output, FileFormat.Jpg);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCode.IoFailure, result.Error!.Code);
        Assert.Equal([7, 7, 7], File.ReadAllBytes(output));
    }

    [Fact]
    public async Task AFolderThatIsNotThereIsAnOutputError()
    {
        var output = Path.Combine(_folder.Root, "nowhere", "a.jpg");

        var result = await Convert(Png(), FileFormat.Png, output, FileFormat.Jpg);

        Assert.Equal(ErrorCode.IoFailure, result.Error!.Code);
        Assert.False(Directory.Exists(Path.Combine(_folder.Root, "nowhere")));
    }

    [Theory]
    [InlineData("relative.jpg")]
    [InlineData(@"\\server\share\a.jpg")]
    [InlineData(@"C:\a\..\b.jpg")]
    public async Task AnUnsafeOutputPathIsRefused(string output)
    {
        var result = await Convert(Png(), FileFormat.Png, output, FileFormat.Jpg);

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
    }

    [Fact]
    public async Task ACancelBeforeStartReturnsCancelledAndWritesNothing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var output = OutputPath("cancelled.jpg");

        var result = await Convert(Png(), FileFormat.Png, output, FileFormat.Jpg, ct: cts.Token);

        Assert.True(result.IsCancelled);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task TheInputIsNeverChanged()
    {
        var source = _folder.Write("keep.png", ThreeBands());
        var bytes = File.ReadAllBytes(source);
        var time = File.GetLastWriteTimeUtc(source);

        await Convert(source, FileFormat.Png, OutputPath("keep.jpg"), FileFormat.Jpg);
        await Convert(source, FileFormat.Png, OutputPath("keep.jpg"), FileFormat.Jpg); // second time fails: the output exists
        await Convert(source, FileFormat.Png, OutputPath("keep.pdf"), FileFormat.Pdf);

        Assert.Equal(bytes, File.ReadAllBytes(source));
        Assert.Equal(time, File.GetLastWriteTimeUtc(source));
    }

    [Fact]
    public async Task NoMessageContainsAPathOrAFileName()
    {
        var secret = _folder.Write("secret-plans.png", Fixtures.Png());
        var results = new List<Result<ImageConversionResult>>
        {
            await Convert(secret, FileFormat.Png, OutputPath("secret-out.jpg"), FileFormat.Jpg),
            await Convert(Path.Combine(_folder.In, "secret-missing.png"), FileFormat.Png, OutputPath("secret-out2.jpg"), FileFormat.Jpg),
            await Convert(secret, FileFormat.Png, Path.Combine(_folder.Root, "secret-nowhere", "x.jpg"), FileFormat.Jpg),
            await Convert(secret, FileFormat.Png, OutputPath("secret-out3.pdf"), FileFormat.Pdf)
        };

        Assert.All(results, r =>
        {
            Assert.True(r.IsFailure);
            Assert.DoesNotContain("secret", r.Error!.SafeMessage, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@":\", r.Error.SafeMessage, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task TheArgumentsAreRequired()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _provider.ConvertAsync(null!, FileFormat.Png, "x", FileFormat.Jpg, new ImageEncodingOptions(), _limits, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _provider.ConvertAsync("x", FileFormat.Png, null!, FileFormat.Jpg, new ImageEncodingOptions(), _limits, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _provider.ConvertAsync("x", FileFormat.Png, "y", FileFormat.Jpg, null!, _limits, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _provider.ConvertAsync("x", FileFormat.Png, "y", FileFormat.Jpg, new ImageEncodingOptions(), null!, Ct));
    }

    // ---- size and parallel work --------------------------------------------------------------------------------------

    [Fact]
    public async Task ALargePictureWithTransparencyAndRotationConverts()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(3000, 2000, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.Transparent);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Color = new SKColor(200, 30, 30, 200) })
            canvas.DrawRect(0, 0, 1500, 1000, paint);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var source = _folder.Write("large.png", data.ToArray());
        var output = OutputPath("large.jpg");

        var result = await Convert(source, FileFormat.Png, output, FileFormat.Jpg);

        AssertOk(result);
        Assert.Equal((3000, 2000), (result.Value.Width, result.Value.Height));
        AssertNear(PictureFixtures.PixelAt(output, 2500, 1500), (255, 255, 255), 6, "transparent corner");
    }

    [Fact]
    public async Task ManyConversionsAtOnceAllSucceedAndAreIndependent()
    {
        var jobs = Enumerable.Range(0, 8).Select(i =>
        {
            var shade = (byte)(i * 30);
            var source = _folder.Write($"p{i}.png", PictureFixtures.Rgb(64, 64, (_, _) => (shade, shade, shade)));
            return (i, shade, source, output: OutputPath($"p{i}.jpg"));
        }).ToList();

        var results = await Task.WhenAll(jobs.Select(j => Convert(j.source, FileFormat.Png, j.output, FileFormat.Jpg, new ImageEncodingOptions { JpegQuality = 100 })));

        Assert.All(results, AssertOk);
        foreach (var job in jobs)
            AssertNear(PictureFixtures.PixelAt(job.output, 32, 32), (job.shade, job.shade, job.shade), 6, $"job {job.i}");
    }

    private string Png() => _folder.Write("small.png", PictureFixtures.Rgb(8, 8, (_, _) => (10, 20, 30)));
}
