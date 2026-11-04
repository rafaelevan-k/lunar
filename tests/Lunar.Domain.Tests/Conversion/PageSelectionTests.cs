using Lunar.Domain.Conversion;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Domain.Tests.Conversion;

// BE-24: which pages of a document are converted.
public sealed class PageSelectionTests
{
    private static PageSelection Parse(string? text)
    {
        Assert.True(PageSelection.TryParse(text, out var selection, out var problem), problem);
        Assert.Null(problem);
        return selection;
    }

    private static IReadOnlyList<int> Pages(PageSelection selection, int pageCount)
    {
        var result = selection.Resolve(pageCount);
        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        return result.Value;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoTextMeansEveryPage(string? text)
    {
        var selection = Parse(text);

        Assert.True(selection.IsAll);
        Assert.Equal(string.Empty, selection.ToString());
        Assert.Equal([1, 2, 3, 4], Pages(selection, 4));
    }

    [Fact]
    public void AllIsTheSameAsNoRanges()
    {
        Assert.True(PageSelection.All.IsAll);
        Assert.Empty(PageSelection.All.Ranges);
        Assert.True(PageSelection.FromRanges([]).IsAll);
    }

    [Theory]
    [InlineData("3", new[] { 3 })]
    [InlineData("1-3", new[] { 1, 2, 3 })]
    [InlineData("1-3, 5", new[] { 1, 2, 3, 5 })]
    [InlineData("5,1-3", new[] { 1, 2, 3, 5 })]
    [InlineData("2-4, 3-6", new[] { 2, 3, 4, 5, 6 })]
    [InlineData("4,4,4", new[] { 4 })]
    [InlineData(" 8 - 10 ", new[] { 8, 9, 10 })]
    [InlineData("9-", new[] { 9, 10 })]
    [InlineData("1-3,9-", new[] { 1, 2, 3, 9, 10 })]
    [InlineData("10", new[] { 10 })]
    public void RangesBecomeAscendingPagesEachOnce(string text, int[] expected)
    {
        Assert.Equal(expected, Pages(Parse(text), 10));
    }

    [Theory]
    [InlineData("1-3, 5, 8-", "1-3,5,8-")]
    [InlineData("5, 1-3", "1-3,5")]
    [InlineData(" 2 - 2 ", "2")]
    [InlineData("7-", "7-")]
    public void TheCanonicalTextIsSortedAndTidy(string text, string canonical)
    {
        var selection = Parse(text);

        Assert.Equal(canonical, selection.ToString());
        Assert.Equal(Pages(selection, 20), Pages(Parse(canonical), 20));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("-3")]
    [InlineData("a")]
    [InlineData("1,,2")]
    [InlineData("1,")]
    [InlineData(",1")]
    [InlineData("3-1")]
    [InlineData("1--3")]
    [InlineData("1-2-3")]
    [InlineData("1.5")]
    [InlineData("+3")]
    [InlineData("1000000")]
    [InlineData("12345678")]
    [InlineData("1-x")]
    [InlineData("one-two")]
    [InlineData("1;2")]
    public void TextThatIsNotAPageListIsRefusedWithAnActionableMessage(string text)
    {
        Assert.False(PageSelection.TryParse(text, out var selection, out var problem));

        Assert.True(selection.IsAll);
        Assert.False(string.IsNullOrWhiteSpace(problem));
        Assert.Contains("page", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AMessageNeverRepeatsWhatWasTyped()
    {
        PageSelection.TryParse("secret-plans", out _, out var problem);

        Assert.DoesNotContain("secret", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TooLongOrTooManyIsRefused()
    {
        Assert.False(PageSelection.TryParse(new string('1', PageSelection.MaxTextLength + 1), out _, out _));
        Assert.False(PageSelection.TryParse(string.Join(',', Enumerable.Repeat("1", PageSelection.MaxRanges + 1)), out _, out _));
        Assert.True(PageSelection.TryParse(string.Join(',', Enumerable.Repeat("1", 100)), out _, out _));
    }

    [Fact]
    public void APageTheDocumentDoesNotHaveIsRefusedAndSaysHowManyThereAre()
    {
        var past = Parse("3-12").Resolve(10);
        var single = Parse("11").Resolve(10);
        var onePage = Parse("2").Resolve(1);

        foreach (var result in new[] { past, single, onePage })
        {
            Assert.True(result.IsFailure);
            Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        }

        Assert.Contains("10 pages", past.Error!.SafeMessage, StringComparison.Ordinal);
        Assert.Contains("1 page.", onePage.Error!.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOpenEndedRangeNeverPassesTheEnd()
    {
        Assert.Equal([5], Pages(Parse("5-"), 5));
        Assert.Equal([1], Pages(Parse("1-"), 1));
    }

    [Fact]
    public void ADocumentWithoutPagesHasNothingToChoose()
    {
        Assert.True(PageSelection.All.Resolve(0).IsFailure);
        Assert.True(Parse("1").Resolve(0).IsFailure);
        Assert.True(PageSelection.All.Resolve(-3).IsFailure);
    }

    [Fact]
    public void RangesBuiltInCodeAreCheckedToo()
    {
        var selection = PageSelection.FromRanges([new PageRange(4, 6), new PageRange(1, 1)]);

        Assert.Equal([1, 4, 5, 6], Pages(selection, 9));
        Assert.Equal("1,4-6", selection.ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() => PageSelection.FromRanges([new PageRange(0, 3)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PageSelection.FromRanges([new PageRange(5, 3)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PageSelection.FromRanges([new PageRange(1, PageSelection.MaxPageNumber + 1)]));
        Assert.Throws<ArgumentNullException>(() => PageSelection.FromRanges(null!));
        Assert.Throws<ArgumentException>(() => PageSelection.FromRanges(Enumerable.Repeat(new PageRange(1, 1), PageSelection.MaxRanges + 1)));
    }

    [Fact]
    public void AHugeDocumentIsResolvedWithoutTrouble()
    {
        var pages = Pages(Parse("1-5, 99990-"), 100_000);

        Assert.Equal(5 + 11, pages.Count);
        Assert.Equal(100_000, pages[^1]);
    }
}
