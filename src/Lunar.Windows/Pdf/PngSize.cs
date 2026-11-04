using System.Buffers.Binary;

namespace Lunar.Windows.Pdf;

/// <summary>Reads the size a PNG says it has, from its header, so a drawn page is measured instead of assumed.</summary>
internal static class PngSize
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The width and height in pixels, or null when the stream does not start with a PNG header. The stream position is not kept.</summary>
    public static (int Width, int Height)? TryRead(Stream png)
    {
        Span<byte> header = stackalloc byte[24];
        png.Flush();
        png.Position = 0;
        var read = 0;
        while (read < header.Length)
        {
            var n = png.Read(header[read..]);
            if (n == 0)
                break;
            read += n;
        }

        if (read < header.Length || !header[..8].SequenceEqual(Signature) || !header.Slice(12, 4).SequenceEqual("IHDR"u8))
            return null;

        var width = BinaryPrimitives.ReadUInt32BigEndian(header[16..]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(header[20..]);
        return width is > 0 and <= int.MaxValue && height is > 0 and <= int.MaxValue ? ((int)width, (int)height) : null;
    }
}
