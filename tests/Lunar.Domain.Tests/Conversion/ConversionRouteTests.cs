using Lunar.Domain.Conversion;
using Xunit;

namespace Lunar.Domain.Tests.Conversion;

// BE-19: a route that contradicts itself cannot be built.
public sealed class ConversionRouteTests
{
    [Fact]
    public void AValidRouteKeepsWhatItWasGiven()
    {
        var route = new ConversionRoute(FileFormat.Png, FileFormat.Jpg, ConversionMode.Native, ConversionFidelity.Lossy, flattensTransparency: true);

        Assert.Equal(FileFormat.Png, route.Source);
        Assert.Equal(FileFormat.Jpg, route.Target);
        Assert.True(route.IsSupported);
        Assert.True(route.IsLossy);
        Assert.True(route.FlattensTransparency);
        Assert.False(route.ProducesOneOutputPerPage);
        Assert.Equal(UnsupportedReason.None, route.UnsupportedReason);
    }

    [Fact]
    public void RoutesWithTheSameValuesAreEqual()
    {
        var one = new ConversionRoute(FileFormat.Pdf, FileFormat.Png, ConversionMode.Rasterize, ConversionFidelity.Rasterized, producesOneOutputPerPage: true);
        var two = new ConversionRoute(FileFormat.Pdf, FileFormat.Png, ConversionMode.Rasterize, ConversionFidelity.Rasterized, producesOneOutputPerPage: true);

        Assert.Equal(one, two);
    }

    [Fact]
    public void OnlyAnUnsupportedRouteMayHaveNoFidelity()
    {
        Assert.Throws<ArgumentException>(() =>
            new ConversionRoute(FileFormat.Png, FileFormat.Pdf, ConversionMode.Embed, ConversionFidelity.NotAvailable));
        Assert.Throws<ArgumentException>(() =>
            new ConversionRoute(FileFormat.Pdf, FileFormat.Docx, ConversionMode.Unsupported, ConversionFidelity.Embedded, UnsupportedReason.PostV1Stretch));
    }

    [Fact]
    public void AnUnsupportedRouteNeedsAReasonAndASupportedOneHasNone()
    {
        Assert.Throws<ArgumentException>(() =>
            new ConversionRoute(FileFormat.Pdf, FileFormat.Docx, ConversionMode.Unsupported, ConversionFidelity.NotAvailable));
        Assert.Throws<ArgumentException>(() =>
            new ConversionRoute(FileFormat.Png, FileFormat.Pdf, ConversionMode.Embed, ConversionFidelity.Embedded, UnsupportedReason.PostV1Stretch));
    }

    [Fact]
    public void AnUnsupportedRouteProducesNothing()
    {
        Assert.Throws<ArgumentException>(() =>
            new ConversionRoute(FileFormat.Pdf, FileFormat.Docx, ConversionMode.Unsupported, ConversionFidelity.NotAvailable, UnsupportedReason.PostV1Stretch, producesOneOutputPerPage: true));
        Assert.Throws<ArgumentException>(() =>
            new ConversionRoute(FileFormat.Pdf, FileFormat.Docx, ConversionMode.Unsupported, ConversionFidelity.NotAvailable, UnsupportedReason.PostV1Stretch, flattensTransparency: true));
    }

    [Fact]
    public void ASameFormatCopyIsExactlyASameEncodingPairWithIdenticalBytes()
    {
        // a different encoding cannot be a copy
        Assert.Throws<ArgumentException>(() =>
            new ConversionRoute(FileFormat.Png, FileFormat.Jpg, ConversionMode.SameFormat, ConversionFidelity.Identical));
        // a same-encoding pair cannot be a conversion
        Assert.Throws<ArgumentException>(() =>
            new ConversionRoute(FileFormat.Jpg, FileFormat.Jpeg, ConversionMode.Native, ConversionFidelity.Lossy));
        // a copy cannot claim anything but identical bytes
        Assert.Throws<ArgumentException>(() =>
            new ConversionRoute(FileFormat.Png, FileFormat.Png, ConversionMode.SameFormat, ConversionFidelity.Lossy));
        // identical bytes cannot come from a conversion
        Assert.Throws<ArgumentException>(() =>
            new ConversionRoute(FileFormat.Png, FileFormat.Pdf, ConversionMode.Embed, ConversionFidelity.Identical));
    }

    [Fact]
    public void AnExternalRendererAlwaysMeansTheLayoutDependsOnIt()
    {
        Assert.Throws<ArgumentException>(() =>
            new ConversionRoute(FileFormat.Docx, FileFormat.Pdf, ConversionMode.ExternalRenderer, ConversionFidelity.Embedded));
        Assert.Throws<ArgumentException>(() =>
            new ConversionRoute(FileFormat.Pdf, FileFormat.Png, ConversionMode.Rasterize, ConversionFidelity.LayoutDependent));
    }

    [Fact]
    public void UnknownValuesAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ConversionRoute((FileFormat)42, FileFormat.Png, ConversionMode.Native, ConversionFidelity.Lossy));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ConversionRoute(FileFormat.Png, (FileFormat)42, ConversionMode.Native, ConversionFidelity.Lossy));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ConversionRoute(FileFormat.Png, FileFormat.Jpg, (ConversionMode)42, ConversionFidelity.Lossy));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ConversionRoute(FileFormat.Png, FileFormat.Jpg, ConversionMode.Native, (ConversionFidelity)42));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ConversionRoute(FileFormat.Png, FileFormat.Jpg, ConversionMode.Native, ConversionFidelity.Lossy, (UnsupportedReason)42));
    }
}
