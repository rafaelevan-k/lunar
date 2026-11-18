using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lunar.Desktop.Features.MediaControls;

/// <summary>
/// The colour of the media pill's bars (ADR-0026 §9): the artwork's main lively colour, lifted until it stands out from the pill. Pure apart from reading
/// the frozen bitmap's pixels.
/// </summary>
internal static class ArtworkTint
{
    /// <summary>Contrast the bars keep against the pill background (WCAG non-text contrast).</summary>
    internal const double MinimumContrast = 3.0;

    private const int Sample = 24;

    /// <summary>The saturation-weighted average of the artwork's colourful pixels, or null for grey, very dark or unreadable artwork.</summary>
    public static Color? Dominant(BitmapSource? artwork)
    {
        if (artwork is null || artwork.PixelWidth <= 0 || artwork.PixelHeight <= 0)
            return null;

        byte[] pixels;
        try
        {
            var small = new TransformedBitmap(artwork, new ScaleTransform((double)Sample / artwork.PixelWidth, (double)Sample / artwork.PixelHeight));
            var bgra = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
            bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }

        return Dominant(pixels);
    }

    /// <summary>As <see cref="Dominant(BitmapSource)"/>, over BGRA bytes.</summary>
    internal static Color? Dominant(ReadOnlySpan<byte> bgra)
    {
        double r = 0, g = 0, b = 0, weight = 0;
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            if (bgra[i + 3] < 128)
                continue;
            var (saturation, value) = SaturationValue(bgra[i + 2], bgra[i + 1], bgra[i]);
            if (saturation < 0.25 || value < 0.2)
                continue;
            var w = saturation * saturation;
            r += bgra[i + 2] * w;
            g += bgra[i + 1] * w;
            b += bgra[i] * w;
            weight += w;
        }

        return weight <= 0 ? null : Color.FromRgb((byte)Math.Round(r / weight), (byte)Math.Round(g / weight), (byte)Math.Round(b / weight));
    }

    /// <summary>The tint moved toward white (on a dark pill) or black (on a light pill) in small steps until it reaches <paramref name="minimum"/>.</summary>
    public static Color Lift(Color tint, Color background, double minimum = MinimumContrast)
    {
        var target = Luminance(background) < 0.5 ? Colors.White : Colors.Black;
        var color = tint;
        for (var step = 0; step <= 20 && Contrast(color, background) < minimum; step++)
            color = Mix(tint, target, step / 20.0);
        return color;
    }

    /// <summary>WCAG contrast ratio of two opaque colours.</summary>
    public static double Contrast(Color a, Color b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c) => (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));

    private static double Channel(byte value)
    {
        var v = value / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static Color Mix(Color from, Color to, double amount) => Color.FromRgb(
        (byte)Math.Round(from.R + ((to.R - from.R) * amount)),
        (byte)Math.Round(from.G + ((to.G - from.G) * amount)),
        (byte)Math.Round(from.B + ((to.B - from.B) * amount)));

    private static (double Saturation, double Value) SaturationValue(byte r, byte g, byte b)
    {
        var max = Math.Max(r, Math.Max(g, b)) / 255.0;
        var min = Math.Min(r, Math.Min(g, b)) / 255.0;
        return (max <= 0 ? 0 : (max - min) / max, max);
    }
}
