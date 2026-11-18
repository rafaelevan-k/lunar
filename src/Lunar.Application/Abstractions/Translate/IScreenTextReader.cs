using Lunar.Domain.Shared;

namespace Lunar.Application.Abstractions.Translate;

/// <summary>A rectangle in a picture's pixels.</summary>
public readonly record struct TextBox(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    /// <summary>The smallest box around both.</summary>
    public TextBox Union(TextBox other)
    {
        var x = Math.Min(X, other.X);
        var y = Math.Min(Y, other.Y);
        return new TextBox(x, y, Math.Max(Right, other.Right) - x, Math.Max(Bottom, other.Bottom) - y);
    }

    /// <summary>Area shared with <paramref name="other"/> divided by the smaller area; 0 when apart.</summary>
    public double OverlapOfSmaller(TextBox other)
    {
        var w = Math.Min(Right, other.Right) - Math.Max(X, other.X);
        var h = Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y);
        if (w <= 0 || h <= 0)
            return 0;
        var smaller = Math.Min(Width * Height, other.Width * other.Height);
        return smaller <= 0 ? 0 : (w * h) / smaller;
    }
}

/// <summary>One recognised word and where it is.</summary>
public sealed record OcrWord(string Text, TextBox Box);

/// <summary>One recognised line, its words in reading order.</summary>
public sealed record OcrLine(string Text, IReadOnlyList<OcrWord> Words)
{
    /// <summary>The box around all the words.</summary>
    public TextBox Box => Words.Count == 0 ? default : Words.Skip(1).Aggregate(Words[0].Box, (box, word) => box.Union(word.Box));
}

/// <summary>The recognisers Lunar uses (ADR-0028): English (also read for Indonesian, which uses the same letters) and Japanese.</summary>
public enum OcrLanguage
{
    English,
    Japanese
}

/// <summary>Reads text from a picture on this PC with Windows' own OCR (ADR-0028). Nothing leaves the PC here.</summary>
public interface IScreenTextReader
{
    /// <summary>The recognisers installed in Windows, of those Lunar uses.</summary>
    IReadOnlyList<OcrLanguage> AvailableLanguages { get; }

    /// <summary>
    /// The lines one recogniser finds. Fails with <see cref="ErrorCode.DependencyMissing"/> when that recogniser is not installed. Runs off the calling
    /// thread and is cancellable between tiles.
    /// </summary>
    Task<Result<IReadOnlyList<OcrLine>>> ReadAsync(ScreenPicture picture, OcrLanguage language, CancellationToken ct);
}
