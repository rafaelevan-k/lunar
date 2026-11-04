namespace Lunar.Application.Conversion.Models;

/// <summary>
/// The caps that keep an untrusted file from exhausting memory, disk or time (PRD FR-CON-05). They are checked while planning
/// where a cheap read of the file can show a breach (size, picture size, archive shape, path length) and are carried in the plan so
/// the steps that run later enforce the rest where only they can (the real number of pages, the real size after unpacking).
/// The defaults are conservative proposals for the user to confirm; every value can be changed by the caller.
/// </summary>
public sealed record ConversionLimits
{
    /// <summary>The longest name of one file or folder (NTFS limit).</summary>
    public const int MaxNameChars = 255;

    /// <summary>Files in one request.</summary>
    public int MaxFilesPerRequest { get; init; } = 100;

    /// <summary>Bytes of one input file.</summary>
    public long MaxFileBytes { get; init; } = 200L * 1024 * 1024;

    /// <summary>Pixels (width times height) of one input picture; a 50-million-pixel picture needs about 200 MB once decoded.</summary>
    public long MaxImagePixels { get; init; } = 50_000_000;

    /// <summary>Pages of one PDF or DOCX that may be converted. Checked against the real page count by the step that knows it.</summary>
    public int MaxPages { get; init; } = 200;

    /// <summary>Entries in a DOCX package.</summary>
    public int MaxArchiveEntries { get; init; } = 2_000;

    /// <summary>Bytes of a DOCX package once unpacked, as the package declares them.</summary>
    public long MaxArchiveUncompressedBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>Bytes of any one entry of a DOCX package once unpacked.</summary>
    public long MaxArchiveEntryBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>
    /// How many times larger than its compressed size an entry may be (applied to entries of 1 MiB and more; small entries
    /// compress very well without being a threat).
    /// </summary>
    public int MaxArchiveCompressionRatio { get; init; } = 200;

    /// <summary>Characters of a whole path.</summary>
    public int MaxPathChars { get; init; } = 1_024;

    /// <summary>Throws when a value cannot work (zero or negative).</summary>
    public ConversionLimits Validated()
    {
        if (MaxFilesPerRequest < 1 || MaxFileBytes < 1 || MaxImagePixels < 1 || MaxPages < 1
            || MaxArchiveEntries < 1 || MaxArchiveUncompressedBytes < 1 || MaxArchiveEntryBytes < 1
            || MaxArchiveCompressionRatio < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ConversionLimits), "Every limit must be at least 1.");
        }

        if (MaxPathChars < 32 || MaxPathChars > 32_000)
            throw new ArgumentOutOfRangeException(nameof(ConversionLimits), "The path limit must be between 32 and 32000 characters.");

        return this;
    }
}
