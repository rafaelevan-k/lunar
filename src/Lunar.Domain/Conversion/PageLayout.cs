namespace Lunar.Domain.Conversion;

/// <summary>The size of the page a picture is put on.</summary>
public enum PageSize
{
    /// <summary>The page is as large as the picture shown at 96 pixels per inch (plus the margins), however large or small the picture is within limits.</summary>
    Auto,

    /// <summary>ISO A4, 210 by 297 mm, turned sideways for a wide picture.</summary>
    A4,

    /// <summary>US Letter, 8.5 by 11 inches, turned sideways for a wide picture.</summary>
    Letter
}

/// <summary>How a picture fills the part of the page inside the margins.</summary>
public enum ImageFit
{
    /// <summary>The whole picture is visible, as large as it can be without being cut, centred.</summary>
    Fit,

    /// <summary>The picture covers the whole area inside the margins, centred, and what sticks out is cut off.</summary>
    Cover
}

/// <summary>The page choices for putting a picture into a PDF or a Word document (PRD section 5): size, margin and fit.</summary>
public sealed record PageLayoutOptions
{
    /// <summary>The largest margin, 2 inches.</summary>
    public const double MaxMarginPoints = 144;

    public PageSize Size { get; init; } = PageSize.Auto;

    /// <summary>The margin on every side in points (1/72 inch), from 0 to 144.</summary>
    public double MarginPoints { get; init; }

    public ImageFit Fit { get; init; } = ImageFit.Fit;

    /// <summary>Throws when a value cannot work.</summary>
    public PageLayoutOptions Validated()
    {
        if (!Enum.IsDefined(Size))
            throw new ArgumentOutOfRangeException(nameof(Size), Size, "Unknown page size.");
        if (!Enum.IsDefined(Fit))
            throw new ArgumentOutOfRangeException(nameof(Fit), Fit, "Unknown fit.");
        if (double.IsNaN(MarginPoints) || MarginPoints < 0 || MarginPoints > MaxMarginPoints)
            throw new ArgumentOutOfRangeException(nameof(MarginPoints), MarginPoints, "The margin runs from 0 to 144 points.");
        return this;
    }
}

/// <summary>
/// Where everything goes on the page, in points from the top left corner: the page, the area inside the margins (the box), and the
/// picture itself, which for <see cref="ImageFit.Cover"/> may stick out of the box (the part outside it is cut off).
/// </summary>
public readonly record struct PageGeometry(
    double PageWidth,
    double PageHeight,
    double BoxX,
    double BoxY,
    double BoxWidth,
    double BoxHeight,
    double DrawX,
    double DrawY,
    double DrawWidth,
    double DrawHeight)
{
    /// <summary>True when part of the picture falls outside the box and is cut off.</summary>
    public bool IsCropped => DrawWidth > BoxWidth + 0.01 || DrawHeight > BoxHeight + 0.01;
}

/// <summary>
/// The page arithmetic for a picture put on a page, in one place so a PDF and a Word document lay a picture out the same way.
/// Pure. The picture's shape is given as its size in pixels after any rotation; only its proportions matter on a fixed page.
/// </summary>
public static class PageLayout
{
    /// <summary>96 pixels per inch shown on a page of 72 points per inch.</summary>
    public const double PointsPerPixel = 0.75;

    public const double A4Width = 595.2756;
    public const double A4Height = 841.8898;
    public const double LetterWidth = 612;
    public const double LetterHeight = 792;

    /// <summary>An automatic page is never smaller than this on its longer side (1 inch), so a tiny icon still makes a usable page.</summary>
    public const double MinAutoPageSide = 72;

    /// <summary>The room that must be left inside the margins on a fixed page, per side.</summary>
    public const double MinBoxSide = 36;

    /// <param name="options">The page choices.</param>
    /// <param name="widthPixels">Width of the picture as it will be seen, in pixels.</param>
    /// <param name="heightPixels">Height of the picture as it will be seen, in pixels.</param>
    /// <param name="maxPagePoints">The longest side the target format allows (14400 for PDF, 1584 for Word).</param>
    public static PageGeometry Compute(PageLayoutOptions options, int widthPixels, int heightPixels, double maxPagePoints)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validated();
        if (widthPixels < 1 || heightPixels < 1)
            throw new ArgumentOutOfRangeException(nameof(widthPixels), "A picture has at least one pixel in each direction.");
        if (double.IsNaN(maxPagePoints) || maxPagePoints < (2 * PageLayoutOptions.MaxMarginPoints) + MinAutoPageSide)
            throw new ArgumentOutOfRangeException(nameof(maxPagePoints), maxPagePoints, "The largest page is too small to be useful.");

        var margin = options.MarginPoints;
        return options.Size == PageSize.Auto
            ? Automatic(widthPixels, heightPixels, margin, maxPagePoints)
            : Fixed(options, widthPixels, heightPixels, margin);
    }

    private static PageGeometry Automatic(int widthPixels, int heightPixels, double margin, double maxPagePoints)
    {
        var width = widthPixels * PointsPerPixel;
        var height = heightPixels * PointsPerPixel;
        var longest = Math.Max(width, height);
        var room = maxPagePoints - (2 * margin);

        // Too large for the format: shrink to the biggest page allowed. Too small: grow to a usable page.
        var scale = longest > room ? room / longest : longest < MinAutoPageSide ? MinAutoPageSide / longest : 1.0;
        width *= scale;
        height *= scale;
        return new PageGeometry(width + (2 * margin), height + (2 * margin), margin, margin, width, height, margin, margin, width, height);
    }

    private static PageGeometry Fixed(PageLayoutOptions options, int widthPixels, int heightPixels, double margin)
    {
        var (pageWidth, pageHeight) = options.Size == PageSize.A4 ? (A4Width, A4Height) : (LetterWidth, LetterHeight);
        if (widthPixels > heightPixels)
            (pageWidth, pageHeight) = (pageHeight, pageWidth); // a wide picture gets a wide page

        var boxWidth = pageWidth - (2 * margin);
        var boxHeight = pageHeight - (2 * margin);
        if (boxWidth < MinBoxSide || boxHeight < MinBoxSide)
            throw new ArgumentOutOfRangeException(nameof(options), "The margins leave too little room on the page.");

        var scale = options.Fit == ImageFit.Fit
            ? Math.Min(boxWidth / widthPixels, boxHeight / heightPixels)
            : Math.Max(boxWidth / widthPixels, boxHeight / heightPixels);
        var drawWidth = widthPixels * scale;
        var drawHeight = heightPixels * scale;
        return new PageGeometry(
            pageWidth, pageHeight, margin, margin, boxWidth, boxHeight,
            margin + ((boxWidth - drawWidth) / 2), margin + ((boxHeight - drawHeight) / 2), drawWidth, drawHeight);
    }
}
