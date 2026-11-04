using Lunar.Domain.Conversion;
using Xunit;

namespace Lunar.Domain.Tests.Conversion;

// BE-23: where a picture goes on a page.
public sealed class PageLayoutTests
{
    private const double Pdf = 14400;
    private const double Word = 1584;

    private static void AssertNear(double expected, double actual, string what = "value") =>
        Assert.True(Math.Abs(expected - actual) < 0.01, $"{what}: expected {expected} but got {actual}.");

    [Fact]
    public void TheDefaultsAreAnAutomaticPageWithNoMarginAndTheWholePictureShown()
    {
        var options = new PageLayoutOptions();

        Assert.Equal(PageSize.Auto, options.Size);
        Assert.Equal(0, options.MarginPoints);
        Assert.Equal(ImageFit.Fit, options.Fit);
        Assert.Same(options, options.Validated());
    }

    // ---- automatic pages ---------------------------------------------------------------------------------------------

    [Fact]
    public void AnAutomaticPageIsThePictureAt96PixelsPerInch()
    {
        var g = PageLayout.Compute(new PageLayoutOptions(), 1920, 1080, Pdf);

        AssertNear(1440, g.PageWidth, "page width");
        AssertNear(810, g.PageHeight, "page height");
        AssertNear(1440, g.DrawWidth);
        AssertNear(810, g.DrawHeight);
        Assert.Equal((0, 0), (g.DrawX, g.DrawY));
        Assert.False(g.IsCropped);
    }

    [Fact]
    public void AnAutomaticPageAddsTheMarginAroundThePicture()
    {
        var g = PageLayout.Compute(new PageLayoutOptions { MarginPoints = 36 }, 400, 200, Pdf);

        AssertNear(300 + 72, g.PageWidth);
        AssertNear(150 + 72, g.PageHeight);
        AssertNear(36, g.BoxX);
        AssertNear(300, g.BoxWidth);
        AssertNear(36, g.DrawX);
        AssertNear(300, g.DrawWidth);
    }

    [Fact]
    public void AnAutomaticPageForAHugePictureIsShrunkToTheLargestPageTheFormatAllows()
    {
        var word = PageLayout.Compute(new PageLayoutOptions(), 8000, 4000, Word);
        var pdf = PageLayout.Compute(new PageLayoutOptions(), 40000, 10000, Pdf);

        AssertNear(1584, word.PageWidth, "Word width");
        AssertNear(792, word.PageHeight, "Word height keeps the shape");
        AssertNear(14400, pdf.PageWidth, "PDF width");
        AssertNear(3600, pdf.PageHeight, "PDF height keeps the shape");
    }

    [Fact]
    public void ALargePageWithAMarginStillFitsInTheLimit()
    {
        var g = PageLayout.Compute(new PageLayoutOptions { MarginPoints = 72 }, 8000, 4000, Word);

        Assert.True(g.PageWidth <= Word + 0.01);
        AssertNear(Word - 144, g.DrawWidth);
    }

    [Fact]
    public void ATinyPictureGetsAUsablePage()
    {
        var g = PageLayout.Compute(new PageLayoutOptions(), 8, 8, Pdf);

        AssertNear(72, g.PageWidth);
        AssertNear(72, g.PageHeight);
    }

    [Fact]
    public void AnAutomaticPagePreservesTheShapeOfAnyPicture()
    {
        foreach (var (w, h) in new[] { (1, 1), (1, 5000), (5000, 1), (333, 777), (4000, 3000), (123, 7) })
        {
            var g = PageLayout.Compute(new PageLayoutOptions(), w, h, Pdf);

            AssertNear((double)w / h, g.DrawWidth / g.DrawHeight, $"{w}x{h} aspect");
            Assert.True(g.PageWidth <= Pdf + 0.01 && g.PageHeight <= Pdf + 0.01);
        }
    }

    // ---- fixed pages -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(PageSize.A4, 100, 200, 595.2756, 841.8898)]
    [InlineData(PageSize.A4, 200, 100, 841.8898, 595.2756)]
    [InlineData(PageSize.A4, 100, 100, 595.2756, 841.8898)]
    [InlineData(PageSize.Letter, 100, 200, 612, 792)]
    [InlineData(PageSize.Letter, 300, 100, 792, 612)]
    public void AFixedPageTurnsSidewaysForAWidePicture(PageSize size, int w, int h, double pageWidth, double pageHeight)
    {
        var g = PageLayout.Compute(new PageLayoutOptions { Size = size }, w, h, Pdf);

        AssertNear(pageWidth, g.PageWidth, "page width");
        AssertNear(pageHeight, g.PageHeight, "page height");
    }

