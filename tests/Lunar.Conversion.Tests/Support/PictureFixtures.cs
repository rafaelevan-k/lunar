using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Lunar.Conversion.Validation;
using SkiaSharp;

namespace Lunar.Conversion.Tests.Support;

/// <summary>Synthetic pictures with known pixels, built byte by byte so the expected result does not depend on the library under test.</summary>
internal static class PictureFixtures
{
    internal static readonly (byte R, byte G, byte B) Red = (255, 0, 0);
    internal static readonly (byte R, byte G, byte B) Green = (0, 255, 0);
    internal static readonly (byte R, byte G, byte B) Blue = (0, 0, 255);
    internal static readonly (byte R, byte G, byte B) Yellow = (255, 255, 0);

    /// <summary>A PNG with any colour type and bit depth, from already packed rows (no filtering), written with a real zlib stream.</summary>
    public static byte[] Png(int width, int height, byte colorType, byte bitDepth, Func<int, byte[]> row, byte[]? palette = null, byte[]? transparency = null)
    {
        using var stream = new MemoryStream();
        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = bitDepth;
        header[9] = colorType;
        stream.Write(Chunk("IHDR", header));
        if (palette is not null)
            stream.Write(Chunk("PLTE", palette));
        if (transparency is not null)
            stream.Write(Chunk("tRNS", transparency));

        using var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0); // filter: none
            raw.Write(row(y));
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw.ToArray());
        stream.Write(Chunk("IDAT", compressed.ToArray()));
        stream.Write(Chunk("IEND", []));
        return stream.ToArray();
    }

    /// <summary>8-bit RGBA, one colour per pixel chosen by the function.</summary>
    public static byte[] Rgba(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> pixel) =>
        Png(width, height, colorType: 6, bitDepth: 8, row: y =>
        {
            var bytes = new byte[width * 4];
            for (var x = 0; x < width; x++)
            {
                var (r, g, b, a) = pixel(x, y);
                bytes[x * 4] = r;
                bytes[(x * 4) + 1] = g;
                bytes[(x * 4) + 2] = b;
                bytes[(x * 4) + 3] = a;
            }

            return bytes;
        });

    /// <summary>8-bit RGB with no alpha channel at all.</summary>
    public static byte[] Rgb(int width, int height, Func<int, int, (byte R, byte G, byte B)> pixel) =>
        Png(width, height, colorType: 2, bitDepth: 8, row: y =>
        {
            var bytes = new byte[width * 3];
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = pixel(x, y);
                bytes[x * 3] = r;
                bytes[(x * 3) + 1] = g;
                bytes[(x * 3) + 2] = b;
            }

            return bytes;
        });

    /// <summary>A picture of four flat quadrants: top left red, top right green, bottom left blue, bottom right yellow.</summary>
    public static (byte R, byte G, byte B) QuadrantColor(int x, int y, int width, int height) =>
        (x < width / 2, y < height / 2) switch
        {
            (true, true) => Red,
            (false, true) => Green,
            (true, false) => Blue,
            _ => Yellow
        };

    /// <summary>The four quadrants as a JPEG at quality 100 with an EXIF orientation tag (1 to 8), or none when <paramref name="orientation"/> is 0.</summary>
    public static byte[] QuadrantJpeg(int width, int height, int orientation)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = QuadrantColor(x, y, width, height);
                bitmap.SetPixel(x, y, new SKColor(r, g, b));
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 100);
        var jpeg = data.ToArray();
        return orientation == 0 ? jpeg : WithOrientation(jpeg, orientation);
    }

    /// <summary>Inserts an EXIF segment holding only the orientation tag right after the start of the JPEG.</summary>
    public static byte[] WithOrientation(byte[] jpeg, int orientation)
    {
        byte[] payload =
        [
            (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0,
            1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0, 0, 0, 0, 0
        ];
        using var stream = new MemoryStream();
        stream.Write(jpeg.AsSpan(0, 2));
        stream.Write([0xFF, 0xE1, 0x00, (byte)(payload.Length + 2)]);
        stream.Write(payload);
        stream.Write(jpeg.AsSpan(2));
        return stream.ToArray();
    }

    public static byte[] SkiaJpeg(int width, int height, Func<int, int, (byte R, byte G, byte B)> pixel, int quality = 95)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = pixel(x, y);
                bitmap.SetPixel(x, y, new SKColor(r, g, b));
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }

    public static SKColor PixelAt(string path, int x, int y)
    {
        using var bitmap = SKBitmap.Decode(path) ?? throw new InvalidOperationException("The output could not be decoded.");
        return bitmap.GetPixel(x, y);
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), Crc32.Compute(chunk.AsSpan(4, 4 + data.Length)));
        return chunk;
    }
}
