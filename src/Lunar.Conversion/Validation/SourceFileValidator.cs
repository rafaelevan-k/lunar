using System.Buffers.Binary;
using System.IO.Compression;
using Lunar.Application.Conversion.Models;
using Lunar.Domain.Conversion;
using Lunar.Domain.Shared;

namespace Lunar.Conversion.Validation;

/// <summary>What a good input looked like when it was read.</summary>
internal sealed record InspectedSource(long Length, DateTime LastWriteUtc);

/// <summary>
/// Looks at one input file the way an attacker would want it not to be looked at (PRD FR-CON-01 and FR-CON-05). It opens the
/// file read-only and reads only what it needs: the start and the end of a picture or PDF, the header chunk of a PNG, the segment
/// headers of a JPEG up to its frame, the central directory of a Word package. It does not decode, render or unpack anything.
///
/// A file passes only when its extension names a supported format, its content really is that format (not just the first bytes),
/// it is not empty, is within the size limit, is not a cloud-only placeholder, a picture is within the pixel limit, a PDF has a
/// header and an end marker, and a Word package has a sane central directory, no unsafe entry names, no macros, within the
/// archive limits, and the two parts every .docx has. Every refusal says what to do.
///
/// What this cannot see: the real number of pages, the real size of an entry after unpacking (the package declares it), whether a
/// PDF is encrypted, and whether a decoder will accept the data. The steps that decode enforce those with the limits carried in the
/// plan. Passing here is permission to try, not a promise that the conversion works.
/// </summary>
internal static class SourceFileValidator
{
    private const int HeadBytes = 1024;
    private const int EndWindowBytes = 64;
    private const int PdfEndWindowBytes = 1024;
    private const int PngHeaderBytes = 33;
    private const int MaxJpegSegments = 10_000;
    private const int MaxCentralDirectoryBytes = 16 * 1024 * 1024;
    private const long RatioCheckFromBytes = 1024 * 1024;