    [Fact]
    public void FitShowsTheWholePictureCentredAndScalesASmallOneUp()
    {
        var g = PageLayout.Compute(new PageLayoutOptions { Size = PageSize.Letter, MarginPoints = 36, Fit = ImageFit.Fit }, 100, 100, Pdf);

        // box is 540 x 720; a square picture is limited by the width
        AssertNear(540, g.DrawWidth);
        AssertNear(540, g.DrawHeight);
        AssertNear(36, g.DrawX);
        AssertNear(36 + ((720 - 540) / 2.0), g.DrawY);
        Assert.False(g.IsCropped);
    }

    [Fact]
    public void FitScalesALargePictureDown()
    {
        var g = PageLayout.Compute(new PageLayoutOptions { Size = PageSize.A4, Fit = ImageFit.Fit }, 5000, 7000, Pdf);

        Assert.True(g.DrawWidth <= g.BoxWidth + 0.01 && g.DrawHeight <= g.BoxHeight + 0.01);
        AssertNear(5000.0 / 7000, g.DrawWidth / g.DrawHeight, "aspect");
    }

    [Fact]
    public void CoverFillsTheBoxAndCutsWhatSticksOut()
    {
        var g = PageLayout.Compute(new PageLayoutOptions { Size = PageSize.Letter, MarginPoints = 36, Fit = ImageFit.Cover }, 100, 100, Pdf);

        // box is 540 x 720; a square picture must be 720 high to cover it, so it is 720 wide and 90 pt too wide on each side
        AssertNear(720, g.DrawWidth);
        AssertNear(720, g.DrawHeight);
        AssertNear(36 - 90, g.DrawX);
        AssertNear(36, g.DrawY);
        Assert.True(g.IsCropped);
    }

    [Fact]
    public void CoverOfAPictureWithTheBoxsOwnShapeCutsNothing()
    {
        var g = PageLayout.Compute(new PageLayoutOptions { Size = PageSize.Letter, Fit = ImageFit.Cover }, 612, 792, Pdf);

        Assert.False(g.IsCropped);
        AssertNear(612, g.DrawWidth);
        AssertNear(792, g.DrawHeight);
    }

    [Fact]
    public void ThePictureIsAlwaysCentredInTheBox()
    {
        foreach (var fit in Enum.GetValues<ImageFit>())
        {
            foreach (var (w, h) in new[] { (100, 300), (300, 100), (640, 480), (90, 90) })
            {
                var g = PageLayout.Compute(new PageLayoutOptions { Size = PageSize.A4, MarginPoints = 20, Fit = fit }, w, h, Pdf);

                AssertNear(g.BoxX + (g.BoxWidth / 2), g.DrawX + (g.DrawWidth / 2), $"{fit} {w}x{h} centre x");
                AssertNear(g.BoxY + (g.BoxHeight / 2), g.DrawY + (g.DrawHeight / 2), $"{fit} {w}x{h} centre y");
            }
        }
    }

    // ---- bad input ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(-1)]
    [InlineData(145)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ImpossibleMarginsAreRejected(double margin)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageLayoutOptions { MarginPoints = margin }.Validated());
    }

    [Fact]
    public void UnknownEnumValuesAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageLayoutOptions { Size = (PageSize)9 }.Validated());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageLayoutOptions { Fit = (ImageFit)9 }.Validated());
    }

    [Fact]
    public void EvenTheLargestMarginLeavesRoomOnAFixedPage()
    {
        // The margin is capped at 144 points, so the smallest box (Letter, 324 wide) is always well above the 36 point minimum.
        foreach (var size in new[] { PageSize.A4, PageSize.Letter })
        {
            var g = PageLayout.Compute(new PageLayoutOptions { Size = size, MarginPoints = 144 }, 10, 10, Pdf);

            Assert.True(g.BoxWidth >= PageLayout.MinBoxSide && g.BoxHeight >= PageLayout.MinBoxSide);
        }
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-1, 5)]
    public void APictureNeedsPixels(int w, int h)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PageLayout.Compute(new PageLayoutOptions(), w, h, Pdf));
    }

    [Fact]
    public void ANullOrAbsurdMaximumIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => PageLayout.Compute(null!, 10, 10, Pdf));
        Assert.Throws<ArgumentOutOfRangeException>(() => PageLayout.Compute(new PageLayoutOptions(), 10, 10, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => PageLayout.Compute(new PageLayoutOptions(), 10, 10, double.NaN));
    }
}
