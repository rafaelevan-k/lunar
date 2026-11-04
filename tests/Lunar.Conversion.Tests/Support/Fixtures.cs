using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Lunar.Conversion.Validation;

namespace Lunar.Conversion.Tests.Support;

/// <summary>Small synthetic files for the validator: just enough structure to be valid, or broken in exactly one way.</summary>
internal static class Fixtures
{
    public static byte[] Png(uint width = 4, uint height = 4, bool endChunk = true, bool badCrc = false, byte bitDepth = 8, byte colorType = 6)
    {
        using var stream = new MemoryStream();
        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), height);
        header[8] = bitDepth;
        header[9] = colorType;
        stream.Write(Chunk("IHDR", header, badCrc));
        stream.Write(Chunk("IDAT", [0x78, 0x9C, 0x63, 0x60, 0x00, 0x00, 0x00, 0x02, 0x00, 0x01], badCrc: false));
        if (endChunk)
            stream.Write(Chunk("IEND", [], badCrc: false));
        return stream.ToArray();
    }

    public static byte[] Jpeg(ushort width = 4, ushort height = 4, bool frame = true, bool endMarker = true)
    {
        using var stream = new MemoryStream();
        stream.Write([0xFF, 0xD8]);
        stream.Write([0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00]);
        if (frame)
        {
            stream.Write([0xFF, 0xC0, 0x00, 0x11, 0x08]);
            stream.WriteByte((byte)(height >> 8));
            stream.WriteByte((byte)height);
            stream.WriteByte((byte)(width >> 8));
            stream.WriteByte((byte)width);
            stream.Write([0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01]);
        }

        stream.Write([0xFF, 0xDA, 0x00, 0x0C, 0x03, 0x01, 0x00, 0x02, 0x11, 0x03, 0x11, 0x00, 0x3F, 0x00]);
        stream.Write(new byte[40]);
        if (endMarker)
            stream.Write([0xFF, 0xD9]);
        return stream.ToArray();
    }

    public static byte[] Pdf(int pages = 1, bool header = true, bool endMarker = true, string prefix = "")
    {
        var text = new StringBuilder(prefix);
        if (header)
            text.Append("%PDF-1.7\n");
        text.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        for (var page = 0; page < pages; page++)
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{3 + page} 0 obj\n<< /Type /Page /Parent 2 0 R >>\nendobj\n");
        text.Append("trailer\n<< /Root 1 0 R >>\nstartxref\n0\n");
        if (endMarker)
            text.Append("%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    public static byte[] Docx(params (string Name, byte[] Data)[] extra) => Docx(includeDocument: true, includeContentTypes: true, extra);

    public static byte[] Docx(bool includeDocument, bool includeContentTypes, params (string Name, byte[] Data)[] extra)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (includeContentTypes)
                Add(zip, "[Content_Types].xml", "<Types/>"u8.ToArray());
            if (includeDocument)
                Add(zip, "word/document.xml", "<document/>"u8.ToArray());
            foreach (var (name, data) in extra)
                Add(zip, name, data);
        }

        return stream.ToArray();
    }

    /// <summary>A part that is tiny when compressed and huge when unpacked.</summary>
    public static (string, byte[]) Bomb(int megabytes) => ("word/media/bomb.bin", new byte[megabytes * 1024 * 1024]);

    public static byte[] OleDocument()
    {
        var bytes = new byte[600];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(bytes, 0);
        return bytes;
    }

    private static void Add(ZipArchive zip, string name, byte[] data)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        entryStream.Write(data);
    }

    private static byte[] Chunk(string type, byte[] data, bool badCrc)
    {
        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        var crc = Crc32.Compute(chunk.AsSpan(4, 4 + data.Length));
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), badCrc ? crc ^ 0xDEADBEEF : crc);
        return chunk;
    }
}
