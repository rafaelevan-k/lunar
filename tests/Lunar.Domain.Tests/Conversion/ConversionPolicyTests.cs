using Lunar.Domain.Conversion;
using Xunit;

namespace Lunar.Domain.Tests.Conversion;

// BE-19: all 25 ordered pairs are classified and pinned against a table written out here from PRD section 5 and
// docs/CONVERSION_MATRIX.md, independently of the policy's own code.
public sealed class ConversionPolicyTests
{
    private const FileFormat Png = FileFormat.Png;
    private const FileFormat Jpg = FileFormat.Jpg;
    private const FileFormat Jpeg = FileFormat.Jpeg;
    private const FileFormat Docx = FileFormat.Docx;
    private const FileFormat Pdf = FileFormat.Pdf;

    // source, target, mode, fidelity (PRD: S same, N native, R rendered/embedded, D dependency, X not in v1)
    private static readonly (FileFormat Source, FileFormat Target, ConversionMode Mode, ConversionFidelity Fidelity)[] Table =
    [
        (Png, Png, ConversionMode.SameFormat, ConversionFidelity.Identical),
        (Png, Jpg, ConversionMode.Native, ConversionFidelity.Lossy),
        (Png, Jpeg, ConversionMode.Native, ConversionFidelity.Lossy),
        (Png, Docx, ConversionMode.Embed, ConversionFidelity.Embedded),
        (Png, Pdf, ConversionMode.Embed, ConversionFidelity.Embedded),

        (Jpg, Png, ConversionMode.Native, ConversionFidelity.PixelPreserving),
        (Jpg, Jpg, ConversionMode.SameFormat, ConversionFidelity.Identical),
        (Jpg, Jpeg, ConversionMode.SameFormat, ConversionFidelity.Identical),
        (Jpg, Docx, ConversionMode.Embed, ConversionFidelity.Embedded),
        (Jpg, Pdf, ConversionMode.Embed, ConversionFidelity.Embedded),

        (Jpeg, Png, ConversionMode.Native, ConversionFidelity.PixelPreserving),
        (Jpeg, Jpg, ConversionMode.SameFormat, ConversionFidelity.Identical),
        (Jpeg, Jpeg, ConversionMode.SameFormat, ConversionFidelity.Identical),
        (Jpeg, Docx, ConversionMode.Embed, ConversionFidelity.Embedded),
        (Jpeg, Pdf, ConversionMode.Embed, ConversionFidelity.Embedded),

        (Docx, Png, ConversionMode.ExternalRenderer, ConversionFidelity.LayoutDependent),
        (Docx, Jpg, ConversionMode.ExternalRenderer, ConversionFidelity.LayoutDependent),
        (Docx, Jpeg, ConversionMode.ExternalRenderer, ConversionFidelity.LayoutDependent),
        (Docx, Docx, ConversionMode.SameFormat, ConversionFidelity.Identical),
        (Docx, Pdf, ConversionMode.ExternalRenderer, ConversionFidelity.LayoutDependent),

        (Pdf, Png, ConversionMode.Rasterize, ConversionFidelity.Rasterized),
        (Pdf, Jpg, ConversionMode.Rasterize, ConversionFidelity.Rasterized),
        (Pdf, Jpeg, ConversionMode.Rasterize, ConversionFidelity.Rasterized),
        (Pdf, Docx, ConversionMode.Unsupported, ConversionFidelity.NotAvailable),
        (Pdf, Pdf, ConversionMode.SameFormat, ConversionFidelity.Identical)
    ];

