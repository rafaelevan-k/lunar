using Lunar.Domain.Conversion;

namespace Lunar.Application.Conversion.Models;

/// <summary>
/// What the user chose about HOW files are converted (PRD section 5), beyond which files and which format: the JPEG quality, the page
/// layout for a picture put in a PDF or Word file, and the resolution and pages for a PDF turned into pictures. Every value has a
/// safe default, so a request with no choices converts with the defaults.
///
/// The password of a protected PDF lives here, in memory only, so that a job tried again does not ask again. It is never written
/// anywhere, and printing this record (or a job that holds it) shows no value. It is carried to the PDF reader and nowhere else.
/// </summary>
public sealed record ConversionChoices
{
    /// <summary>The defaults.</summary>
    public static ConversionChoices Default { get; } = new();

    public ImageEncodingOptions Image { get; init; } = new();

    public PageLayoutOptions Layout { get; init; } = new();

    public PdfRasterizationOptions Pdf { get; init; } = new();

    /// <summary>The password of a protected PDF; null when the PDF is not protected.</summary>
    public string? PdfPassword { get; init; }

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> when a value cannot work.</summary>
    public ConversionChoices Validated()
    {
        ArgumentNullException.ThrowIfNull(Image);
        ArgumentNullException.ThrowIfNull(Layout);
        ArgumentNullException.ThrowIfNull(Pdf);
        Image.Validated();
        Layout.Validated();
        Pdf.Validated();
        return this;
    }

    // The generated text would print the password.
    public override string ToString() =>
        $"ConversionChoices {{ Image = {Image}, Layout = {Layout}, Pdf = {Pdf}, PdfPassword = {(PdfPassword is null ? "none" : "set")} }}";
}
