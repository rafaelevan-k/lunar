using System.Runtime.InteropServices;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Translate;
using Lunar.Application.Shell.Models;
using Lunar.Windows.Capture;
using Lunar.Windows.Ocr;
using Xunit;

namespace Lunar.Windows.IntegrationTests.Ocr;

/// <summary>
/// BE-47 proof (ADR-0028): known text drawn with GDI into a picture of known size, read back by this PC's Windows OCR. English and Indonesian go
/// through the English recogniser; Japanese (hiragana, katakana, kanji) through the Japanese one. The accuracy is written to the test output for the
/// evidence; the asserts hold the lines the sprint relies on.
/// </summary>
public sealed class ScreenTextProofTests(ITestOutputHelper output)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, OcrLanguage, string, string> Samples => new()
    {
        { "english", OcrLanguage.English, "Segoe UI", "Save your changes before closing the window" },
        { "indonesian", OcrLanguage.English, "Segoe UI", "Simpan perubahan Anda sebelum menutup jendela ini" },
        { "japanese", OcrLanguage.Japanese, "Yu Gothic UI", "ウィンドウを閉じる前に変更を保存してください" },
        { "japanese-kanji", OcrLanguage.Japanese, "Yu Gothic UI", "東京の天気は明日晴れるでしょう" }
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task KnownTextIsReadBack(string name, OcrLanguage language, string font, string text)
    {
        var reader = new WindowsScreenTextReader(new NullLog());
        if (!reader.AvailableLanguages.Contains(language))
            Assert.Skip($"The {language} OCR language is not installed on this PC.");

        var picture = Draw(text, font, 900, 120, 30);
        var started = DateTime.UtcNow;
        var read = await reader.ReadAsync(picture, language, Ct);
        var took = DateTime.UtcNow - started;
        Assert.True(read.IsSuccess, read.Error?.SafeMessage);
        var found = string.Concat(read.Value.Select(l => l.Text));
        var score = Similarity(Squash(text), Squash(found));
        output.WriteLine($"{name}: {score:P0} in {took.TotalMilliseconds:0} ms; read \"{found}\"");

        Assert.True(score >= 0.85, $"{name}: only {score:P0} of the characters were read back.");
        var box = read.Value[0].Box;
        Assert.InRange(box.X, 10, 60); // drawn at x = 20
        Assert.InRange(box.Y, 20, 80);
    }

    [Fact]
    public async Task AFullHdPictureIsReadInAReasonableTime()
    {
        var reader = new WindowsScreenTextReader(new NullLog());
        if (!reader.AvailableLanguages.Contains(OcrLanguage.English))
            Assert.Skip("No English OCR language on this PC.");

        var picture = DrawMany(1920, 1080, 30);
        var started = DateTime.UtcNow;
        var read = await reader.ReadAsync(picture, OcrLanguage.English, Ct);
        var took = DateTime.UtcNow - started;
        Assert.True(read.IsSuccess);
        output.WriteLine($"1920x1080 with 30 lines: {read.Value.Count} lines in {took.TotalMilliseconds:0} ms");
        Assert.InRange(read.Value.Count, 25, 40);
        Assert.True(took < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AMissingRecogniserIsATypedResultAndABlackPictureHasNoText()
    {
        var reader = new WindowsScreenTextReader(new NullLog());
        var black = new ScreenPicture(new PixelRect(0, 0, 400, 200), Enumerable.Repeat((byte)0, 400 * 200 * 4).ToArray());
        foreach (var language in Enum.GetValues<OcrLanguage>())
        {
            var read = await reader.ReadAsync(black, language, Ct);
            if (reader.AvailableLanguages.Contains(language))
                Assert.Empty(read.Value);
            else
                Assert.Equal(Lunar.Domain.Shared.ErrorCode.DependencyMissing, read.Error!.Code);
        }
    }

    [Fact]
    public void TilesCoverTheWholePictureOnce()
    {
        var tiles = WindowsScreenTextReader.Tiles(25000, 12000, 10000).ToList();
        Assert.Equal(6, tiles.Count);
        Assert.Equal(25000L * 12000, tiles.Sum(t => (long)t.Width * t.Height));
        Assert.All(tiles, t => Assert.True(t.Width <= 10000 && t.Height <= 10000));
    }

    [Fact]
    public void TheCaptureReadsTheActiveDisplayIntoMemory()
    {
        var capture = new GdiScreenCapture(new NullLog());
        var display = GdiScreenCapture.ActiveDisplay();
        Assert.NotNull(display);
        var picture = capture.Capture(new PixelRect(display!.X, display!.Y, Math.Min(200, display!.Width), Math.Min(100, display!.Height)));
        Assert.True(picture.IsSuccess, picture.Error?.SafeMessage);
        Assert.Equal(200 * 100 * 4, picture.Value.Pixels.Length);
        Assert.All(Enumerable.Range(0, 200 * 100).Select(i => picture.Value.Pixels[(i * 4) + 3]), a => Assert.Equal(255, a));
        picture.Value.Clear();
        Assert.True(picture.Value.IsCleared);
        Assert.All(picture.Value.Pixels, b => Assert.Equal(0, b));
        Assert.False(capture.Capture(null).IsSuccess);
    }

    // ---- drawing with GDI ------------------------------------------------------------------------------------------

    private static ScreenPicture Draw(string text, string font, int width, int height, int size) =>
        Render(width, height, (dc) =>
        {
            var f = CreateFontW(-size, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, font);
            var old = SelectObject(dc, f);
            TextOutW(dc, 20, 40, text, text.Length);
            SelectObject(dc, old);
            DeleteObject(f);
        });

    private static ScreenPicture DrawMany(int width, int height, int lines) =>
        Render(width, height, dc =>
        {
            var f = CreateFontW(-20, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            var old = SelectObject(dc, f);
            for (var i = 0; i < lines; i++)
            {
                var line = $"Line {i + 1}: the quick brown fox jumps over the lazy dog";
                TextOutW(dc, 40 + ((i % 3) * 600), 30 + ((i / 3) * 100), line, line.Length);
            }

            SelectObject(dc, old);
            DeleteObject(f);
        });

    private static ScreenPicture Render(int width, int height, Action<IntPtr> paint)
    {
        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        var old = SelectObject(dc, bitmap);
        try
        {
            var white = CreateSolidBrush(0x00FFFFFF);
            var rect = new Rect { Right = width, Bottom = height };
            FillRect(dc, ref rect, white);
            DeleteObject(white);
            SetBkMode(dc, 1);
            SetTextColor(dc, 0x00202020);
            paint(dc);
            SelectObject(dc, old);
            var header = new BitmapInfoHeader { Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height, Planes = 1, BitCount = 32 };
            var pixels = new byte[width * height * 4];
            GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref header, 0);
            for (var i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255;
            return new ScreenPicture(new PixelRect(0, 0, width, height), pixels);
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(dc);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static string Squash(string text) => new([.. text.Where(c => !char.IsWhiteSpace(c))]);

    /// <summary>1 minus the edit distance over the longer length.</summary>
    private static double Similarity(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
            d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++)
            d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        }

        return 1 - ((double)d[a.Length, b.Length] / Math.Max(1, Math.Max(a.Length, b.Length)));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref Rect rect, IntPtr brush);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr dc, uint color);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool TextOutW(IntPtr dc, int x, int y, string text, int length);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strike,
        uint charset, uint precision, uint clip, uint quality, uint pitch, string face);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfoHeader info, int usage);

    private sealed class NullLog : IDiagnosticLog
    {
        public void Write(DiagnosticEvent diagnosticEvent)
        {
        }
    }
}
