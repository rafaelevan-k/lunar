namespace Lunar.Application.Conversion.Models;

/// <summary>A plain colour (no transparency) such as the background transparent pixels are painted onto.</summary>
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static RgbColor White => new(255, 255, 255);

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// The choices that shape a picture-to-picture conversion (PRD section 5). Only the values the converter really uses are here:
/// how hard a JPEG is compressed, and what colour shows through where the source is transparent and the target cannot be.
/// </summary>
public sealed record ImageEncodingOptions
{
    public const int MinJpegQuality = 1;
    public const int MaxJpegQuality = 100;
    public const int DefaultJpegQuality = 90;

    /// <summary>JPEG quality from 1 (smallest file, most loss) to 100 (largest file, least loss); 90 by default.</summary>
    public int JpegQuality { get; init; } = DefaultJpegQuality;

    /// <summary>The colour painted under transparent and half-transparent pixels when the target is a JPEG; white by default.</summary>
    public RgbColor Background { get; init; } = RgbColor.White;

    /// <summary>Throws when a value cannot work.</summary>
    public ImageEncodingOptions Validated()
    {
        if (JpegQuality is < MinJpegQuality or > MaxJpegQuality)
            throw new ArgumentOutOfRangeException(nameof(JpegQuality), JpegQuality, "JPEG quality runs from 1 to 100.");
        return this;
    }
}
