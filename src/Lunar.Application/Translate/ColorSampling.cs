using Lunar.Application.Abstractions.Translate;

namespace Lunar.Application.Translate;

/// <summary>An opaque colour.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>WCAG relative luminance.</summary>
    public double Luminance => (0.2126 * Channel(R)) + (0.7152 * Channel(G)) + (0.0722 * Channel(B));

    public static double Contrast(Rgb a, Rgb b)
    {
        var la = a.Luminance;
        var lb = b.Luminance;
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Channel(byte value)
    {
        var v = value / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }
}

/// <summary>
/// The colours a translated block is drawn in (ADR-0028 §11): the background is the most common colour in a thin ring around the original text, the
/// text colour is what stands out most from it inside the box, corrected towards black or white until it has at least 4.5:1 contrast. Pure.
/// </summary>
public static class ColorSampling
{
    public const double MinimumTextContrast = 4.5;

    public static (Rgb Background, Rgb Foreground) Sample(ScreenPicture picture, TextBox box)
    {
        ArgumentNullException.ThrowIfNull(picture);
        var background = MostCommon(picture, Ring(picture, box));
        var inside = Pixels(picture, Clamp(picture, box), step: Math.Max(1, (int)(box.Height / 12)));
        var far = inside.OrderByDescending(c => Distance(c, background)).Take(Math.Max(1, inside.Count / 10)).ToList();
        var foreground = far.Count == 0 || Distance(far[0], background) < 30
            ? (background.Luminance < 0.5 ? new Rgb(255, 255, 255) : new Rgb(0, 0, 0))
            : Average(far);
        return (background, Readable(foreground, background));
    }

    /// <summary>
    /// The colour moved toward black or white (whichever stands out more on <paramref name="background"/>) in small steps until it reaches
    /// <see cref="MinimumTextContrast"/>; on any background one of the two always does.
    /// </summary>
    public static Rgb Readable(Rgb text, Rgb background)
    {
        var white = new Rgb(255, 255, 255);
        var black = new Rgb(0, 0, 0);
        var target = Rgb.Contrast(white, background) >= Rgb.Contrast(black, background) ? white : black; // the extreme that can reach it
        var color = text;
        for (var step = 1; step <= 20 && Rgb.Contrast(color, background) < MinimumTextContrast; step++)
            color = Mix(text, target, step / 20.0);
        return color;
    }

    private static IEnumerable<(int X, int Y)> Ring(ScreenPicture picture, TextBox box)
    {
        var pad = Math.Max(2, (int)(box.Height * 0.15));
        var left = Math.Max(0, (int)box.X - pad);
        var top = Math.Max(0, (int)box.Y - pad);
        var right = Math.Min(picture.Width - 1, (int)Math.Ceiling(box.Right) + pad);
        var bottom = Math.Min(picture.Height - 1, (int)Math.Ceiling(box.Bottom) + pad);
        var step = Math.Max(1, (right - left) / 60);
        for (var x = left; x <= right; x += step)
        {
            yield return (x, top);
            yield return (x, bottom);
        }

        for (var y = top; y <= bottom; y += Math.Max(1, (bottom - top) / 10))
        {
            yield return (left, y);
            yield return (right, y);
        }
    }

    private static Rgb MostCommon(ScreenPicture picture, IEnumerable<(int X, int Y)> points)
    {
        var counts = new Dictionary<int, (int Count, long R, long G, long B)>();
        foreach (var (x, y) in points)
        {
            var c = At(picture, x, y);
            var key = ((c.R >> 4) << 8) | ((c.G >> 4) << 4) | (c.B >> 4);
            counts.TryGetValue(key, out var v);
            counts[key] = (v.Count + 1, v.R + c.R, v.G + c.G, v.B + c.B);
        }

        if (counts.Count == 0)
            return new Rgb(255, 255, 255);
        var best = counts.Values.MaxBy(v => v.Count);
        return new Rgb((byte)(best.R / best.Count), (byte)(best.G / best.Count), (byte)(best.B / best.Count));
    }

    private static List<Rgb> Pixels(ScreenPicture picture, (int Left, int Top, int Right, int Bottom) area, int step)
    {
        var list = new List<Rgb>();
        for (var y = area.Top; y <= area.Bottom; y += step)
        {
            for (var x = area.Left; x <= area.Right; x += step)
                list.Add(At(picture, x, y));
        }

        return list;
    }

    private static (int, int, int, int) Clamp(ScreenPicture picture, TextBox box) =>
        (Math.Clamp((int)box.X, 0, picture.Width - 1), Math.Clamp((int)box.Y, 0, picture.Height - 1),
         Math.Clamp((int)Math.Ceiling(box.Right), 0, picture.Width - 1), Math.Clamp((int)Math.Ceiling(box.Bottom), 0, picture.Height - 1));

    private static Rgb At(ScreenPicture picture, int x, int y)
    {
        var i = ((y * picture.Width) + x) * 4;
        return new Rgb(picture.Pixels[i + 2], picture.Pixels[i + 1], picture.Pixels[i]);
    }

    private static int Distance(Rgb a, Rgb b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

    private static Rgb Average(List<Rgb> colors) => new(
        (byte)colors.Average(c => c.R), (byte)colors.Average(c => c.G), (byte)colors.Average(c => c.B));

    private static Rgb Mix(Rgb from, Rgb to, double amount) => new(
        (byte)Math.Round(from.R + ((to.R - from.R) * amount)),
        (byte)Math.Round(from.G + ((to.G - from.G) * amount)),
        (byte)Math.Round(from.B + ((to.B - from.B) * amount)));
}
