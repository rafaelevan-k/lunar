using System.Runtime.InteropServices;
using Lunar.Application.Abstractions.Conversion;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Conversion.Models;
using Lunar.Domain.Shared;
using Lunar.Windows.Interop;
using Windows.Data.Pdf;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace Lunar.Windows.Pdf;

/// <summary>
/// Draws PDF pages with the operating system's own PDF renderer, <c>Windows.Data.Pdf</c> (ADR-0003, BE-24). It implements the Application
/// port <see cref="IPdfPageRasterizer"/>; nothing of WinRT leaves this class.
///
/// What it takes care of so the callers do not have to:
/// - The caller's streams are only borrowed (see <see cref="NonClosingStream"/>); a cancel or a timeout waits for the engine to stop writing
///   before returning, so the caller can safely close the destination afterwards. If the engine will not stop, the document refuses further pages.
/// - The engine measures a page in device-independent units and scales it by the screen's resolution, so a request of 1240 pixels may come out
///   wider or narrower. The first guess is the system scale; every picture is then measured (its real size is read from the PNG header), the scale is
///   learned from the measurement, and a picture that is off is drawn once more. A size that still does not come out within a couple of pixels
///   is a failure, never a silently wrong picture. The real size is returned.
/// - Pages are drawn on white paper (not transparent) and ignoring the high-contrast theme, so the same page always looks the same.
/// - One page at a time per document, a time limit for opening and for each page, and the engine's own size limit refused up front.
///
/// The PDF is parsed by the operating system inside this process. A damaged or hostile file is therefore bounded only by the time limits and the
/// size checks of the callers; running the parser in a separate process is a later security decision (ARCHITECTURE section 4.4). Passwords are never
/// stored, logged or put in a message; a protected PDF that needs a password, and one that did not accept it, are told apart by whether one was given.
/// Thread-safe.
/// </summary>
public sealed class WindowsPdfPageRasterizer : IPdfPageRasterizer
{
    /// <summary>The longest side of a picture the engine is asked for, in pixels.</summary>
    public const int MaxSidePixels = 32_767;

    /// <summary>The most pixels in one picture the engine is asked for.</summary>
    public const long MaxPixels = 100_000_000;

    private const string Component = "PdfRasterizer";
    // ERROR_WRONG_PASSWORD, which the engine reports both for a missing and for a wrong password (observed); ERROR_INVALID_PASSWORD is accepted too.
    private const int WrongPasswordHResult = unchecked((int)0x8007052B);
    private const int InvalidPasswordHResult = unchecked((int)0x80070056);

    private readonly IDiagnosticLog? _log;
    private readonly TimeSpan _openTimeout;
    private readonly TimeSpan _pageTimeout;
    private readonly double _initialScale;

    public WindowsPdfPageRasterizer(IDiagnosticLog? log = null)
        : this(log, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(90), initialScale: null)
    {
    }

    internal WindowsPdfPageRasterizer(IDiagnosticLog? log, TimeSpan openTimeout, TimeSpan pageTimeout, double? initialScale)
    {
        _log = log;
        _openTimeout = openTimeout;
        _pageTimeout = pageTimeout;
        _initialScale = initialScale ?? SystemScale();
    }

