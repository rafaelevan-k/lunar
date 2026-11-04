namespace Lunar.Domain.Conversion;

/// <summary>
/// The conversion matrix of PRD section 5 as code: all 25 ordered pairs of the five formats, each classified once and never
/// left to a default. This is the single answer to "can this become that, how, and what does the user keep"; planning,
/// validation, providers and the UI ask here and do not keep their own copy. Pure and deterministic.
///
/// What the matrix says (JPG and JPEG are one encoding):
/// - A format to itself, and JPG to JPEG or back: a copy under the new name, bytes unchanged.
/// - PNG to JPG/JPEG: re-encoded with loss, transparency flattened. JPG/JPEG to PNG: pixels kept exactly.
/// - Any picture to PDF or DOCX: the picture is embedded (no text recognition, nothing editable).
/// - PDF to a picture: one rasterized picture per page.
/// - DOCX to PDF or to pictures: only with an installed layout engine; the layout may differ from Word.
/// - PDF to DOCX: not in v1 (a possible later stretch that needs its own approval).
/// </summary>
public static class ConversionPolicy
{
    private static readonly ConversionRoute[] Matrix = Build();

    /// <summary>All 25 routes, source by source in the order of <see cref="FileFormats.All"/>, then target by target.</summary>
    public static IReadOnlyList<ConversionRoute> All => Matrix;

    /// <summary>The route from <paramref name="source"/> to <paramref name="target"/>. Every pair has one; an unknown value throws.</summary>
    public static ConversionRoute Resolve(FileFormat source, FileFormat target)
    {
        FileFormats.Require(source);
        FileFormats.Require(target);
        return Matrix[(int)source * FileFormats.All.Count + (int)target];
    }

    /// <summary>The formats <paramref name="source"/> can become in v1, in the order of <see cref="FileFormats.All"/>.</summary>
    public static IReadOnlyList<FileFormat> TargetsFor(FileFormat source)
    {
        FileFormats.Require(source);
        return [.. FileFormats.All.Where(target => Resolve(source, target).IsSupported)];
    }

    private static ConversionRoute[] Build()
    {
        var formats = FileFormats.All;
        var routes = new ConversionRoute[formats.Count * formats.Count];
        foreach (var source in formats)
        {
            foreach (var target in formats)
                routes[(int)source * formats.Count + (int)target] = Classify(source, target);
        }

        return routes;
    }

    // Written out per source so the table reads like the matrix in the PRD. The tests pin all 25 pairs against an independent
    // copy of the table, so a pair that falls into a default branch by mistake fails there.
    private static ConversionRoute Classify(FileFormat source, FileFormat target)
    {
        if (FileFormats.SameEncoding(source, target))
            return new ConversionRoute(source, target, ConversionMode.SameFormat, ConversionFidelity.Identical);

        return source switch
        {
            FileFormat.Png => target switch
            {
                FileFormat.Jpg or FileFormat.Jpeg =>
                    new ConversionRoute(source, target, ConversionMode.Native, ConversionFidelity.Lossy, flattensTransparency: true),
                _ => new ConversionRoute(source, target, ConversionMode.Embed, ConversionFidelity.Embedded)
            },
            FileFormat.Jpg or FileFormat.Jpeg => target switch
            {
                FileFormat.Png => new ConversionRoute(source, target, ConversionMode.Native, ConversionFidelity.PixelPreserving),
                _ => new ConversionRoute(source, target, ConversionMode.Embed, ConversionFidelity.Embedded)
            },
            FileFormat.Docx => target switch
            {
                FileFormat.Pdf => new ConversionRoute(source, target, ConversionMode.ExternalRenderer, ConversionFidelity.LayoutDependent),
                _ => new ConversionRoute(source, target, ConversionMode.ExternalRenderer, ConversionFidelity.LayoutDependent, producesOneOutputPerPage: true)
            },
            _ => target switch
            {
                FileFormat.Docx => new ConversionRoute(source, target, ConversionMode.Unsupported, ConversionFidelity.NotAvailable, UnsupportedReason.PostV1Stretch),
                _ => new ConversionRoute(source, target, ConversionMode.Rasterize, ConversionFidelity.Rasterized, producesOneOutputPerPage: true)
            }
        };
    }
}
