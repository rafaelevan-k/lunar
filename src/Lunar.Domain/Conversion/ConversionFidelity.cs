namespace Lunar.Domain.Conversion;

/// <summary>
/// What the user keeps when a route runs, in words the UI can turn into a notice before the conversion starts. It never
/// promises more than the route does: no value means "editable" or "lossless" for a route that is neither.
/// </summary>
public enum ConversionFidelity
{
    /// <summary>The route does not exist in v1, so there is nothing to promise.</summary>
    NotAvailable,

    /// <summary>The bytes are copied unchanged under a new name (same encoding, no re-encoding).</summary>
    Identical,

    /// <summary>The decoded pixels are kept exactly in a lossless format; artefacts already in the source (for example JPEG blocks) stay.</summary>
    PixelPreserving,

    /// <summary>The picture is compressed again and loses detail; transparency is flattened onto a background colour.</summary>
    Lossy,

    /// <summary>The picture is placed inside a document as pixels. There is no text recognition and nothing becomes editable.</summary>
    Embedded,

    /// <summary>Each page becomes a picture at a chosen resolution; text and vectors are no longer selectable or sharp when enlarged.</summary>
    Rasterized,

    /// <summary>The result depends on an installed layout engine; fonts, spacing and page breaks may differ from Word.</summary>
    LayoutDependent
}
