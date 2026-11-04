namespace Lunar.Domain.Conversion;

/// <summary>
/// The file formats Lunar's converter knows (PRD section 5). JPG and JPEG are two names for one encoding but stay separate
/// values because the user picks, and sees, the extension they asked for.
/// </summary>
public enum FileFormat
{
    Png,
    Jpg,
    Jpeg,
    Docx,
    Pdf
}

/// <summary>
/// Pure rules about <see cref="FileFormat"/>: the canonical extension, the format an extension names, and which formats share an
/// encoding. It looks at names only, never at a file: checking that a file really is what its extension says belongs to the
/// validation in the Conversion layer (signatures, sizes, limits).
/// </summary>
public static class FileFormats
{
    private static readonly FileFormat[] AllFormats = [FileFormat.Png, FileFormat.Jpg, FileFormat.Jpeg, FileFormat.Docx, FileFormat.Pdf];

    /// <summary>Every format, in a fixed order (PNG, JPG, JPEG, DOCX, PDF) that the matrix and its tests follow.</summary>
    public static IReadOnlyList<FileFormat> All => AllFormats;

    /// <summary>The extension written for a format, with the dot, in lower case: ".png", ".jpg", ".jpeg", ".docx", ".pdf".</summary>
    public static string Extension(FileFormat format) => Require(format) switch
    {
        FileFormat.Png => ".png",
        FileFormat.Jpg => ".jpg",
        FileFormat.Jpeg => ".jpeg",
        FileFormat.Docx => ".docx",
        _ => ".pdf"
    };

    /// <summary>
    /// The format an extension names ("png", ".PNG" and ".png" all mean PNG), or false for anything else, including nothing, a
    /// name with a path or a double extension such as ".png.exe". Only an extension is accepted, never a file name.
    /// </summary>
    public static bool TryFromExtension(string? extension, out FileFormat format)
    {
        format = default;
        if (string.IsNullOrWhiteSpace(extension))
            return false;

        var text = extension.StartsWith('.') ? extension[1..] : extension;
        foreach (var candidate in AllFormats)
        {
            if (string.Equals(text, Extension(candidate)[1..], StringComparison.OrdinalIgnoreCase))
            {
                format = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>True for PNG, JPG and JPEG: formats that hold one picture.</summary>
    public static bool IsImage(FileFormat format) => Require(format) is FileFormat.Png or FileFormat.Jpg or FileFormat.Jpeg;

    /// <summary>
    /// True when both formats store data the same way, so a "conversion" between them changes nothing but the name: a format with
    /// itself, and JPG with JPEG.
    /// </summary>
    public static bool SameEncoding(FileFormat first, FileFormat second)
    {
        Require(first);
        Require(second);
        return first == second || (first is (FileFormat.Jpg or FileFormat.Jpeg) && second is (FileFormat.Jpg or FileFormat.Jpeg));
    }

    internal static FileFormat Require(FileFormat format)
    {
        if (!Enum.IsDefined(format))
            throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown file format.");
        return format;
    }
}
