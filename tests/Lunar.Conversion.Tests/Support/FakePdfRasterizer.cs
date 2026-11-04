using Lunar.Application.Abstractions.Conversion;
using Lunar.Application.Conversion.Models;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Conversion.Tests.Support;

/// <summary>
/// A PDF engine that draws flat-coloured pages (one colour per page number) as real PNGs of exactly the requested size, so the provider's
/// own work (page choice, sizes, limits, naming, density, JPEG, cleanup) is tested without the operating system's renderer. It can fail,
/// delay, cancel or misreport on demand. The real engine is tested in the Windows project.
/// </summary>
internal sealed class FakePdfRasterizer : IPdfPageRasterizer
{
    public int Pages { get; set; } = 3;

    /// <summary>Size of each page in points, by page index.</summary>
    public Func<int, (double Width, double Height)> PageSize { get; set; } = _ => (100, 150);

    public string? RequiredPassword { get; set; }

    public Result<IPdfDocument>? OpenFailure { get; set; }

    /// <summary>Replaces drawing for a page; return null to draw normally.</summary>
    public Func<int, int, int, Stream, CancellationToken, Task<Result<RenderedPage>>?>? RenderOverride { get; set; }

    public int OpenCalls { get; private set; }

    public string? LastPassword { get; private set; }

    public bool DocumentDisposed { get; private set; }

    public bool StreamStillOpenWhenDisposed { get; private set; }

    public List<(int Index, int Width, int Height)> Renders { get; } = [];

    public static (byte R, byte G, byte B) ColorOfPage(int pageNumber) => ((byte)(30 + ((pageNumber * 61) % 200)), (byte)(40 + ((pageNumber * 37) % 190)), (byte)(220 - ((pageNumber * 23) % 160)));

    public Task<Result<IPdfDocument>> OpenAsync(Stream pdf, string? password, CancellationToken ct)
    {
        OpenCalls++;
        LastPassword = password;
        if (OpenFailure is { } failure)
            return Task.FromResult(failure);
        if (RequiredPassword is { } required && password != required)
        {
            return Task.FromResult(Result.Failure<IPdfDocument>(new OperationError(
                ErrorCode.AccessDenied,
                string.IsNullOrEmpty(password) ? "This PDF is protected by a password. Enter the password to convert it." : "That password didn't open this PDF. Check it and try again.")));
        }

        // The source must be readable now and stay open while the document is in use.
        Assert.True(pdf.CanRead && pdf.CanSeek);
        return Task.FromResult(Result.Success<IPdfDocument>(new Document(this, pdf)));
    }

    private sealed class Document(FakePdfRasterizer owner, Stream pdf) : IPdfDocument
    {
        public int PageCount => owner.Pages;

        public Result<PdfPageSize> GetPageSize(int pageIndex)
        {
            if (pageIndex < 0 || pageIndex >= PageCount)
                return Result.Failure<PdfPageSize>(new OperationError(ErrorCode.InvalidInput, "That page does not exist."));
            var (w, h) = owner.PageSize(pageIndex);
            return Result.Success(new PdfPageSize(w, h));
        }

        public Task<Result<RenderedPage>> RenderPageAsync(int pageIndex, int widthPixels, int heightPixels, Stream destination, CancellationToken ct)
        {
            lock (owner.Renders)
                owner.Renders.Add((pageIndex, widthPixels, heightPixels));
            if (owner.RenderOverride?.Invoke(pageIndex, widthPixels, heightPixels, destination, ct) is { } replaced)
                return replaced;

            var color = ColorOfPage(pageIndex + 1);
            destination.Write(PictureFixtures.Rgb(widthPixels, heightPixels, (_, _) => color));
            return Task.FromResult(Result.Success(new RenderedPage(widthPixels, heightPixels)));
        }

        public ValueTask DisposeAsync()
        {
            owner.DocumentDisposed = true;
            owner.StreamStillOpenWhenDisposed = pdf.CanRead;
            return ValueTask.CompletedTask;
        }
    }
}
