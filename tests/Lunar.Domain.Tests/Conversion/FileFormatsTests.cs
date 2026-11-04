using Lunar.Domain.Conversion;
using Xunit;

namespace Lunar.Domain.Tests.Conversion;

// BE-19: names and encodings of the five formats. Extensions only: nothing here looks at a file.
public sealed class FileFormatsTests
{
    [Fact]
    public void AllListsTheFiveFormatsInAFixedOrder()
    {
        Assert.Equal([FileFormat.Png, FileFormat.Jpg, FileFormat.Jpeg, FileFormat.Docx, FileFormat.Pdf], FileFormats.All);
        Assert.Equal(Enum.GetValues<FileFormat>(), FileFormats.All);
    }

    [Theory]
    [InlineData(FileFormat.Png, ".png")]
    [InlineData(FileFormat.Jpg, ".jpg")]
    [InlineData(FileFormat.Jpeg, ".jpeg")]
    [InlineData(FileFormat.Docx, ".docx")]
    [InlineData(FileFormat.Pdf, ".pdf")]
    public void EachFormatHasOneLowerCaseExtension(FileFormat format, string extension)
    {
        Assert.Equal(extension, FileFormats.Extension(format));
    }

    [Theory]
    [InlineData(".png", FileFormat.Png)]
    [InlineData("png", FileFormat.Png)]
    [InlineData(".PNG", FileFormat.Png)]
    [InlineData(".Jpg", FileFormat.Jpg)]
    [InlineData(".JPEG", FileFormat.Jpeg)]
    [InlineData(".docx", FileFormat.Docx)]
    [InlineData("PDF", FileFormat.Pdf)]
    public void AnExtensionNamesItsFormatWhateverTheCaseOrTheDot(string extension, FileFormat expected)
    {
        Assert.True(FileFormats.TryFromExtension(extension, out var format));
        Assert.Equal(expected, format);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData(".doc")]
    [InlineData(".gif")]
    [InlineData(".png.exe")]
    [InlineData("..png")]
    [InlineData("photo.png")]
    [InlineData(".png ")]
    [InlineData("C:\\a\\b.png")]
    public void AnythingElseIsNotAFormat(string? extension)
    {
        Assert.False(FileFormats.TryFromExtension(extension, out _));
    }

    [Fact]
    public void EveryExtensionRoundTrips()
    {
        foreach (var format in FileFormats.All)
        {
            Assert.True(FileFormats.TryFromExtension(FileFormats.Extension(format), out var back));
            Assert.Equal(format, back);
        }
    }

    [Theory]
    [InlineData(FileFormat.Png, true)]
    [InlineData(FileFormat.Jpg, true)]
    [InlineData(FileFormat.Jpeg, true)]
    [InlineData(FileFormat.Docx, false)]
    [InlineData(FileFormat.Pdf, false)]
    public void OnlyPngAndJpegFamilyAreImages(FileFormat format, bool expected)
    {
        Assert.Equal(expected, FileFormats.IsImage(format));
    }

    [Fact]
    public void AFormatSharesItsEncodingWithItselfAndJpgWithJpegOnly()
    {
        foreach (var a in FileFormats.All)
        {
            foreach (var b in FileFormats.All)
            {
                var expected = a == b || (a is FileFormat.Jpg or FileFormat.Jpeg && b is FileFormat.Jpg or FileFormat.Jpeg);
                Assert.Equal(expected, FileFormats.SameEncoding(a, b));
            }
        }
    }

    [Fact]
    public void UnknownFormatsAreRejected()
    {
        var unknown = (FileFormat)99;

        Assert.Throws<ArgumentOutOfRangeException>(() => FileFormats.Extension(unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => FileFormats.IsImage(unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => FileFormats.SameEncoding(unknown, FileFormat.Png));
        Assert.Throws<ArgumentOutOfRangeException>(() => FileFormats.SameEncoding(FileFormat.Png, unknown));
    }
}