    public async Task<Result<IPdfDocument>> OpenAsync(Stream pdf, string? password, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        if (!pdf.CanRead || !pdf.CanSeek)
            return Fail<IPdfDocument>(ErrorCode.InvalidInput, "The PDF could not be read.");
        if (ct.IsCancellationRequested)
            return Result.Cancelled<IPdfDocument>();

        var hasPassword = !string.IsNullOrEmpty(password);
        using var timeout = new CancellationTokenSource(_openTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        IRandomAccessStream? random = null;
        try
        {
            pdf.Position = 0;
            random = new NonClosingStream(pdf).AsRandomAccessStream();
            var document = hasPassword
                ? await PdfDocument.LoadFromStreamAsync(random, password).AsTask(linked.Token).ConfigureAwait(false)
                : await PdfDocument.LoadFromStreamAsync(random).AsTask(linked.Token).ConfigureAwait(false);
            if (document.PageCount == 0)
            {
                random.Dispose();
                return Fail<IPdfDocument>(ErrorCode.InvalidInput, "This PDF has no pages.");
            }

            return Result.Success<IPdfDocument>(new WindowsPdfDocument(document, random, _initialScale, _pageTimeout, _log));
        }
        catch (OperationCanceledException)
        {
            random?.Dispose();
            return ct.IsCancellationRequested
                ? Result.Cancelled<IPdfDocument>()
                : Fail<IPdfDocument>(ErrorCode.Timeout, "Opening this PDF took too long.", retryable: true);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            random?.Dispose();
            if (ex.HResult is WrongPasswordHResult or InvalidPasswordHResult)
            {
                return Fail<IPdfDocument>(ErrorCode.AccessDenied, hasPassword
                    ? "That password didn't open this PDF. Check it and try again."
                    : "This PDF is protected by a password. Enter the password to convert it.");
            }

            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Information, "pdf.open.failed", Component, "Open", ErrorCode.InvalidInput, ExceptionType: ex.GetType()));
            return Fail<IPdfDocument>(ErrorCode.InvalidInput, "This PDF is damaged or in a form Lunar can't read. Try getting a fresh copy of it.");
        }
    }

    private static double SystemScale()
    {
        try
        {
            var dpi = NativeMethods.GetDpiForSystem();
            return dpi >= 48 && dpi <= 960 ? dpi / 96.0 : 1.0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return 1.0;
        }
    }

    internal static Result<T> Fail<T>(ErrorCode code, string message, bool retryable = false) =>
        Result.Failure<T>(new OperationError(code, message, retryable));

    private sealed class WindowsPdfDocument : IPdfDocument
    {
        private const int StopWaitMilliseconds = 5_000;

        private readonly PdfDocument _document;
        private readonly IRandomAccessStream _stream;
        private readonly TimeSpan _pageTimeout;
        private readonly IDiagnosticLog? _log;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private double _scale;
        private bool _disposed;
        private bool _stuck;

        public WindowsPdfDocument(PdfDocument document, IRandomAccessStream stream, double scale, TimeSpan pageTimeout, IDiagnosticLog? log)
        {
            _document = document;
            _stream = stream;
            _scale = scale;
            _pageTimeout = pageTimeout;
            _log = log;
            PageCount = (int)Math.Min(document.PageCount, int.MaxValue);
        }

        public int PageCount { get; }

        public Result<PdfPageSize> GetPageSize(int pageIndex)
        {
            if (pageIndex < 0 || pageIndex >= PageCount)
                return Fail<PdfPageSize>(ErrorCode.InvalidInput, "That page does not exist.");
            if (_disposed)
                return Fail<PdfPageSize>(ErrorCode.InvalidInput, "The PDF is closed.");
            try
            {
                using var page = _document.GetPage((uint)pageIndex);
                var size = page.Size;
                if (!(size.Width > 0 && size.Height > 0) || double.IsInfinity(size.Width) || double.IsInfinity(size.Height))
                    return Fail<PdfPageSize>(ErrorCode.InvalidInput, "This page has no usable size.");
                return Result.Success(new PdfPageSize(size.Width * 0.75, size.Height * 0.75)); // device-independent units are 1/96 inch
            }
            catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
            {
                return Fail<PdfPageSize>(ErrorCode.InvalidInput, "This page can't be read. The PDF may be damaged.");
            }
        }

        public async Task<Result<RenderedPage>> RenderPageAsync(int pageIndex, int widthPixels, int heightPixels, Stream destination, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(destination);
            if (pageIndex < 0 || pageIndex >= PageCount)
                return Fail<RenderedPage>(ErrorCode.InvalidInput, "That page does not exist.");
            if (widthPixels < 1 || heightPixels < 1 || widthPixels > MaxSidePixels || heightPixels > MaxSidePixels || (long)widthPixels * heightPixels > MaxPixels)
                return Fail<RenderedPage>(ErrorCode.InvalidInput, "That page is too large to draw at this resolution. Choose a lower resolution.");
            if (!destination.CanWrite || !destination.CanSeek)
                return Fail<RenderedPage>(ErrorCode.InvalidInput, "The picture could not be written.");
            if (ct.IsCancellationRequested)
                return Result.Cancelled<RenderedPage>();

            try
            {
                await _gate.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Result.Cancelled<RenderedPage>();
            }

            try
            {
                if (_disposed)
                    return Fail<RenderedPage>(ErrorCode.InvalidInput, "The PDF is closed.");
                if (_stuck)
                    return Fail<RenderedPage>(ErrorCode.Unexpected, "The PDF engine stopped answering. Close and open the PDF again.", retryable: true);

                using var page = _document.GetPage((uint)pageIndex);
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var dipWidth = (uint)Math.Max(1, Math.Round(widthPixels / _scale));
                    var dipHeight = (uint)Math.Max(1, Math.Round(heightPixels / _scale));
                    destination.Position = 0;
                    destination.SetLength(0);

                    var failure = await DrawAsync(page, destination, dipWidth, dipHeight, ct).ConfigureAwait(false);
                    if (failure is not null)
                        return failure;

                    var actual = PngSize.TryRead(destination);
                    if (actual is null)
                        return Fail<RenderedPage>(ErrorCode.Unexpected, "The PDF engine did not produce a picture.");

                    var (width, height) = actual.Value;
                    // What the engine really did: pixels per requested unit. Learned for the next picture.
                    _scale = Math.Clamp(((width / (double)dipWidth) + (height / (double)dipHeight)) / 2, 0.25, 8);

                    var tolerance = Math.Max(2, (int)Math.Ceiling(_scale));
                    if (Math.Abs(width - widthPixels) <= tolerance && Math.Abs(height - heightPixels) <= tolerance)
                        return Result.Success(new RenderedPage(width, height));
                }

                return Fail<RenderedPage>(ErrorCode.Unexpected, "The page could not be drawn at the requested size.");
            }
            catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
            {
                _log?.Write(new DiagnosticEvent(DiagnosticLevel.Information, "pdf.render.failed", Component, "Render", ErrorCode.InvalidInput, ExceptionType: ex.GetType()));
                return Fail<RenderedPage>(ErrorCode.InvalidInput, "This page can't be drawn. The PDF may be damaged.");
            }
            finally
            {
                _gate.Release();
            }
        }

        // Returns null when the picture was drawn, otherwise the failure to hand back. Never returns while the engine may still be writing.
        private async Task<Result<RenderedPage>?> DrawAsync(PdfPage page, Stream destination, uint dipWidth, uint dipHeight, CancellationToken ct)
        {
            using var timeout = new CancellationTokenSource(_pageTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            IRandomAccessStream? target = null;
            IAsyncAction? operation = null;
            try
            {
                target = new NonClosingStream(destination).AsRandomAccessStream();
                operation = page.RenderToStreamAsync(target, new PdfPageRenderOptions
                {
                    DestinationWidth = dipWidth,
                    DestinationHeight = dipHeight,
                    BackgroundColor = global::Windows.UI.Color.FromArgb(255, 255, 255, 255),
                    IsIgnoringHighContrast = true
                });
                await operation.AsTask(linked.Token).ConfigureAwait(false);
                await target.FlushAsync();
                return null;
            }
            catch (OperationCanceledException)
            {
                var stopped = await StopAsync(operation).ConfigureAwait(false);
                if (!stopped)
                    _stuck = true;
                if (ct.IsCancellationRequested)
                    return Result.Cancelled<RenderedPage>();
                return Fail<RenderedPage>(ErrorCode.Timeout, "Drawing this page took too long.", retryable: true);
            }
            finally
            {
                target?.Dispose();
            }
        }

        // Cancels the engine's work and waits until it has really stopped, so the destination can be closed safely.
        private static async Task<bool> StopAsync(IAsyncAction? operation)
        {
            if (operation is null)
                return true;
            try
            {
                operation.Cancel();
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                // Already finished or failed: the status below says which.
            }

            var waited = 0;
            while (operation.Status == AsyncStatus.Started && waited < StopWaitMilliseconds)
            {
                await Task.Delay(20).ConfigureAwait(false);
                waited += 20;
            }

            return operation.Status != AsyncStatus.Started;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            // Wait for a page in progress; if the engine is stuck, give up waiting and leave the stream to be collected.
            var acquired = await _gate.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            _disposed = true;
            if (acquired && !_stuck)
                _stream.Dispose();
            if (acquired)
                _gate.Release();
        }
    }
}
