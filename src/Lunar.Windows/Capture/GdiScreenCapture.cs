using System.Runtime.InteropServices;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Translate;
using Lunar.Application.Shell.Models;
using Lunar.Domain.Shared;

namespace Lunar.Windows.Capture;

/// <summary>
/// <see cref="IScreenCapture"/> with a GDI copy of the screen (ADR-0028). GDI honours display affinity, so Lunar's windows, which are excluded from
/// capture (ADR-0019), are not in the picture. The display is the one Windows says holds the foreground window. The pixels go straight into managed
/// memory; nothing is written to disk and only fixed events are logged.
/// </summary>
public sealed class GdiScreenCapture : IScreenCapture
{
    private const string Component = "screen-capture";
    private const int SrcCopy = 0x00CC0020;
    private const int CaptureBlt = 0x40000000;
    private const uint MonitorDefaultToPrimary = 1;
    private const int DibRgbColors = 0;

    private readonly IDiagnosticLog _log;

    public GdiScreenCapture(IDiagnosticLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    public Task<Result<ScreenPicture>> CaptureActiveDisplayAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Task.FromResult(Result.Cancelled<ScreenPicture>());
        return Task.Run(() => Capture(ActiveDisplay()), ct);
    }

    /// <summary>The bounds of the display holding the foreground window, else the primary display.</summary>
    internal static PixelRect? ActiveDisplay()
    {
        var monitor = MonitorFromWindow(GetForegroundWindow(), MonitorDefaultToPrimary);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            return null;
        var r = info.Monitor;
        return new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>A copy of <paramref name="area"/> of the screen. Internal so the proof can capture a known area.</summary>
    internal Result<ScreenPicture> Capture(PixelRect? area)
    {
        if (area is not { Width: > 0, Height: > 0 } bounds)
            return Fail(ErrorCode.DeviceUnavailable, "No display could be read.");

        var screen = IntPtr.Zero;
        var memory = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            screen = GetDC(IntPtr.Zero);
            memory = CreateCompatibleDC(screen);
            bitmap = CreateCompatibleBitmap(screen, bounds.Width, bounds.Height);
            if (screen == IntPtr.Zero || memory == IntPtr.Zero || bitmap == IntPtr.Zero)
                return Fail(ErrorCode.DeviceUnavailable, "No display could be read.");
            previous = SelectObject(memory, bitmap);
            if (!BitBlt(memory, 0, 0, bounds.Width, bounds.Height, screen, bounds.X, bounds.Y, SrcCopy | CaptureBlt))
                return Fail(ErrorCode.AccessDenied, "Windows did not allow Lunar to read the screen.");
            SelectObject(memory, previous);
            previous = IntPtr.Zero;

            var header = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(),
                Width = bounds.Width,
                Height = -bounds.Height, // negative: top row first
                Planes = 1,
                BitCount = 32
            };
            var pixels = new byte[bounds.Width * bounds.Height * 4];
            var lines = GetDIBits(memory, bitmap, 0, (uint)bounds.Height, pixels, ref header, DibRgbColors);
            if (lines != bounds.Height)
            {
                Array.Clear(pixels);
                return Fail(ErrorCode.AccessDenied, "Windows did not allow Lunar to read the screen.");
            }

            // GDI leaves alpha at zero; the picture is opaque.
            for (var i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255;
            return Result.Success(new ScreenPicture(bounds, pixels));
        }
        finally
        {
            if (previous != IntPtr.Zero)
                SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero)
                DeleteObject(bitmap);
            if (memory != IntPtr.Zero)
                DeleteDC(memory);
            if (screen != IntPtr.Zero)
                ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private Result<ScreenPicture> Fail(ErrorCode code, string message)
    {
        _log.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "screen-capture.failed", Component, "capture", code));
        return Result.Failure<ScreenPicture>(new OperationError(code, message, retryable: true));
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
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
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

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfoHeader info, int usage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);
}
