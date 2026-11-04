using Lunar.Application.Conversion.Models;
using Lunar.Conversion.Output;
using Lunar.Conversion.Pipeline;
using Lunar.Conversion.Tests.Support;
using Lunar.Domain.Conversion;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Conversion.Tests.Output;

// BE-24 amendment to BE-26: pages that are only some of a document's pages keep the document's own numbers in their names.
public sealed class OutputCommitterNumberingTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly TempWorkspace _workspace;
    private readonly ConversionLimits _limits = new();

    public OutputCommitterNumberingTests() => _workspace = TempWorkspace.Create(Path.Combine(_folder.Root, "work"));

    public void Dispose()
    {
        _workspace.Dispose();
        _folder.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PlannedConversion PagedJob(string baseName = "doc", bool replaces = false, bool renamed = false) => new(
        0, _folder.Write("source.pdf", Fixtures.Pdf(3)), FileFormat.Pdf, 1, DateTime.UtcNow,
        ConversionPolicy.Resolve(FileFormat.Pdf, FileFormat.Png), _folder.Out, baseName, ".png", renamed, replaces);

    private string[] Stage(int count) => [.. Enumerable.Range(0, count).Select(i =>
    {
        var path = _workspace.NewFilePath(".png");
        File.WriteAllBytes(path, Fixtures.Png((uint)(i + 2), 2));
        return path;
    })];

    private string[] OutFiles() => [.. Directory.GetFiles(_folder.Out).Select(f => Path.GetFileName(f)).OrderBy(n => n, StringComparer.Ordinal)];

    [Fact]
    public async Task ChosenPagesAreNamedWithTheirOwnNumbers()
    {
        var result = await new OutputCommitter().CommitAsync(PagedJob(), _workspace, Stage(3), new PageNumbering([3, 5, 7], 9), _limits, Ct);

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal(["doc_page_003.png", "doc_page_005.png", "doc_page_007.png"], result.Value.Select(Path.GetFileName));
    }

    [Theory]
    [InlineData(99, "doc_page_007.png")]
    [InlineData(999, "doc_page_007.png")]
    [InlineData(1000, "doc_page_0007.png")]
    [InlineData(123456, "doc_page_000007.png")]
    public async Task TheNumberIsAsWideAsTheWholeDocumentNeeds(int documentPages, string expected)
    {
        var result = await new OutputCommitter().CommitAsync(PagedJob(), _workspace, Stage(1), new PageNumbering([7], documentPages), _limits, Ct);

        Assert.Equal(expected, Path.GetFileName(Assert.Single(result.Value)));
    }

    [Fact]
    public async Task WithoutNumberingTheOldBehaviourStays()
    {
        var result = await new OutputCommitter().CommitAsync(PagedJob(), _workspace, Stage(2), _limits, Ct);

        Assert.Equal(["doc_page_001.png", "doc_page_002.png"], result.Value.Select(Path.GetFileName));
    }

    [Fact]
    public async Task ATakenPageMovesTheWholeSetButKeepsTheNumbers()
    {
        File.WriteAllBytes(Path.Combine(_folder.Out, "doc_page_005.png"), [1, 2, 3]);

        var result = await new OutputCommitter().CommitAsync(PagedJob(), _workspace, Stage(3), new PageNumbering([3, 5, 7], 9), _limits, Ct);

        Assert.Equal(["doc (2)_page_003.png", "doc (2)_page_005.png", "doc (2)_page_007.png"], result.Value.Select(Path.GetFileName));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(_folder.Out, "doc_page_005.png")));
    }

    [Fact]
    public async Task OverwriteReplacesOnlyTheChosenPagesThatExist()
    {
        File.WriteAllBytes(Path.Combine(_folder.Out, "doc_page_005.png"), Fixtures.Png(30, 30));
        File.WriteAllBytes(Path.Combine(_folder.Out, "doc_page_006.png"), [9, 9]);

        var result = await new OutputCommitter().CommitAsync(PagedJob(replaces: true), _workspace, Stage(2), new PageNumbering([3, 5], 9), _limits, Ct);

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal(["doc_page_003.png", "doc_page_005.png", "doc_page_006.png"], OutFiles());
        Assert.Equal([9, 9], File.ReadAllBytes(Path.Combine(_folder.Out, "doc_page_006.png"))); // a page that was not converted is left alone
    }

    [Theory]
    [MemberData(nameof(WrongNumberings))]
    public async Task NumberingThatDoesNotMatchThePicturesIsRefusedAndNothingIsWritten(int staged, int[] numbers, int documentPages)
    {
        var result = await new OutputCommitter().CommitAsync(PagedJob(), _workspace, Stage(staged), new PageNumbering(numbers, documentPages), _limits, Ct);

        Assert.Equal(ErrorCode.Unexpected, result.Error!.Code);
        Assert.Empty(OutFiles());
    }

    public static TheoryData<int, int[], int> WrongNumberings => new()
    {
        { 2, [3], 9 },               // fewer numbers than pictures
        { 1, [3, 5], 9 },            // more numbers than pictures
        { 2, [5, 3], 9 },            // not ascending
        { 2, [3, 3], 9 },            // the same page twice
        { 2, [0, 3], 9 },            // pages start at 1
        { 2, [3, 10], 9 },           // past the end of the document
        { 1, [3], 0 },               // no pages in the document
        { 1, [3], 1_000_000 }        // more pages than a name has room for
    };

    [Fact]
    public async Task NumberingForARouteWithOneOutputIsRefused()
    {
        var single = new PlannedConversion(0, _folder.Write("a.png", Fixtures.Png()), FileFormat.Png, 1, DateTime.UtcNow,
            ConversionPolicy.Resolve(FileFormat.Png, FileFormat.Jpg), _folder.Out, "a", ".jpg", false, false);
        var staged = _workspace.NewFilePath(".jpg");
        File.WriteAllBytes(staged, Fixtures.Jpeg());

        var result = await new OutputCommitter().CommitAsync(single, _workspace, [staged], new PageNumbering([1], 1), _limits, Ct);

        Assert.Equal(ErrorCode.Unexpected, result.Error!.Code);
        Assert.Empty(OutFiles());
    }

    [Fact]
    public void ThePageNumberingChecksItselfAgainstTheNumberOfPictures()
    {
        Assert.True(new PageNumbering([1, 2, 3], 3).IsValidFor(3));
        Assert.True(new PageNumbering([2], 999_999).IsValidFor(1));
        Assert.False(new PageNumbering([1, 2], 3).IsValidFor(3));
        Assert.False(new PageNumbering(null!, 3).IsValidFor(0));
    }
}