    public static TheoryData<FileFormat, FileFormat, ConversionMode, ConversionFidelity> Expected
    {
        get
        {
            var data = new TheoryData<FileFormat, FileFormat, ConversionMode, ConversionFidelity>();
            foreach (var (source, target, mode, fidelity) in Table)
                data.Add(source, target, mode, fidelity);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Expected))]
    public void EveryOrderedPairIsClassifiedAsTheMatrixSays(FileFormat source, FileFormat target, ConversionMode mode, ConversionFidelity fidelity)
    {
        var route = ConversionPolicy.Resolve(source, target);

        Assert.Equal(source, route.Source);
        Assert.Equal(target, route.Target);
        Assert.Equal(mode, route.Mode);
        Assert.Equal(fidelity, route.Fidelity);
    }

    [Fact]
    public void TheTableAboveHasAllTwentyFivePairsExactlyOnce()
    {
        var pairs = Table.Select(row => (row.Source, row.Target)).ToList();

        Assert.Equal(25, pairs.Count);
        Assert.Equal(25, pairs.Distinct().Count());
        Assert.Equal(25, FileFormats.All.Count * FileFormats.All.Count);
    }

    [Fact]
    public void AllHoldsTwentyFiveRoutesOneForEachPairInAFixedOrder()
    {
        var all = ConversionPolicy.All;

        Assert.Equal(25, all.Count);
        Assert.Equal(25, all.Select(r => (r.Source, r.Target)).Distinct().Count());
        var expectedOrder = FileFormats.All.SelectMany(s => FileFormats.All.Select(t => (s, t)));
        Assert.Equal(expectedOrder, all.Select(r => (r.Source, r.Target)));
    }

    [Fact]
    public void ResolvingAPairTwiceGivesTheSameRoute()
    {
        foreach (var source in FileFormats.All)
        {
            foreach (var target in FileFormats.All)
                Assert.Equal(ConversionPolicy.Resolve(source, target), ConversionPolicy.Resolve(source, target));
        }
    }

    // ---- same-format behaviour ---------------------------------------------------------------------------------------

    [Fact]
    public void SameEncodingPairsAreCopiesWithIdenticalBytes()
    {
        var copies = ConversionPolicy.All.Where(r => r.Mode == ConversionMode.SameFormat).ToList();

        // five formats with themselves, plus JPG to JPEG and JPEG to JPG
        Assert.Equal(7, copies.Count);
        Assert.All(copies, r =>
        {
            Assert.Equal(ConversionFidelity.Identical, r.Fidelity);
            Assert.True(FileFormats.SameEncoding(r.Source, r.Target));
            Assert.True(r.IsSupported);
            Assert.False(r.RequiresExternalRenderer);
            Assert.False(r.IsLossy);
        });
    }

    [Fact]
    public void JpgAndJpegAreCopiesOfEachOtherNotReEncodes()
    {
        Assert.Equal(ConversionMode.SameFormat, ConversionPolicy.Resolve(Jpg, Jpeg).Mode);
        Assert.Equal(ConversionMode.SameFormat, ConversionPolicy.Resolve(Jpeg, Jpg).Mode);
        Assert.False(ConversionPolicy.Resolve(Jpg, Jpeg).FlattensTransparency);
    }

    // ---- the unsupported stretch route -------------------------------------------------------------------------------

    [Fact]
    public void PdfToDocxIsTheOnlyUnsupportedRouteAndSaysItIsALaterStretch()
    {
        var unsupported = ConversionPolicy.All.Where(r => !r.IsSupported).ToList();

        var route = Assert.Single(unsupported);
        Assert.Equal((Pdf, Docx), (route.Source, route.Target));
        Assert.Equal(ConversionMode.Unsupported, route.Mode);
        Assert.Equal(ConversionFidelity.NotAvailable, route.Fidelity);
        Assert.Equal(UnsupportedReason.PostV1Stretch, route.UnsupportedReason);
        Assert.False(route.RequiresExternalRenderer);
        Assert.False(route.ProducesOneOutputPerPage);
    }

    [Fact]
    public void EverySupportedRouteHasNoUnsupportedReason()
    {
        Assert.All(ConversionPolicy.All.Where(r => r.IsSupported), r => Assert.Equal(UnsupportedReason.None, r.UnsupportedReason));
    }

    // ---- what each kind of route promises ----------------------------------------------------------------------------

    [Fact]
    public void OnlyRoutesFromDocxAsideFromItsOwnCopyNeedAnExternalRenderer()
    {
        var needing = ConversionPolicy.All.Where(r => r.RequiresExternalRenderer).ToList();

        Assert.Equal(4, needing.Count);
        Assert.All(needing, r =>
        {
            Assert.Equal(Docx, r.Source);
            Assert.NotEqual(Docx, r.Target);
            Assert.Equal(ConversionFidelity.LayoutDependent, r.Fidelity);
        });
    }

    [Fact]
    public void OnlyPngToJpegFamilyIsLossyAndFlattensTransparency()
    {
        var lossy = ConversionPolicy.All.Where(r => r.IsLossy).Select(r => (r.Source, r.Target)).ToList();
        var flattening = ConversionPolicy.All.Where(r => r.FlattensTransparency).Select(r => (r.Source, r.Target)).ToList();

        Assert.Equal([(Png, Jpg), (Png, Jpeg)], lossy);
        Assert.Equal(lossy, flattening);
    }

    [Fact]
    public void PagedSourcesGiveOneOutputPerPageOnlyWhenTheTargetIsAPicture()
    {
        var perPage = ConversionPolicy.All.Where(r => r.ProducesOneOutputPerPage).Select(r => (r.Source, r.Target)).ToList();

        Assert.Equal(
            [(Docx, Png), (Docx, Jpg), (Docx, Jpeg), (Pdf, Png), (Pdf, Jpg), (Pdf, Jpeg)],
            perPage);
        Assert.All(perPage, p => Assert.True(FileFormats.IsImage(p.Target)));
    }

    [Fact]
    public void PicturesBecomeDocumentsByEmbeddingNeverByRecognisingText()
    {
        foreach (var picture in new[] { Png, Jpg, Jpeg })
        {
            foreach (var document in new[] { Docx, Pdf })
            {
                var route = ConversionPolicy.Resolve(picture, document);
                Assert.Equal(ConversionMode.Embed, route.Mode);
                Assert.Equal(ConversionFidelity.Embedded, route.Fidelity);
            }
        }
    }

    [Fact]
    public void JpegBackToPngKeepsPixelsButNotTheOriginalFile()
    {
        foreach (var jpeg in new[] { Jpg, Jpeg })
        {
            var route = ConversionPolicy.Resolve(jpeg, Png);
            Assert.Equal(ConversionFidelity.PixelPreserving, route.Fidelity);
            Assert.False(route.IsLossy);
            Assert.False(route.FlattensTransparency);
        }
    }

    [Fact]
    public void NoRouteIsEverLabelledAsLosslessOrEditable()
    {
        // The vocabulary itself has no such word: this pins that nobody adds one without revisiting the notices.
        var names = Enum.GetNames<ConversionFidelity>();

        Assert.DoesNotContain(names, n => n.Contains("Lossless", StringComparison.OrdinalIgnoreCase) || n.Contains("Editable", StringComparison.OrdinalIgnoreCase));
    }

    // ---- targets and bad input ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(FileFormat.Png, new[] { FileFormat.Png, FileFormat.Jpg, FileFormat.Jpeg, FileFormat.Docx, FileFormat.Pdf })]
    [InlineData(FileFormat.Jpg, new[] { FileFormat.Png, FileFormat.Jpg, FileFormat.Jpeg, FileFormat.Docx, FileFormat.Pdf })]
    [InlineData(FileFormat.Jpeg, new[] { FileFormat.Png, FileFormat.Jpg, FileFormat.Jpeg, FileFormat.Docx, FileFormat.Pdf })]
    [InlineData(FileFormat.Docx, new[] { FileFormat.Png, FileFormat.Jpg, FileFormat.Jpeg, FileFormat.Docx, FileFormat.Pdf })]
    [InlineData(FileFormat.Pdf, new[] { FileFormat.Png, FileFormat.Jpg, FileFormat.Jpeg, FileFormat.Pdf })]
    public void TargetsListWhatASourceCanBecomeInV1(FileFormat source, FileFormat[] expected)
    {
        Assert.Equal(expected, ConversionPolicy.TargetsFor(source));
    }

    [Fact]
    public void AnUnknownFormatIsRejectedNotGuessed()
    {
        var unknown = (FileFormat)99;

        Assert.Throws<ArgumentOutOfRangeException>(() => ConversionPolicy.Resolve(unknown, Png));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConversionPolicy.Resolve(Png, unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConversionPolicy.TargetsFor(unknown));
    }
}
