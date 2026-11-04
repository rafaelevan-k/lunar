namespace Lunar.Conversion.Output;

/// <summary>
/// Which page of its document each staged picture is, so the output names keep the document's own numbers ("name_page_003.png" for page 3)
/// even when only some pages were converted, and so the number is as wide as the whole document needs (three digits up to 999 pages, then four).
/// </summary>
/// <param name="PageNumbers">The page number of each staged picture, ascending, counting from 1.</param>
/// <param name="DocumentPageCount">The number of pages in the whole document.</param>
public sealed record PageNumbering(IReadOnlyList<int> PageNumbers, int DocumentPageCount)
{
    /// <summary>The widest page number the name pattern has room for (the planner keeps six digits free).</summary>
    public const int MaxDocumentPages = 999_999;

    /// <summary>True when there is one number for each of <paramref name="stagedCount"/> pictures, strictly ascending and inside the document.</summary>
    public bool IsValidFor(int stagedCount)
    {
        if (PageNumbers is null || PageNumbers.Count != stagedCount || DocumentPageCount < 1 || DocumentPageCount > MaxDocumentPages)
            return false;
        var previous = 0;
        foreach (var number in PageNumbers)
        {
            if (number <= previous || number > DocumentPageCount)
                return false;
            previous = number;
        }

        return true;
    }
}
