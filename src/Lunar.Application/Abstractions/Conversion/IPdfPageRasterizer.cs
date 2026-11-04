using Lunar.Application.Conversion.Models;
using Lunar.Domain.Shared;

namespace Lunar.Application.Abstractions.Conversion;

/// <summary>
/// Draws the pages of a PDF as pictures (BE-24, ADR-0003). The engine is the operating system's PDF renderer, owned by the Windows
/// layer; this port is the only thing the conversion layer sees of it, and no Windows type crosses it.
///
/// Streams: the caller owns every stream it passes in and keeps it open until the <see cref="IPdfDocument"/> made from it is disposed;
/// the rasterizer never closes it. The PDF stream must be readable and seekable. A destination stream must be writable and seekable and
/// empty; the picture is written to it as a PNG, nothing is kept after the call returns, and after a failure or a cancel the destination
/// holds nothing usable (the caller discards it). By the time a call returns, even a cancelled one, the engine is no longer writing to it.
///
/// Expected failures are returned as <see cref="Result"/> failures with messages that carry no path, password or content:
/// <see cref="ErrorCode.InvalidInput"/> for something that is not a readable PDF (or a page that does not exist or a size that cannot be
/// drawn), <see cref="ErrorCode.AccessDenied"/> for a PDF that needs a password (no password was given) or did not accept the one given,
/// <see cref="ErrorCode.Unsupported"/> for a PDF the engine cannot draw, <see cref="ErrorCode.Timeout"/> when the engine did not answer,
/// and <see cref="Result.Cancelled{T}"/> on cancellation. Passwords exist only in memory for the length of the call.
/// </summary>
public interface IPdfPageRasterizer
{
    /// <summary>Opens a PDF. The <paramref name="password"/> is null or empty for a PDF that needs none.</summary>
    Task<Result<IPdfDocument>> OpenAsync(Stream pdf, string? password, CancellationToken ct);
}

/// <summary>An open PDF. Pages are numbered from 0 here. One page is drawn at a time; the document is thread-safe in that respect.</summary>
public interface IPdfDocument : IAsyncDisposable
{
    int PageCount { get; }

    /// <summary>The size of a page when printed, in points (1/72 inch), before any scaling.</summary>
    Result<PdfPageSize> GetPageSize(int pageIndex);

    /// <summary>
    /// Draws a page on white paper into <paramref name="destination"/> as a PNG about <paramref name="widthPixels"/> by
    /// <paramref name="heightPixels"/> pixels (the engine's scaling may make it differ by a pixel or two; the real size is returned).
    /// A size over the engine's own limit is refused with <see cref="ErrorCode.InvalidInput"/>.
    /// </summary>
    Task<Result<RenderedPage>> RenderPageAsync(int pageIndex, int widthPixels, int heightPixels, Stream destination, CancellationToken ct);
}
