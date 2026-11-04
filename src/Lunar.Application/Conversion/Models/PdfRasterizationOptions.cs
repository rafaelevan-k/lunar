using Lunar.Domain.Conversion;

namespace Lunar.Application.Conversion.Models;

/// <summary>The size of a PDF page in points (1/72 inch).</summary>
public readonly record struct PdfPageSize(double WidthPoints, double HeightPoints);

/// <summary>What a drawn page turned out to be, in pixels.</summary>
public readonly record struct RenderedPage(int WidthPixels, int HeightPixels);

/// <summary>
/// The choices for turning PDF pages into pictures (PRD section 5): the resolution and which pages. The password of a protected PDF is
/// not here on purpose: it is passed on its own, so printing or logging these options can never show it.
/// </summary>
public sealed record PdfRasterizationOptions
{
    public const int Dpi72 = 72;
    public const int Dpi150 = 150;
    public const int Dpi300 = 300;

    /// <summary>Pixels per inch: 72, 150 (the default) or 300.</summary>
    public int Dpi { get; init; } = Dpi150;

    /// <summary>The pages to draw; every page by default. Page numbers in the output names are the document's own.</summary>
    public PageSelection Pages { get; init; } = PageSelection.All;

    /// <summary>Throws when a value cannot work.</summary>
    public PdfRasterizationOptions Validated()
    {
        if (Dpi is not (Dpi72 or Dpi150 or Dpi300))
            throw new ArgumentOutOfRangeException(nameof(Dpi), Dpi, "The resolution is 72, 150 or 300 pixels per inch.");
        ArgumentNullException.ThrowIfNull(Pages);
        return this;
    }
}
