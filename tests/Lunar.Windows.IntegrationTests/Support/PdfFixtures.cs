using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Lunar.Windows.IntegrationTests.Support;

/// <summary>
/// Small PDFs written by hand, so the pages, sizes and colours are known exactly and nothing but the operating system's renderer is under
/// test. Each page is one flat colour with a black square in its bottom left corner (so a wrongly turned page shows). A PDF can be
/// protected with the standard security handler (RC4, 128 bit, revision 3) by a user password, written from the algorithms of the PDF
/// specification, to test password handling without any PDF tool.
/// </summary>
internal static class PdfFixtures
{
    private static readonly byte[] PasswordPad =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A
    ];

    public static (byte R, byte G, byte B) PageColor(int pageIndex) =>
        ((byte)(40 + ((pageIndex * 53) % 200)), (byte)(60 + ((pageIndex * 97) % 180)), (byte)(200 - ((pageIndex * 31) % 150)));

    /// <summary>A PDF of <paramref name="pages"/> pages, each <paramref name="width"/> by <paramref name="height"/> points unless a size is given for the page.</summary>
    public static byte[] Build(int pages, double width = 612, double height = 792, Func<int, (double Width, double Height)>? size = null, string? userPassword = null)
    {
        var objects = new List<byte[]>();      // index 0 is object 1
        var encryption = userPassword is null ? null : new Rc4Protection(userPassword);

        // 1 catalog, 2 page tree, then a page and its content for every page, then (when protected) the encryption dictionary.
        var kids = string.Join(' ', Enumerable.Range(0, pages).Select(i => $"{3 + (2 * i)} 0 R"));
        objects.Add(Ascii("<< /Type /Catalog /Pages 2 0 R >>"));
        objects.Add(Ascii($"<< /Type /Pages /Kids [{kids}] /Count {pages} >>"));
        for (var i = 0; i < pages; i++)
        {
            var (w, h) = size?.Invoke(i) ?? (width, height);
            var (r, g, b) = PageColor(i);
            var content = Ascii(string.Create(CultureInfo.InvariantCulture,
                $"{r / 255.0:0.###} {g / 255.0:0.###} {b / 255.0:0.###} rg 0 0 {w:0.###} {h:0.###} re f 0 0 0 rg 0 0 {w / 4:0.###} {h / 4:0.###} re f"));
            var pageObject = 3 + (2 * i);
            var contentObject = pageObject + 1;
            objects.Add(Ascii(string.Create(CultureInfo.InvariantCulture,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w:0.###} {h:0.###}] /Contents {contentObject} 0 R >>")));
            var stored = encryption is null ? content : encryption.Encrypt(content, contentObject);
            objects.Add(Concat(Ascii($"<< /Length {stored.Length} >>\nstream\n"), stored, Ascii("\nendstream")));
        }

        if (encryption is not null)
            objects.Add(Ascii(encryption.Dictionary()));

        using var file = new MemoryStream();
        Write(file, "%PDF-1.7\n%âãÏÓ\n");
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(file.Position);
            Write(file, $"{i + 1} 0 obj\n");
            file.Write(objects[i]);
            Write(file, "\nendobj\n");
        }

        var xref = file.Position;
        Write(file, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            Write(file, string.Create(CultureInfo.InvariantCulture, $"{offset:0000000000} 00000 n \n"));
        var trailer = $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R";
        if (encryption is not null)
            trailer += $" /Encrypt {objects.Count} 0 R /ID [<{Convert.ToHexString(encryption.Id)}> <{Convert.ToHexString(encryption.Id)}>]";
        Write(file, trailer + $" >>\nstartxref\n{xref}\n%%EOF\n");
        return file.ToArray();
    }

    private static void Write(Stream stream, string text) => stream.Write(Encoding.Latin1.GetBytes(text));

    private static byte[] Ascii(string text) => Encoding.Latin1.GetBytes(text);

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    // Standard security handler, version 2, revision 3, 128-bit RC4 (PDF 1.7 specification, algorithms 1 to 5).
    private sealed class Rc4Protection
    {
        private const int Permissions = -4;
        private readonly byte[] _key;
        private readonly byte[] _o;
        private readonly byte[] _u;

        public Rc4Protection(string userPassword)
        {
            Id = MD5.HashData(Encoding.ASCII.GetBytes("lunar-test-" + userPassword));
            var user = Pad(userPassword);
            _o = ComputeO(Pad("owner-" + userPassword), user);

            // algorithm 2: the encryption key
            var data = new List<byte>(user);
            data.AddRange(_o);
            data.AddRange(BitConverter.GetBytes(Permissions));
            data.AddRange(Id);
            var hash = MD5.HashData([.. data]);
            for (var i = 0; i < 50; i++)
                hash = MD5.HashData(hash.AsSpan(0, 16));
            _key = hash[..16];

            // algorithm 5: the user entry
            var u = Rc4(_key, MD5.HashData([.. PasswordPad, .. Id]));
            for (var i = 1; i <= 19; i++)
                u = Rc4(_key.Select(b => (byte)(b ^ i)).ToArray(), u);
            _u = [.. u, .. new byte[16]];
        }

        public byte[] Id { get; }

        public string Dictionary() =>
            string.Create(CultureInfo.InvariantCulture,
                $"<< /Filter /Standard /V 2 /R 3 /Length 128 /P {Permissions} /O <{Convert.ToHexString(_o)}> /U <{Convert.ToHexString(_u)}> >>");

        // algorithm 1: a key for each object
        public byte[] Encrypt(byte[] data, int objectNumber)
        {
            byte[] extra = [(byte)objectNumber, (byte)(objectNumber >> 8), (byte)(objectNumber >> 16), 0, 0];
            var objectKey = MD5.HashData([.. _key, .. extra])[..16];
            return Rc4(objectKey, data);
        }

        private static byte[] ComputeO(byte[] ownerPadded, byte[] userPadded)
        {
            var hash = MD5.HashData(ownerPadded);
            for (var i = 0; i < 50; i++)
                hash = MD5.HashData(hash);
            var key = hash[..16];
            var o = Rc4(key, userPadded);
            for (var i = 1; i <= 19; i++)
                o = Rc4(key.Select(b => (byte)(b ^ i)).ToArray(), o);
            return o;
        }

        private static byte[] Pad(string password)
        {
            var bytes = Encoding.Latin1.GetBytes(password);
            return [.. bytes.Concat(PasswordPad).Take(32)];
        }

        private static byte[] Rc4(byte[] key, byte[] data)
        {
            var s = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
            var j = 0;
            for (var i = 0; i < 256; i++)
            {
                j = (j + s[i] + key[i % key.Length]) & 0xFF;
                (s[i], s[j]) = (s[j], s[i]);
            }

            var output = new byte[data.Length];
            int a = 0, b = 0;
            for (var n = 0; n < data.Length; n++)
            {
                a = (a + 1) & 0xFF;
                b = (b + s[a]) & 0xFF;
                (s[a], s[b]) = (s[b], s[a]);
                output[n] = (byte)(data[n] ^ s[(s[a] + s[b]) & 0xFF]);
            }

            return output;
        }
    }
}