    private const FileAttributes Offline = (FileAttributes)0x1000;
    private const FileAttributes RecallOnOpen = (FileAttributes)0x40000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] ZipLocalHeader = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] ZipEndRecord = [0x50, 0x4B, 0x05, 0x06];
    private static readonly byte[] OleSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>Checks the file at <paramref name="path"/> (already a safe full path) as <paramref name="expected"/>.</summary>
    public static (InspectedSource? Source, ValidationFailure? Failure) Inspect(string path, FileFormat expected, ConversionLimits limits)
    {
        FileInfo info;
        try
        {
            if (Directory.Exists(path))
                return (null, NotAFile());
            info = new FileInfo(path);
            if (!info.Exists)
                return (null, NotAFile());
            if ((info.Attributes & (Offline | RecallOnOpen | RecallOnDataAccess)) != 0)
            {
                return (null, ValidationFailure.Invalid(RejectionReason.OnlineOnly,
                    "This file is stored online only. Make it available on this PC (for example \"Always keep on this device\") and try again."));
            }

            var length = info.Length;
            if (length == 0)
                return (null, ValidationFailure.Invalid(RejectionReason.Empty, "This file is empty."));
            if (length > limits.MaxFileBytes)
                return (null, ValidationFailure.Invalid(RejectionReason.TooLarge, $"This file is larger than the {FormatBytes(limits.MaxFileBytes)} limit."));

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            var failure = expected switch
            {
                FileFormat.Png => CheckPng(stream, expected, limits),
                FileFormat.Jpg or FileFormat.Jpeg => CheckJpeg(stream, expected, limits),
                FileFormat.Pdf => CheckPdf(stream, expected),
                _ => CheckDocx(stream, limits)
            };

            return failure is null ? (new InspectedSource(length, info.LastWriteTimeUtc), null) : (null, failure);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return (null, ValidationFailure.Invalid(RejectionReason.Unreadable,
                "Lunar can't read this file. Close any program that is using it, check that you may open it, and try again."));
        }
    }

    // ---- PNG ---------------------------------------------------------------------------------------------------------

    private static ValidationFailure? CheckPng(FileStream stream, FileFormat expected, ConversionLimits limits)
    {
        Span<byte> header = stackalloc byte[PngHeaderBytes];
        var read = ReadAt(stream, 0, header);
        if (read < PngSignature.Length || !header[..PngSignature.Length].SequenceEqual(PngSignature))
            return Mismatch(stream, expected);
        if (read < PngHeaderBytes)
            return Damaged(expected);

        // The first chunk must be IHDR, 13 bytes long, with a correct checksum.
        if (BinaryPrimitives.ReadUInt32BigEndian(header[8..]) != 13 || !header.Slice(12, 4).SequenceEqual("IHDR"u8))
            return Damaged(expected);
        if (BinaryPrimitives.ReadUInt32BigEndian(header[29..]) != Crc32.Compute(header.Slice(12, 17)))
            return Damaged(expected);

        var width = BinaryPrimitives.ReadUInt32BigEndian(header[16..]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(header[20..]);
        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
            return Damaged(expected);
        if (!ValidPngFormat(header[24], header[25]) || header[26] != 0 || header[27] != 0 || header[28] > 1)
            return Damaged(expected);

        if ((long)width * height > limits.MaxImagePixels)
            return TooManyPixels(limits);

        return TailContains(stream, "IEND"u8, EndWindowBytes) ? null : Damaged(expected);
    }

    private static bool ValidPngFormat(byte bitDepth, byte colorType) => colorType switch
    {
        0 => bitDepth is 1 or 2 or 4 or 8 or 16,
        2 or 4 or 6 => bitDepth is 8 or 16,
        3 => bitDepth is 1 or 2 or 4 or 8,
        _ => false
    };

    // ---- JPEG --------------------------------------------------------------------------------------------------------

    private static ValidationFailure? CheckJpeg(FileStream stream, FileFormat expected, ConversionLimits limits)
    {
        Span<byte> start = stackalloc byte[3];
        if (ReadAt(stream, 0, start) < 3 || start[0] != 0xFF || start[1] != 0xD8 || start[2] != 0xFF)
            return Mismatch(stream, expected);

        Span<byte> lengthBytes = stackalloc byte[2];
        Span<byte> frame = stackalloc byte[6];
        stream.Position = 2;
        for (var segment = 0; segment < MaxJpegSegments; segment++)
        {
            if (stream.ReadByte() != 0xFF)
                return Damaged(expected);

            int marker;
            do
            {
                marker = stream.ReadByte();
            }
            while (marker == 0xFF);

            if (marker < 0)
                return Damaged(expected);
            if (marker is 0x00 or 0x01 or 0xD8 || marker is >= 0xD0 and <= 0xD7)
                continue; // markers that carry no length
            if (marker is 0xD9 or 0xDA)
                return Damaged(expected); // the picture ended or its data began without a frame header

            if (stream.Read(lengthBytes) < 2)
                return Damaged(expected);
            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);
            if (segmentLength < 2)
                return Damaged(expected);

            var isFrame = marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
            if (isFrame)
            {
                if (segmentLength < 8 || stream.Read(frame) < 6)
                    return Damaged(expected);

                var height = BinaryPrimitives.ReadUInt16BigEndian(frame[1..]);
                var width = BinaryPrimitives.ReadUInt16BigEndian(frame[3..]);
                if (width == 0 || height == 0 || frame[5] == 0)
                    return Damaged(expected);
                if ((long)width * height > limits.MaxImagePixels)
                    return TooManyPixels(limits);

                return TailContains(stream, [0xFF, 0xD9], EndWindowBytes) ? null : Damaged(expected);
            }

            var next = stream.Position + segmentLength - 2;
            if (next > stream.Length)
                return Damaged(expected);
            stream.Position = next;
        }

        return Damaged(expected);
    }

    // ---- PDF ---------------------------------------------------------------------------------------------------------

    private static ValidationFailure? CheckPdf(FileStream stream, FileFormat expected)
    {
        var head = new byte[(int)Math.Min(stream.Length, HeadBytes)];
        ReadAt(stream, 0, head);
        var at = head.AsSpan().IndexOf("%PDF-"u8);
        if (at < 0)
            return Mismatch(stream, expected, head);

        // "%PDF-" then a version such as 1.7 or 2.0.
        var version = head.AsSpan(at + 5);
        if (version.Length < 3 || !char.IsAsciiDigit((char)version[0]) || version[1] != (byte)'.' || !char.IsAsciiDigit((char)version[2]))
            return Damaged(expected);

        return TailContains(stream, "%%EOF"u8, PdfEndWindowBytes) ? null : Damaged(expected);
    }

    // ---- DOCX --------------------------------------------------------------------------------------------------------

    private static ValidationFailure? CheckDocx(FileStream stream, ConversionLimits limits)
    {
        var expected = FileFormat.Docx;
        Span<byte> start = stackalloc byte[8];
        var read = ReadAt(stream, 0, start);
        if (read >= OleSignature.Length && start.SequenceEqual(OleSignature))
        {
            return ValidationFailure.Invalid(RejectionReason.EncryptedOrLegacy,
                "This looks like an old Word file (.doc) or a password-protected document, not a regular .docx. Open it in Word, remove the password or save it as .docx, and try again.");
        }

        if (read < ZipLocalHeader.Length || !start[..ZipLocalHeader.Length].SequenceEqual(ZipLocalHeader))
            return Mismatch(stream, expected);

        // Read the end-of-central-directory record by hand first, so a package that claims millions of entries is refused before
        // the framework reads (and allocates) its whole directory.
        var tailLength = (int)Math.Min(stream.Length, 65_557);
        var tail = new byte[tailLength];
        ReadAt(stream, stream.Length - tailLength, tail);
        var end = tail.AsSpan().LastIndexOf(ZipEndRecord);
        if (end < 0 || end + 22 > tailLength)
            return Damaged(expected);

        var entryCount = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 10));
        var directorySize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 12));
        var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 16));
        if (entryCount == 0xFFFF || directorySize == 0xFFFFFFFF || directoryOffset == 0xFFFFFFFF)
            return ArchiveTooBig("it uses an extended package format");
        if (entryCount == 0)
            return Damaged(expected);
        if (entryCount > limits.MaxArchiveEntries)
            return ArchiveTooBig($"it has more than {limits.MaxArchiveEntries} parts");
        if (directorySize > MaxCentralDirectoryBytes)
            return ArchiveTooBig("its table of contents is too large");
        if ((long)directoryOffset + directorySize > stream.Length)
            return Damaged(expected);

        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            return CheckPackageEntries(archive, limits);
        }
        catch (InvalidDataException)
        {
            return Damaged(expected);
        }
    }

    private static ValidationFailure? CheckPackageEntries(ZipArchive archive, ConversionLimits limits)
    {
        if (archive.Entries.Count > limits.MaxArchiveEntries)
            return ArchiveTooBig($"it has more than {limits.MaxArchiveEntries} parts");

        var hasContentTypes = false;
        var hasDocument = false;
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (name.Length == 0 || name.Length > 512 || name[0] == '/' || name.Contains('\\', StringComparison.Ordinal)
                || name.Contains(':', StringComparison.Ordinal) || name.Split('/').Contains(".."))
            {
                return ValidationFailure.Invalid(RejectionReason.Corrupt, "This Word file has a part with an unsafe name, so Lunar won't open it. It may be damaged or not a real .docx.");
            }

            if (name.Contains("vbaProject", StringComparison.OrdinalIgnoreCase))
            {
                return ValidationFailure.Invalid(RejectionReason.HasMacros,
                    "This Word file contains macros, which a regular .docx can't have. Lunar never runs macros. Save it as a plain .docx without macros and try again.");
            }

            if (name.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase))
                hasContentTypes = true;
            else if (name.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase))
                hasDocument = true;

            var size = entry.Length;
            if (size > limits.MaxArchiveEntryBytes)
                return ArchiveTooBig("one of its parts is too large once unpacked");
            if (size >= RatioCheckFromBytes && (entry.CompressedLength <= 0 || size / entry.CompressedLength > limits.MaxArchiveCompressionRatio))
                return ArchiveTooBig("one of its parts is compressed far more than a real document is");

            total += size;
            if (total > limits.MaxArchiveUncompressedBytes)
                return ArchiveTooBig("it is too large once unpacked");
        }

        return hasContentTypes && hasDocument
            ? null
            : ValidationFailure.Invalid(RejectionReason.SignatureMismatch, "This file is named .docx but isn't a Word document. Check that you picked the right file.");
    }

    // ---- shared pieces -----------------------------------------------------------------------------------------------

    // The end marker must be among the last bytes: a file cut short does not have it. The window tolerates padding some writers
    // leave behind (a PDF may carry up to a kilobyte after its end marker).
    private static bool TailContains(FileStream stream, ReadOnlySpan<byte> marker, int windowBytes)
    {
        Span<byte> tail = stackalloc byte[PdfEndWindowBytes];
        var length = (int)Math.Min(stream.Length, windowBytes);
        var read = ReadAt(stream, stream.Length - length, tail[..length]);
        return tail[..read].IndexOf(marker) >= 0;
    }

    private static int ReadAt(Stream stream, long offset, Span<byte> buffer)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        var total = 0;
        while (total < buffer.Length)
        {
            var n = stream.Read(buffer[total..]);
            if (n == 0)
                break;
            total += n;
        }

        return total;
    }

    // The extension says one thing and the content another. Say which, so the user knows what the file really is.
    private static ValidationFailure Mismatch(FileStream stream, FileFormat expected, byte[]? knownHead = null)
    {
        var head = knownHead;
        if (head is null)
        {
            head = new byte[(int)Math.Min(stream.Length, 16)];
            ReadAt(stream, 0, head);
        }

        string? actual = null;
        if (head.AsSpan().StartsWith(PngSignature))
            actual = "a PNG picture";
        else if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
            actual = "a JPEG picture";
        else if (head.AsSpan().StartsWith("%PDF-"u8))
            actual = "a PDF";
        else if (head.AsSpan().StartsWith(ZipLocalHeader))
            actual = "a zip-based file";
        else if (head.AsSpan().StartsWith(OleSignature))
            actual = "an old Office file";

        var wanted = FileFormats.Extension(expected);
        var message = actual is null
            ? $"This file is named {wanted} but its content isn't a valid {FormatName(expected)}. It may be damaged or have the wrong extension."
            : $"This file is named {wanted} but its content looks like {actual}. Rename it to the right extension or pick another file.";
        return ValidationFailure.Invalid(RejectionReason.SignatureMismatch, message);
    }

    private static ValidationFailure Damaged(FileFormat expected) =>
        ValidationFailure.Invalid(RejectionReason.Corrupt, $"This {FormatName(expected)} file is damaged or cut short. Try getting a fresh copy of it.");

    private static ValidationFailure TooManyPixels(ConversionLimits limits) =>
        ValidationFailure.Invalid(RejectionReason.TooManyPixels, $"This picture is larger than {limits.MaxImagePixels / 1_000_000} megapixels, which is more than Lunar converts.");

    private static ValidationFailure ArchiveTooBig(string detail) =>
        ValidationFailure.Invalid(RejectionReason.ArchiveLimits, $"This Word file is beyond what Lunar converts safely ({detail}).");

    private static ValidationFailure NotAFile() =>
        ValidationFailure.Invalid(RejectionReason.NotAFile, "Lunar can't find this file. It may have been moved or deleted, or it may be a folder.");

    private static string FormatName(FileFormat format) => format switch
    {
        FileFormat.Png => "PNG",
        FileFormat.Jpg or FileFormat.Jpeg => "JPEG",
        FileFormat.Docx => "Word",
        _ => "PDF"
    };

    private static string FormatBytes(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):0.#} GB" : $"{bytes / (1024.0 * 1024):0.#} MB";
}
