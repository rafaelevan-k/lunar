using System.Runtime.InteropServices.WindowsRuntime;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Translate;
using Lunar.Domain.Shared;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using OcrEngine = Windows.Media.Ocr.OcrEngine;
using OcrLine = Lunar.Application.Abstractions.Translate.OcrLine;
using OcrWord = Lunar.Application.Abstractions.Translate.OcrWord;

namespace Lunar.Windows.Ocr;

/// <summary>
/// <see cref="IScreenTextReader"/> with <c>Windows.Media.Ocr</c> (ADR-0028), on this PC. English uses any installed English recogniser (en-US first);
/// Japanese uses the Japanese one. A picture larger than the engine's limit is read in tiles. No WinRT type leaves this class. Nothing is logged but
/// fixed events and counts.
/// </summary>
public sealed class WindowsScreenTextReader : IScreenTextReader
{
    private const string Component = "screen-ocr";
    private readonly IDiagnosticLog _log;
    private readonly Dictionary<OcrLanguage, string> _tags = [];

    public WindowsScreenTextReader(IDiagnosticLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
        try
        {
            var installed = OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();
            var english = installed.FirstOrDefault(t => t.Equals("en-US", StringComparison.OrdinalIgnoreCase))
                          ?? installed.FirstOrDefault(t => t.StartsWith("en", StringComparison.OrdinalIgnoreCase));
            var japanese = installed.FirstOrDefault(t => t.StartsWith("ja", StringComparison.OrdinalIgnoreCase));
            if (english is not null)
                _tags[OcrLanguage.English] = english;
            if (japanese is not null)
                _tags[OcrLanguage.Japanese] = japanese;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or TypeLoadException or InvalidCastException)
        {
            _log.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "screen-ocr.unavailable", Component, "languages", ErrorCode.DependencyMissing, ExceptionType: ex.GetType()));
        }
    }

    public IReadOnlyList<OcrLanguage> AvailableLanguages => [.. _tags.Keys.Order()];

    public Task<Result<IReadOnlyList<OcrLine>>> ReadAsync(ScreenPicture picture, OcrLanguage language, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(picture);
        if (ct.IsCancellationRequested)
            return Task.FromResult(Result.Cancelled<IReadOnlyList<OcrLine>>());
        if (!_tags.TryGetValue(language, out var tag))
            return Task.FromResult(Result.Failure<IReadOnlyList<OcrLine>>(new OperationError(ErrorCode.DependencyMissing, "The OCR language is not installed in Windows.")));
        return Task.Run(() => ReadAllAsync(picture, tag, ct), ct);
    }

    private async Task<Result<IReadOnlyList<OcrLine>>> ReadAllAsync(ScreenPicture picture, string tag, CancellationToken ct)
    {
        var engine = OcrEngine.TryCreateFromLanguage(new Language(tag));
        if (engine is null)
            return Result.Failure<IReadOnlyList<OcrLine>>(new OperationError(ErrorCode.DependencyMissing, "The OCR language is not installed in Windows."));

        var limit = (int)OcrEngine.MaxImageDimension;
        var lines = new List<OcrLine>();
        try
        {
            foreach (var tile in Tiles(picture.Width, picture.Height, limit))
            {
                if (ct.IsCancellationRequested)
                    return Result.Cancelled<IReadOnlyList<OcrLine>>();
                var bytes = Crop(picture, tile);
                using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(bytes.AsBuffer(), BitmapPixelFormat.Bgra8, tile.Width, tile.Height, BitmapAlphaMode.Premultiplied);
                Array.Clear(bytes);
                var result = await engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);
                foreach (var line in result.Lines)
                {
                    var words = line.Words
                        .Select(w => new OcrWord(w.Text, new TextBox(w.BoundingRect.X + tile.X, w.BoundingRect.Y + tile.Y, w.BoundingRect.Width, w.BoundingRect.Height)))
                        .ToList();
                    if (words.Count > 0)
                        lines.Add(new OcrLine(line.Text, words));
                }
            }
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled<IReadOnlyList<OcrLine>>();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
        {
            _log.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "screen-ocr.failed", Component, "read", ErrorCode.Unexpected, ExceptionType: ex.GetType()));
            return Result.Failure<IReadOnlyList<OcrLine>>(new OperationError(ErrorCode.Unexpected, "Windows could not read the text on the screen.", retryable: true));
        }

        return Result.Success<IReadOnlyList<OcrLine>>(lines);
    }

    /// <summary>Tiles no larger than the engine's limit, covering the picture once.</summary>
    internal static IEnumerable<(int X, int Y, int Width, int Height)> Tiles(int width, int height, int limit)
    {
        limit = Math.Max(1, limit);
        for (var y = 0; y < height; y += limit)
        {
            for (var x = 0; x < width; x += limit)
                yield return (x, y, Math.Min(limit, width - x), Math.Min(limit, height - y));
        }
    }

    private static byte[] Crop(ScreenPicture picture, (int X, int Y, int Width, int Height) tile)
    {
        if (tile.X == 0 && tile.Y == 0 && tile.Width == picture.Width && tile.Height == picture.Height)
            return (byte[])picture.Pixels.Clone();
        var bytes = new byte[tile.Width * tile.Height * 4];
        for (var row = 0; row < tile.Height; row++)
            Buffer.BlockCopy(picture.Pixels, (((tile.Y + row) * picture.Width) + tile.X) * 4, bytes, row * tile.Width * 4, tile.Width * 4);
        return bytes;
    }
}
