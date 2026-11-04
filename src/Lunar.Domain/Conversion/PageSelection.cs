using System.Globalization;
using System.Text;
using Lunar.Domain.Shared;

namespace Lunar.Domain.Conversion;

/// <summary>One run of pages: <see cref="First"/> to <see cref="Last"/>, or to the last page of the document when <see cref="Last"/> is null.</summary>
public readonly record struct PageRange(int First, int? Last)
{
    public override string ToString() => Last is null ? $"{First}-" : First == Last ? First.ToString(CultureInfo.InvariantCulture) : $"{First}-{Last}";
}

/// <summary>
/// Which pages of a document to convert (PRD section 5): all of them, or the ones named in a list such as "1-3, 5, 8-" (pages 1 to 3,
/// page 5, and page 8 to the end). Pages count from 1. Pure: it turns text into ranges and ranges into page numbers; whether a page
/// exists is decided by <see cref="Resolve"/> once the page count of the document is known. The text that comes back from
/// <see cref="ToString"/> is canonical, so the same choice always reads the same.
/// </summary>
public sealed record PageSelection
{
    public const int MaxPageNumber = 999_999;
    public const int MaxRanges = 200;
    public const int MaxTextLength = 500;

    private PageSelection(IReadOnlyList<PageRange> ranges) => Ranges = ranges;

    /// <summary>Every page (no ranges).</summary>
    public static PageSelection All { get; } = new([]);

    public IReadOnlyList<PageRange> Ranges { get; }

    public bool IsAll => Ranges.Count == 0;

    /// <summary>Builds a selection from ranges; they may overlap or come in any order.</summary>
    public static PageSelection FromRanges(IEnumerable<PageRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        var list = ranges.ToList();
        if (list.Count > MaxRanges)
            throw new ArgumentException($"At most {MaxRanges} ranges are allowed.", nameof(ranges));
        foreach (var range in list)
        {
            if (range.First < 1 || range.First > MaxPageNumber || (range.Last is { } last && (last < range.First || last > MaxPageNumber)))
                throw new ArgumentOutOfRangeException(nameof(ranges), "A range runs from a page of at least 1 to a page that is not before it.");
        }

        return list.Count == 0 ? All : new PageSelection(list);
    }

    /// <summary>
    /// Reads "1-3, 5, 8-". Empty text means every page. On failure <paramref name="problem"/> says what to write instead (it never
    /// repeats the text) and <paramref name="selection"/> is <see cref="All"/>.
    /// </summary>
    public static bool TryParse(string? text, out PageSelection selection, out string? problem)
    {
        selection = All;
        problem = null;
        if (string.IsNullOrWhiteSpace(text))
            return true;

        if (text.Length > MaxTextLength)
            return Reject(out problem, "The list of pages is too long.");

        var ranges = new List<PageRange>();
        foreach (var token in text.Split(','))
        {
            var part = token.Trim();
            if (part.Length == 0)
                return Reject(out problem, "Write page numbers like 1-3, 5, 8-.");

            var dash = part.IndexOf('-', StringComparison.Ordinal);
            if (dash < 0)
            {
                if (!TryPage(part, out var page))
                    return Reject(out problem, "Write page numbers like 1-3, 5, 8-.");
                ranges.Add(new PageRange(page, page));
                continue;
            }

            if (!TryPage(part[..dash].Trim(), out var first))
                return Reject(out problem, "Write page numbers like 1-3, 5, 8-.");

            var rest = part[(dash + 1)..].Trim();
            if (rest.Length == 0)
            {
                ranges.Add(new PageRange(first, null));
            }
            else if (TryPage(rest, out var last) && last >= first)
            {
                ranges.Add(new PageRange(first, last));
            }
            else
            {
                return Reject(out problem, "A range such as 3-5 must go from a lower page to a higher one.");
            }
        }

        if (ranges.Count > MaxRanges)
            return Reject(out problem, "There are too many ranges in the list of pages.");

        selection = new PageSelection(ranges);
        return true;
    }

    /// <summary>
    /// The page numbers this choice means in a document of <paramref name="pageCount"/> pages: ascending, each once. A page the
    /// document does not have is an <see cref="ErrorCode.InvalidInput"/> failure that says how many pages it has.
    /// </summary>
    public Result<IReadOnlyList<int>> Resolve(int pageCount)
    {
        if (pageCount < 1)
            return Result.Failure<IReadOnlyList<int>>(new OperationError(ErrorCode.InvalidInput, "The document has no pages."));

        if (IsAll)
            return Result.Success<IReadOnlyList<int>>([.. Enumerable.Range(1, pageCount)]);

        var pages = new SortedSet<int>();
        foreach (var range in Ranges)
        {
            var last = range.Last ?? pageCount;
            if (range.First > pageCount || last > pageCount)
            {
                return Result.Failure<IReadOnlyList<int>>(new OperationError(
                    ErrorCode.InvalidInput,
                    string.Create(CultureInfo.InvariantCulture, $"This document has {pageCount} page{(pageCount == 1 ? string.Empty : "s")}. Choose pages from 1 to {pageCount}.")));
            }

            for (var page = range.First; page <= last; page++)
                pages.Add(page);
        }

        return Result.Success<IReadOnlyList<int>>([.. pages]);
    }

    /// <summary>The canonical text, for example "1-3,5,8-"; empty for every page.</summary>
    public override string ToString()
    {
        if (IsAll)
            return string.Empty;
        var text = new StringBuilder();
        foreach (var range in Ranges.OrderBy(r => r.First).ThenBy(r => r.Last ?? int.MaxValue))
        {
            if (text.Length > 0)
                text.Append(',');
            text.Append(range);
        }

        return text.ToString();
    }

    private static bool TryPage(string text, out int page)
    {
        page = 0;
        return text.Length is > 0 and <= 7 && text.All(char.IsAsciiDigit)
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out page) && page is >= 1 and <= MaxPageNumber;
    }

    private static bool Reject(out string? problem, string message)
    {
        problem = message;
        return false;
    }
}
