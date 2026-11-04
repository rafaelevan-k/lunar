using System.Buffers.Binary;
using System.Text;
using Lunar.Conversion.Providers;
using Lunar.Conversion.Tests.Support;
using Lunar.Conversion.Validation;
using SkiaSharp;
using Xunit;

namespace Lunar.Conversion.Tests.Providers;

// BE-24: the resolution written into a PNG.
public sealed class PngDensityTests
{
    private static List<(string Type, byte[] Data)> Chunks(byte[] png)
    {
        var list = new List<(string, byte[])>();
        var position = 8;
        while (position + 12 <= png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(position));
            var type = Encoding.ASCII.GetString(png, position + 4, 4);
            var data = png.AsSpan(position + 8, length).ToArray();
            var crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(position + 8 + length));
            Assert.Equal(Crc32.Compute(png.AsSpan(position + 4, 4 + length)), crc); // every chunk keeps a correct checksum
            list.Add((type, data));
            position += 12 + length;
        }

        return list;
    }

    [Theory]
    [InlineData(72, 2835u)]
    [InlineData(96, 3780u)]
    [InlineData(150, 5906u)]
    [InlineData(300, 11811u)]
    [InlineData(600, 23622u)]
    public void ThePixelsPerMetreAreTheRoundedResolutionInPixelsPerInch(int dpi, uint perMetre)
    {
        Assert.Equal(perMetre, PngDensity.PixelsPerMetre(dpi));
    }

    [Fact]
    public void TheChunkIsInsertedRightAfterTheHeaderAndTheRestIsUntouched()
    {
        var png = Fixtures.Png(8, 8);

        var result = PngDensity.WithDensity(png, 150);

        var before = Chunks(png);
        var after = Chunks(result);
        Assert.Equal(before.Count + 1, after.Count);
        Assert.Equal(["IHDR", "pHYs", "IDAT", "IEND"], after.Select(c => c.Type));
        Assert.Equal(before.Select(c => (c.Type, Convert.ToHexString(c.Data))), after.Where(c => c.Type != "pHYs").Select(c => (c.Type, Convert.ToHexString(c.Data))));
        var density = after[1].Data;
        Assert.Equal(5906u, BinaryPrimitives.ReadUInt32BigEndian(density));
        Assert.Equal(5906u, BinaryPrimitives.ReadUInt32BigEndian(density.AsSpan(4)));
        Assert.Equal(1, density[8]);
    }

    [Fact]
    public void AnExistingResolutionIsReplacedNotAdded()
    {
        var once = PngDensity.WithDensity(Fixtures.Png(8, 8), 120);

        var twice = PngDensity.WithDensity(once, 300);

        Assert.Single(Chunks(twice), c => c.Type == "pHYs");
        Assert.Equal(11811u, BinaryPrimitives.ReadUInt32BigEndian(Chunks(twice).Single(c => c.Type == "pHYs").Data));
    }

    [Fact]
    public void AValidPictureStillDecodesToTheSamePixels()
    {
        var png = PictureFixtures.Rgba(32, 16, (x, y) => ((byte)(x * 8), (byte)(y * 16), 99, (byte)(x < 16 ? 255 : 128)));

        var result = PngDensity.WithDensity(png, 300);

        using var before = SKBitmap.Decode(png);
        using var after = SKBitmap.Decode(result);
        Assert.Equal((before.Width, before.Height), (after.Width, after.Height));
        for (var y = 0; y < before.Height; y++)
        {
            for (var x = 0; x < before.Width; x++)
                Assert.Equal(before.GetPixel(x, y), after.GetPixel(x, y));
        }
    }

    [Fact]
    public void ThePassedDataIsNotChanged()
    {
        var png = Fixtures.Png(8, 8);
        var copy = png.ToArray();

        PngDensity.WithDensity(png, 72);

        Assert.Equal(copy, png);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4801)]
    public void AnImpossibleResolutionIsRefused(int dpi)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PngDensity.WithDensity(Fixtures.Png(), dpi));
    }

    [Fact]
    public void DataThatIsNotAWellFormedPngIsRefused()
    {
        var good = Fixtures.Png();
        var cases = new[]
        {
            Array.Empty<byte>(),
            "not a png at all, just text of some length ..............."u8.ToArray(),
            good[..20],
            good[..(good.Length - 5)],
            [.. good[..8], .. new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, .. good[12..]]
        };

        foreach (var bytes in cases)
            Assert.Throws<InvalidDataException>(() => PngDensity.WithDensity(bytes, 150));
    }
}
