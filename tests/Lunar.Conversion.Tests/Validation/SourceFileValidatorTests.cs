using Lunar.Application.Conversion.Models;
using Lunar.Conversion.Tests.Support;
using Lunar.Conversion.Validation;
using Lunar.Domain.Conversion;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Conversion.Tests.Validation;

// BE-20: what a file must be before Lunar will try to convert it, and what each refusal says. Synthetic files only.
public sealed class SourceFileValidatorTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly ConversionLimits _limits = new();

    public void Dispose() => _folder.Dispose();

    private (InspectedSource? Source, ValidationFailure? Failure) Inspect(string name, byte[] bytes, FileFormat format, ConversionLimits? limits = null)
    {
        var path = _folder.Write(name, bytes);
        return SourceFileValidator.Inspect(path, format, limits ?? _limits);
    }

    private static void AssertRejected((InspectedSource? Source, ValidationFailure? Failure) result, RejectionReason reason)
    {
        Assert.Null(result.Source);
        Assert.NotNull(result.Failure);
        Assert.Equal(reason, result.Failure.Reason);
        Assert.Equal(ErrorCode.InvalidInput, result.Failure.Code);
        Assert.False(string.IsNullOrWhiteSpace(result.Failure.Message));
    }

    // ---- accepted ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("a.png", FileFormat.Png)]
    [InlineData("a.jpg", FileFormat.Jpg)]
    [InlineData("a.jpeg", FileFormat.Jpeg)]
    [InlineData("a.pdf", FileFormat.Pdf)]
    [InlineData("a.docx", FileFormat.Docx)]
    public void AValidFileOfEachFormatIsAccepted(string name, FileFormat format)
    {
        var bytes = format switch
        {
            FileFormat.Png => Fixtures.Png(),
            FileFormat.Jpg or FileFormat.Jpeg => Fixtures.Jpeg(),
            FileFormat.Pdf => Fixtures.Pdf(),
            _ => Fixtures.Docx()
        };

        var result = Inspect(name, bytes, format);

        Assert.Null(result.Failure);
        Assert.Equal(bytes.Length, result.Source!.Length);
    }

    [Fact]
    public void ARealHundredPagePdfShapedFileIsAccepted()
    {
        // The validator does not count pages; the step that rasterizes enforces the page limit against the real count.
        Assert.Null(Inspect("hundred.pdf", Fixtures.Pdf(pages: 100), FileFormat.Pdf).Failure);
    }

    [Fact]
    public void APdfMayStartWithAFewBytesBeforeItsHeader()
    {
        Assert.Null(Inspect("lead.pdf", Fixtures.Pdf(prefix: "junk\n"), FileFormat.Pdf).Failure);
    }

    [Fact]
    public void AUnicodeNamedFileIsAccepted()
    {
        Assert.Null(Inspect("foto-ürün-写真.png", Fixtures.Png(), FileFormat.Png).Failure);
    }

    [Fact]
    public void AFileOpenInAnotherProgramForReadingAndWritingCanStillBeRead()
    {
        var path = _folder.Write("open.png", Fixtures.Png());
        using var other = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

        Assert.Null(SourceFileValidator.Inspect(path, FileFormat.Png, _limits).Failure);
    }

    // ---- not a usable file -------------------------------------------------------------------------------------------

    [Fact]
    public void AMissingFileAndAFolderAreNotFiles()
    {
        AssertRejected(SourceFileValidator.Inspect(Path.Combine(_folder.In, "gone.png"), FileFormat.Png, _limits), RejectionReason.NotAFile);
        var folder = Path.Combine(_folder.In, "dir.png");
        Directory.CreateDirectory(folder);
        AssertRejected(SourceFileValidator.Inspect(folder, FileFormat.Png, _limits), RejectionReason.NotAFile);
    }

    [Fact]
    public void AnEmptyFileIsRefused()
    {
        AssertRejected(Inspect("empty.png", [], FileFormat.Png), RejectionReason.Empty);
    }

    [Fact]
    public void AFileOverTheSizeLimitIsRefusedBeforeItIsRead()
    {
        var limits = new ConversionLimits { MaxFileBytes = 50 };

        var result = Inspect("big.png", Fixtures.Png(), FileFormat.Png, limits);

        AssertRejected(result, RejectionReason.TooLarge);
        Assert.Contains("limit", result.Failure!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileThatIsOnlyInTheCloudIsRefusedWithoutOpeningIt()
    {
        var path = _folder.Write("cloud.png", Fixtures.Png());
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Offline);

        var result = SourceFileValidator.Inspect(path, FileFormat.Png, _limits);

        AssertRejected(result, RejectionReason.OnlineOnly);
        Assert.Contains("this PC", result.Failure!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileAnotherProgramHoldsExclusivelyIsUnreadableNotACrash()
    {
        var path = _folder.Write("locked.png", Fixtures.Png());
        using var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        AssertRejected(SourceFileValidator.Inspect(path, FileFormat.Png, _limits), RejectionReason.Unreadable);
    }

    // ---- the extension lies ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("pngnamedjpg.jpg", FileFormat.Jpg, "PNG")]
    [InlineData("jpegnamedpng.png", FileFormat.Png, "JPEG")]
    [InlineData("pdfnamedpng.png", FileFormat.Png, "PDF")]
    [InlineData("docxnamedpdf.pdf", FileFormat.Pdf, "zip")]
    public void ContentOfAnotherFormatIsNamedInTheMessage(string name, FileFormat format, string looksLike)
    {
        var bytes = name.StartsWith("pngnamed", StringComparison.Ordinal) ? Fixtures.Png()
            : name.StartsWith("jpegnamed", StringComparison.Ordinal) ? Fixtures.Jpeg()
            : name.StartsWith("pdfnamed", StringComparison.Ordinal) ? Fixtures.Pdf()
            : Fixtures.Docx();

        var result = Inspect(name, bytes, format);

        AssertRejected(result, RejectionReason.SignatureMismatch);
        Assert.Contains(looksLike, result.Failure!.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(FileFormat.Png)]
    [InlineData(FileFormat.Jpg)]
    [InlineData(FileFormat.Pdf)]
    [InlineData(FileFormat.Docx)]
    public void AProgramRenamedToADocumentTypeIsRefused(FileFormat format)
    {
        var program = new byte[2048];
        program[0] = (byte)'M';
        program[1] = (byte)'Z';

        AssertRejected(Inspect("setup" + FileFormats.Extension(format), program, format), RejectionReason.SignatureMismatch);
    }

    [Fact]
    public void ARandomBlobIsNotAPicture()
    {
        var blob = new byte[5000];
        new Random(7).NextBytes(blob);

        AssertRejected(Inspect("noise.jpeg", blob, FileFormat.Jpeg), RejectionReason.SignatureMismatch);
    }

    // ---- PNG ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void ACutShortPngIsCorrupt()
    {
        var png = Fixtures.Png(endChunk: false);

        AssertRejected(Inspect("cut.png", png, FileFormat.Png), RejectionReason.Corrupt);
    }

    [Fact]
    public void APngThatEndsInsideItsHeaderIsCorrupt()
    {
        var png = Fixtures.Png();

        AssertRejected(Inspect("stub.png", png[..20], FileFormat.Png), RejectionReason.Corrupt);
    }

    [Fact]
    public void APngWithADamagedHeaderChecksumIsCorrupt()
    {
        AssertRejected(Inspect("crc.png", Fixtures.Png(badCrc: true), FileFormat.Png), RejectionReason.Corrupt);
    }

    [Theory]
    [InlineData(0u, 4u)]
    [InlineData(4u, 0u)]
    [InlineData(0x80000000u, 4u)]
    public void APngWithImpossibleDimensionsIsCorrupt(uint width, uint height)
    {
        AssertRejected(Inspect("dims.png", Fixtures.Png(width, height), FileFormat.Png), RejectionReason.Corrupt);
    }

    [Theory]
    [InlineData((byte)3, (byte)6)]
    [InlineData((byte)8, (byte)5)]
    [InlineData((byte)16, (byte)3)]
    public void APngWithAnImpossibleColourFormatIsCorrupt(byte bitDepth, byte colorType)
    {
        AssertRejected(Inspect("fmt.png", Fixtures.Png(bitDepth: bitDepth, colorType: colorType), FileFormat.Png), RejectionReason.Corrupt);
    }

    [Fact]
    public void ADecompressionBombPngIsRefusedByItsDeclaredSize()
    {
        // 65535 x 65535 pixels in a few dozen bytes: it would need 17 GB decoded.
        var result = Inspect("bomb.png", Fixtures.Png(65_535, 65_535), FileFormat.Png);

        AssertRejected(result, RejectionReason.TooManyPixels);
        Assert.Contains("megapixels", result.Failure!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APictureExactlyAtThePixelLimitIsAcceptedAndOneOverIsNot()
    {
        var limits = new ConversionLimits { MaxImagePixels = 100 * 100 };

        Assert.Null(Inspect("edge.png", Fixtures.Png(100, 100), FileFormat.Png, limits).Failure);
        AssertRejected(Inspect("over.png", Fixtures.Png(101, 100), FileFormat.Png, limits), RejectionReason.TooManyPixels);
    }

    // ---- JPEG --------------------------------------------------------------------------------------------------------

    [Fact]
    public void ACutShortJpegIsCorrupt()
    {
        AssertRejected(Inspect("cut.jpg", Fixtures.Jpeg(endMarker: false), FileFormat.Jpg), RejectionReason.Corrupt);
    }

    [Fact]
    public void AJpegWithoutAFrameHeaderIsCorrupt()
    {
        AssertRejected(Inspect("noframe.jpg", Fixtures.Jpeg(frame: false), FileFormat.Jpg), RejectionReason.Corrupt);
    }

    [Fact]
    public void AJpegWithZeroDimensionsIsCorrupt()
    {
        AssertRejected(Inspect("zero.jpg", Fixtures.Jpeg(width: 0), FileFormat.Jpg), RejectionReason.Corrupt);
    }

    [Fact]
    public void AJpegThatEndsInsideAHeaderSegmentIsCorrupt()
    {
        var jpeg = Fixtures.Jpeg();

        AssertRejected(Inspect("stub.jpg", jpeg[..12], FileFormat.Jpg), RejectionReason.Corrupt);
    }

    [Fact]
    public void AJpegSegmentThatClaimsToReachPastTheEndOfTheFileIsCorrupt()
    {
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

        AssertRejected(Inspect("long.jpg", jpeg, FileFormat.Jpg), RejectionReason.Corrupt);
    }

    [Fact]
    public void AJpegWithAHugeDeclaredSizeIsRefused()
    {
        // 65535 x 65535 is over 4 billion pixels; the default limit is 50 million.
        AssertRejected(Inspect("huge.jpeg", Fixtures.Jpeg(65_535, 65_535), FileFormat.Jpeg), RejectionReason.TooManyPixels);
    }

    // ---- PDF ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void APdfWithoutItsEndMarkerIsCorrupt()
    {
        AssertRejected(Inspect("cut.pdf", Fixtures.Pdf(endMarker: false), FileFormat.Pdf), RejectionReason.Corrupt);
    }

    [Fact]
    public void APdfWhoseHeaderHasNoVersionIsCorrupt()
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes("%PDF-x\nstartxref\n0\n%%EOF\n");

        AssertRejected(Inspect("version.pdf", bytes, FileFormat.Pdf), RejectionReason.Corrupt);
    }

    [Fact]
    public void ATextFileNamedPdfIsNotAPdf()
    {
        AssertRejected(Inspect("notes.pdf", System.Text.Encoding.UTF8.GetBytes("just some text"), FileFormat.Pdf), RejectionReason.SignatureMismatch);
    }

    // ---- DOCX --------------------------------------------------------------------------------------------------------

    [Fact]
    public void AnOldOrPasswordProtectedWordFileGetsItsOwnExplanation()
    {
        var result = Inspect("old.docx", Fixtures.OleDocument(), FileFormat.Docx);

        AssertRejected(result, RejectionReason.EncryptedOrLegacy);
        Assert.Contains("password", result.Failure!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AZipThatIsNotAWordDocumentIsRefused()
    {
        var spreadsheet = Fixtures.Docx(includeDocument: false, includeContentTypes: true, ("xl/workbook.xml", "<w/>"u8.ToArray()));

        AssertRejected(Inspect("sheet.docx", spreadsheet, FileFormat.Docx), RejectionReason.SignatureMismatch);
    }

    [Fact]
    public void APackageWithoutContentTypesIsRefused()
    {
        AssertRejected(Inspect("nocontent.docx", Fixtures.Docx(includeDocument: true, includeContentTypes: false), FileFormat.Docx), RejectionReason.SignatureMismatch);
    }

    [Fact]
    public void AZipHeaderFollowedByGarbageIsCorrupt()
    {
        var bytes = new byte[3000];
        new byte[] { 0x50, 0x4B, 0x03, 0x04 }.CopyTo(bytes, 0);

        AssertRejected(Inspect("garbage.docx", bytes, FileFormat.Docx), RejectionReason.Corrupt);
    }

    [Fact]
    public void ACutShortPackageIsCorrupt()
    {
        var docx = Fixtures.Docx();

        AssertRejected(Inspect("cut.docx", docx[..(docx.Length / 2)], FileFormat.Docx), RejectionReason.Corrupt);
    }

    [Fact]
    public void AZipBombIsRefusedByItsCompressionRatio()
    {
        var docx = Fixtures.Docx(Fixtures.Bomb(10));

        var result = Inspect("bomb.docx", docx, FileFormat.Docx);

        AssertRejected(result, RejectionReason.ArchiveLimits);
        Assert.Contains("compressed", result.Failure!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APartThatIsTooLargeOnceUnpackedIsRefused()
    {
        var limits = new ConversionLimits { MaxArchiveEntryBytes = 1024 * 1024, MaxArchiveCompressionRatio = 100_000 };

        AssertRejected(Inspect("entry.docx", Fixtures.Docx(Fixtures.Bomb(2)), FileFormat.Docx, limits), RejectionReason.ArchiveLimits);
    }

    [Fact]
    public void APackageThatIsTooLargeOnceUnpackedIsRefused()
    {
        var limits = new ConversionLimits { MaxArchiveUncompressedBytes = 3 * 1024 * 1024, MaxArchiveCompressionRatio = 100_000 };
        var docx = Fixtures.Docx(("word/media/a.bin", new byte[2 * 1024 * 1024]), ("word/media/b.bin", new byte[2 * 1024 * 1024]));

        AssertRejected(Inspect("total.docx", docx, FileFormat.Docx, limits), RejectionReason.ArchiveLimits);
    }

    [Fact]
    public void APackageWithTooManyPartsIsRefusedBeforeItsDirectoryIsLoaded()
    {
        var limits = new ConversionLimits { MaxArchiveEntries = 5 };
        var extras = Enumerable.Range(0, 10).Select(i => ($"word/media/{i}.png", new byte[] { 1 })).ToArray();

        var result = Inspect("many.docx", Fixtures.Docx(extras), FileFormat.Docx, limits);

        AssertRejected(result, RejectionReason.ArchiveLimits);
        Assert.Contains("parts", result.Failure!.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../evil.xml")]
    [InlineData("word/../../evil.xml")]
    [InlineData("/absolute.xml")]
    [InlineData("word\\back.xml")]
    [InlineData("C:evil.xml")]
    public void APartWithAnUnsafeNameIsRefused(string name)
    {
        var docx = Fixtures.Docx((name, new byte[] { 1, 2, 3 }));

        AssertRejected(Inspect("slip.docx", docx, FileFormat.Docx), RejectionReason.Corrupt);
    }

    [Theory]
    [InlineData("word/vbaProject.bin")]
    [InlineData("word/VBAPROJECT.BIN")]
    public void AWordFileWithMacrosIsRefused(string name)
    {
        var result = Inspect("macro.docx", Fixtures.Docx((name, new byte[] { 1 })), FileFormat.Docx);

        AssertRejected(result, RejectionReason.HasMacros);
        Assert.Contains("never runs macros", result.Failure!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARealisticPackageWithImagesAndStylesIsAccepted()
    {
        var docx = Fixtures.Docx(
            ("word/styles.xml", "<styles/>"u8.ToArray()),
            ("word/_rels/document.xml.rels", "<Relationships/>"u8.ToArray()),
            ("word/media/image1.png", Fixtures.Png()),
            ("docProps/core.xml", "<core/>"u8.ToArray()));

        Assert.Null(Inspect("real.docx", docx, FileFormat.Docx).Failure);
    }

    // ---- messages ----------------------------------------------------------------------------------------------------

    [Fact]
    public void NoRefusalNamesThePathOrTheFile()
    {
        var folder = _folder.In;
        var cases = new List<(string Name, byte[] Bytes, FileFormat Format)>
        {
            ("secret-report.png", Fixtures.Png(endChunk: false), FileFormat.Png),
            ("secret-report.jpg", Fixtures.Jpeg(frame: false), FileFormat.Jpg),
            ("secret-report.pdf", Fixtures.Pdf(header: false), FileFormat.Pdf),
            ("secret-report.docx", Fixtures.Docx(("word/vbaProject.bin", new byte[] { 1 })), FileFormat.Docx),
            ("secret-report2.png", [], FileFormat.Png)
        };

        foreach (var (name, bytes, format) in cases)
        {
            var failure = Inspect(name, bytes, format).Failure;
            Assert.NotNull(failure);
            Assert.DoesNotContain("secret", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(folder, failure.Message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
