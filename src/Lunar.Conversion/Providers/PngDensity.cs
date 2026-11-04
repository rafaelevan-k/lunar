using System.Buffers.Binary;
using Lunar.Conversion.Validation;

namespace Lunar.Conversion.Providers;

/// <summary>
/// Writes the resolution a PNG should be shown at into the file (the "pHYs" chunk), replacing whatever it said before. The operating
/// system's renderer stamps the resolution of the screen it happened to run on (for example 120 pixels per inch), which would make a
/// 150 DPI page come out the wrong size in an editor or a printer; the pixels themselves are never touched. Every other chunk is copied
/// byte for byte.
/// </summary>
internal static class PngDensity
{
    private const double InchesPerMetre = 39.37007874015748;

    /// <summary>Pixels per metre for a resolution in pixels per inch, as a PNG stores it (72 gives 2835, 150 gives 5906, 300 gives 11811).</summary>
    public static uint PixelsPerMetre(int dpi) => (uint)Math.Round(dpi * InchesPerMetre);

    /// <summary>The PNG with its resolution set to <paramref name="dpi"/>. Throws <see cref="InvalidDataException"/> if the data is not a well-formed PNG.</summary>
    public static byte[] WithDensity(ReadOnlySpan<byte> png, int dpi)
    {
        if (dpi is < 1 or > 4800)
            throw new ArgumentOutOfRangeException(nameof(dpi), dpi, "A resolution runs from 1 to 4800 pixels per inch.");
        if (png.Length < 8 + 25 || !png[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            throw new InvalidDataException("Not a PNG.");

        using var output = new MemoryStream(png.Length + 21);
        output.Write(png[..8]);
        var position = 8;
        var wrote = false;
        while (position < png.Length)
        {
            if (png.Length - position < 12)
                throw new InvalidDataException("A PNG chunk is cut short.");
            var length = BinaryPrimitives.ReadUInt32BigEndian(png[position..]);
            if (length > int.MaxValue || position + 12L + length > png.Length)
                throw new InvalidDataException("A PNG chunk is longer than the file.");
            var chunk = png.Slice(position, 12 + (int)length);
            var type = chunk.Slice(4, 4);

            if (!type.SequenceEqual("pHYs"u8))
                output.Write(chunk);
            if (!wrote && type.SequenceEqual("IHDR"u8))
            {
                output.Write(Density(dpi));
                wrote = true;
            }

            position += chunk.Length;
        }

        if (!wrote)
            throw new InvalidDataException("A PNG has no header chunk.");
        return output.ToArray();
    }

    private static byte[] Density(int dpi)
    {
        var chunk = new byte[21];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, 9);
        "pHYs"u8.CopyTo(chunk.AsSpan(4));
        var perMetre = PixelsPerMetre(dpi);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8), perMetre);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(12), perMetre);
        chunk[16] = 1; // the unit is the metre
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(17), Crc32.Compute(chunk.AsSpan(4, 13)));
        return chunk;
    }
}
