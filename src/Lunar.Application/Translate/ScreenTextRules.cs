using System.Text;
using Lunar.Application.Abstractions.Translate;

namespace Lunar.Application.Translate;

/// <summary>A paragraph-like group of recognised lines, translated as one text and drawn over its box (ADR-0028 §5).</summary>
public sealed record RecognizedBlock(IReadOnlyList<OcrLine> Lines, TextBox Box, string Text, double LineHeight, bool IsJapanese);

/// <summary>Pure rules that turn OCR lines into blocks (ADR-0028 §3, §5, §6).</summary>
public static class ScreenTextRules
{
    /// <summary>Hiragana, katakana (also half width) and CJK ideographs.</summary>
    public static bool IsJapanese(char c) =>
        c is >= '぀' and <= 'ヿ' or >= '㐀' and <= '䶿' or >= '一' and <= '鿿' or >= 'ｦ' and <= 'ﾟ' or '々' or '〆';

    /// <summary>True when at least a third of the letters are Japanese characters.</summary>
    public static bool LooksJapanese(string text)
    {
        var letters = 0;
        var japanese = 0;
        foreach (var c in text)
        {
            if (IsJapanese(c))
            {
                japanese++;
                letters++;
            }
            else if (char.IsLetter(c))
            {
                letters++;
            }
        }

        return japanese > 0 && japanese * 3 >= letters;
    }

    /// <summary>Removes the spaces Windows OCR puts between Japanese characters ("東 京" → "東京"); spaces next to Latin text stay.</summary>
    public static string JoinJapanese(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                var before = builder.Length > 0 ? builder[^1] : '\0';
                var j = i;
                while (j < text.Length && char.IsWhiteSpace(text[j]))
                    j++;
                var after = j < text.Length ? text[j] : '\0';
                if (IsJapaneseOrPunctuation(before) && IsJapaneseOrPunctuation(after))
                {
                    i = j - 1;
                    continue;
                }
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Combines the two recognisers' results on one screen: lines the Japanese recogniser found with Japanese characters win; English-recogniser lines
    /// are kept where they do not cover such a line. With only one result, that result.
    /// </summary>
    public static IReadOnlyList<OcrLine> Merge(IReadOnlyList<OcrLine> english, IReadOnlyList<OcrLine> japanese)
    {
        var chosen = japanese.Where(l => LooksJapanese(l.Text)).Select(l => l with { Text = JoinJapanese(l.Text) }).ToList();
        var boxes = chosen.Select(l => l.Box).ToList();
        foreach (var line in english)
        {
            var box = line.Box;
            if (!boxes.Any(b => b.OverlapOfSmaller(box) > 0.3))
                chosen.Add(line);
        }

        return chosen;
    }

    /// <summary>
    /// Groups lines into blocks in reading order: a line joins the block above it when it starts near the same left edge (or overlaps it widely), has a
    /// similar height and follows within about one line's height. Lines without any letter (numbers, symbols) are left out: they need no translation.
    /// </summary>
    public static IReadOnlyList<RecognizedBlock> Group(IReadOnlyList<OcrLine> lines)
    {
        var usable = lines.Where(l => l.Words.Count > 0 && l.Text.Any(char.IsLetter) && l.Box.Height > 0)
            .OrderBy(l => l.Box.Y).ThenBy(l => l.Box.X).ToList();
        var groups = new List<List<OcrLine>>();
        foreach (var line in usable)
        {
            var box = line.Box;
            List<OcrLine>? home = null;
            foreach (var group in groups)
            {
                var last = group[^1].Box;
                var height = Math.Max(last.Height, box.Height);
                var similar = Math.Max(last.Height, box.Height) / Math.Max(1, Math.Min(last.Height, box.Height)) < 1.4;
                var gap = box.Y - last.Bottom;
                var aligned = Math.Abs(box.X - last.X) <= height * 0.8 || HorizontalOverlap(last, box) > 0.6;
                if (similar && aligned && gap >= -height * 0.3 && gap <= height * 0.9)
                {
                    home = group;
                    break;
                }
            }

            if (home is null)
                groups.Add(new List<OcrLine> { line });
            else
                home.Add(line);
        }

        return groups.Select(ToBlock).OrderBy(b => b.Box.Y).ThenBy(b => b.Box.X).ToArray();
    }

    private static RecognizedBlock ToBlock(List<OcrLine> lines)
    {
        var box = lines.Skip(1).Aggregate(lines[0].Box, (b, l) => b.Union(l.Box));
        var japanese = lines.Any(l => LooksJapanese(l.Text));
        var text = string.Join(japanese ? string.Empty : " ", lines.Select(l => l.Text.Trim()));
        var heights = lines.Select(l => l.Box.Height).Order().ToList();
        return new RecognizedBlock(lines, box, text, heights[heights.Count / 2], japanese);
    }

    private static double HorizontalOverlap(TextBox a, TextBox b)
    {
        var w = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
        return w <= 0 ? 0 : w / Math.Max(1, Math.Min(a.Width, b.Width));
    }

    private static bool IsJapaneseOrPunctuation(char c) => IsJapanese(c) || c is >= '　' and <= '〿' or >= '！' and <= '／';
}
