using Lunar.Application.Shell.Models;
using Lunar.Domain.Shared;

namespace Lunar.Application.Abstractions.Translate;

/// <summary>
/// One still picture of a display (ADR-0028): 32-bit BGRA pixels, top row first, in the display's physical pixels. It lives only in memory and is
/// never written to disk; <see cref="Clear"/> wipes the pixels as soon as the overlay closes.
/// </summary>
public sealed class ScreenPicture
{
    public ScreenPicture(PixelRect bounds, byte[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(bounds), "A picture needs a size.");
        if (pixels.Length != bounds.Width * bounds.Height * 4)
            throw new ArgumentException("The pixels do not match the size.", nameof(pixels));
        Bounds = bounds;
        Pixels = pixels;
    }

    /// <summary>Where the picture was on the virtual screen, in physical pixels.</summary>
    public PixelRect Bounds { get; }

    public int Width => Bounds.Width;

    public int Height => Bounds.Height;

    /// <summary>BGRA, <see cref="Width"/> × 4 bytes per row.</summary>
    public byte[] Pixels { get; }

    /// <summary>True once <see cref="Clear"/> ran.</summary>
    public bool IsCleared { get; private set; }

    /// <summary>Overwrites the pixels, so nothing of the screen stays in memory longer than needed.</summary>
    public void Clear()
    {
        Array.Clear(Pixels);
        IsCleared = true;
    }
}

/// <summary>Takes the picture that screen translation reads (ADR-0028). Only ever called after the user asked for a translation.</summary>
public interface IScreenCapture
{
    /// <summary>
    /// One picture of the display that holds the foreground window. Lunar's own windows are not in it (ADR-0019). Fails with
    /// <see cref="ErrorCode.DeviceUnavailable"/> when no display can be read and <see cref="ErrorCode.AccessDenied"/> when Windows refuses the copy
    /// (for example on the secure desktop). Runs off the calling thread.
    /// </summary>
    Task<Result<ScreenPicture>> CaptureActiveDisplayAsync(CancellationToken ct);
}
