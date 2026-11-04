namespace Lunar.Domain.Conversion;

/// <summary>How a route gets from the source format to the target format.</summary>
public enum ConversionMode
{
    /// <summary>The route does not exist in v1.</summary>
    Unsupported,

    /// <summary>Same encoding: the file is copied under the target name, never re-encoded (PNG to PNG, JPG to JPEG, and so on).</summary>
    SameFormat,

    /// <summary>One picture is decoded and written in another picture format.</summary>
    Native,

    /// <summary>A picture is placed inside a PDF page or a DOCX document.</summary>
    Embed,

    /// <summary>Pages of a PDF are drawn into pictures, one output per page.</summary>
    Rasterize,

    /// <summary>An installed document layout engine does the work; Lunar neither bundles nor downloads it.</summary>
    ExternalRenderer
}

/// <summary>Why a route is unsupported, so the UI can say more than "not available".</summary>
public enum UnsupportedReason
{
    /// <summary>The route is supported.</summary>
    None,

    /// <summary>
    /// A possible later addition (PDF to editable DOCX) that v1 does not include. It would need its own approval, a reviewed
    /// provider and a fidelity label before it could be turned on.
    /// </summary>
    PostV1Stretch
}

/// <summary>
/// One ordered pair of formats (what the user has, what the user wants) with how it is done and what it keeps. A route is a
/// fact of the product's matrix, not a promise that it can run right now: whether a renderer is installed, or a file is
/// valid, is decided later (planning and validation).
/// Invariants are enforced on creation, so a route that contradicts itself cannot exist: only same-encoding pairs are
/// <see cref="ConversionMode.SameFormat"/> (and they are the only <see cref="ConversionFidelity.Identical"/> ones); only an
/// unsupported route has <see cref="ConversionFidelity.NotAvailable"/> and a reason; an external renderer always means
/// <see cref="ConversionFidelity.LayoutDependent"/>.
/// </summary>
public sealed record ConversionRoute
{
    public ConversionRoute(
        FileFormat source,
        FileFormat target,
        ConversionMode mode,
        ConversionFidelity fidelity,
        UnsupportedReason unsupportedReason = UnsupportedReason.None,
        bool producesOneOutputPerPage = false,
        bool flattensTransparency = false)
    {
        FileFormats.Require(source);
        FileFormats.Require(target);
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown conversion mode.");
        if (!Enum.IsDefined(fidelity))
            throw new ArgumentOutOfRangeException(nameof(fidelity), fidelity, "Unknown fidelity.");
        if (!Enum.IsDefined(unsupportedReason))
            throw new ArgumentOutOfRangeException(nameof(unsupportedReason), unsupportedReason, "Unknown reason.");

        var unsupported = mode == ConversionMode.Unsupported;
        if (unsupported != (fidelity == ConversionFidelity.NotAvailable))
            throw new ArgumentException("Only an unsupported route has no fidelity.", nameof(fidelity));
        if (unsupported != (unsupportedReason != UnsupportedReason.None))
            throw new ArgumentException("An unsupported route needs a reason, and a supported one has none.", nameof(unsupportedReason));
        if ((mode == ConversionMode.SameFormat) != FileFormats.SameEncoding(source, target))
            throw new ArgumentException("A route is a same-format copy exactly when both formats share an encoding.", nameof(mode));
        if ((mode == ConversionMode.SameFormat) != (fidelity == ConversionFidelity.Identical))
            throw new ArgumentException("Only a same-format copy keeps the bytes identical.", nameof(fidelity));
        if ((mode == ConversionMode.ExternalRenderer) != (fidelity == ConversionFidelity.LayoutDependent))
            throw new ArgumentException("An external renderer means the layout depends on it, and nothing else does.", nameof(fidelity));
        if (unsupported && (producesOneOutputPerPage || flattensTransparency))
            throw new ArgumentException("An unsupported route produces nothing.", nameof(producesOneOutputPerPage));

        Source = source;
        Target = target;
        Mode = mode;
        Fidelity = fidelity;
        UnsupportedReason = unsupportedReason;
        ProducesOneOutputPerPage = producesOneOutputPerPage;
        FlattensTransparency = flattensTransparency;
    }

    /// <summary>The format the user has.</summary>
    public FileFormat Source { get; }

    /// <summary>The format the user wants.</summary>
    public FileFormat Target { get; }

    public ConversionMode Mode { get; }

    public ConversionFidelity Fidelity { get; }

    /// <summary>None for a supported route.</summary>
    public UnsupportedReason UnsupportedReason { get; }

    /// <summary>True when a multi-page source gives one output file per page (PDF or DOCX to pictures), so the output names carry the page number.</summary>
    public bool ProducesOneOutputPerPage { get; }

    /// <summary>True when transparent pixels are painted onto a background colour because the target cannot hold transparency (to JPG/JPEG).</summary>
    public bool FlattensTransparency { get; }

    /// <summary>True when the route exists in v1.</summary>
    public bool IsSupported => Mode != ConversionMode.Unsupported;

    /// <summary>True when an installed layout engine is needed, which the UI must disclose and the planner must detect.</summary>
    public bool RequiresExternalRenderer => Mode == ConversionMode.ExternalRenderer;

    /// <summary>True when the result is compressed again with loss.</summary>
    public bool IsLossy => Fidelity == ConversionFidelity.Lossy;
}
