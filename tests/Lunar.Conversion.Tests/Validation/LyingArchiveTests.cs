using System.IO.Compression;
using System.Text;
using Lunar.Conversion.Tests.Support;
using Xunit;

namespace Lunar.Conversion.Tests.Validation;

// BE-28: the Word package checks trust the sizes a package declares (SourceFileValidator reads them from the central directory). That is only
// safe if the runtime never hands out more bytes than an entry declared. This test pins that platform behavior with a package whose declared size
// is a lie: if a future runtime stopped enforcing it, Lunar's own reading of relationship parts (LibreOfficeRendererProvider.LinksOutside)
// could be made to read far more than the checked limit, and this test would fail first.
public sealed class LyingArchiveTests
{
    private const string Part = "word/_rels/document.xml.rels";

    private static byte[] PackageWithALyingPart(int realBytes, int declaredBytes)
    {
        var bytes = Fixtures.Docx((Part, Encoding.ASCII.GetBytes(new string(' ', realBytes))));

        // Find the central-directory record of the part and overwrite its declared uncompressed size (offset 24 of the record).
        var name = Encoding.ASCII.GetBytes(Part);
        for (var i = 0; i < bytes.Length - 46; i++)
        {
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 0x01 && bytes[i + 3] == 0x02
                && bytes.AsSpan(i + 46, name.Length).SequenceEqual(name))
            {
                BitConverter.TryWriteBytes(bytes.AsSpan(i + 24), (uint)declaredBytes);
                return bytes;
            }
        }

        throw new InvalidOperationException("The part's record was not found.");
    }

    [Fact]
    public void ARuntimeReadOfAPartThatDeclaresLessThanItHoldsNeverReturnsMoreThanItDeclared()
    {
        var package = PackageWithALyingPart(realBytes: 4 * 1024 * 1024, declaredBytes: 100);
        using var archive = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        var entry = archive.GetEntry(Part)!;
        Assert.Equal(100, entry.Length); // the lie is in place

        long read = 0;
        try
        {
            using var stream = entry.Open();
            var buffer = new byte[8192];
            int n;
            while ((n = stream.Read(buffer)) > 0)
                read += n;
        }
        catch (InvalidDataException)
        {
            // refusing the part is just as safe
        }

        Assert.True(read <= 100, $"The runtime returned {read} bytes for a part that declared 100.");
    }
}
