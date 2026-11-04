using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Conversion.Models;
using Lunar.Conversion.Output;
using Lunar.Conversion.Pipeline;
using Lunar.Conversion.Tests.Support;
using Lunar.Domain.Conversion;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Conversion.Tests.Output;

// BE-26: results reach the user's folder complete or not at all, and the user's own files survive every kind of interruption.
public sealed class OutputCommitterTests : IDisposable
{
    private sealed class RecordingLog : IDiagnosticLog
    {
        public List<DiagnosticEvent> Events { get; } = [];

        public void Write(DiagnosticEvent diagnosticEvent)
        {
            lock (Events)
                Events.Add(diagnosticEvent);
        }
    }

    private readonly TempFolder _folder = new();
    private readonly List<TempWorkspace> _workspaces = [];
    private readonly ConversionLimits _limits = new();

    public void Dispose()
    {
        foreach (var workspace in _workspaces)
            workspace.Dispose();
        _folder.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TempWorkspace NewWorkspace()
    {
        var workspace = TempWorkspace.Create(Path.Combine(_folder.Root, "work"));
        _workspaces.Add(workspace);
        return workspace;
    }

    private string SourceFile(string name = "photo.png") => _folder.Write(name, Fixtures.Png());

    private PlannedConversion SingleJob(string source, string baseName = "photo", bool wasRenamed = false, bool replaces = false, FileFormat target = FileFormat.Jpg) => new(
        0, source, FileFormat.Png, new FileInfo(source).Length, File.GetLastWriteTimeUtc(source),
        ConversionPolicy.Resolve(FileFormat.Png, target), _folder.Out, baseName, FileFormats.Extension(target), wasRenamed, replaces);

    private PlannedConversion PagedJob(string source, string baseName = "doc", bool replaces = false) => new(
        0, source, FileFormat.Pdf, new FileInfo(source).Length, File.GetLastWriteTimeUtc(source),
        ConversionPolicy.Resolve(FileFormat.Pdf, FileFormat.Png), _folder.Out, baseName, ".png", false, replaces);

    private static string Stage(TempWorkspace workspace, byte[] bytes, string extension)
    {
        var path = workspace.NewFilePath(extension);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static async Task<Result<IReadOnlyList<string>>> Commit(OutputCommitter committer, PlannedConversion job, TempWorkspace workspace, params string[] staged) =>
        await committer.CommitAsync(job, workspace, staged, new ConversionLimits(), Ct);

    private string[] OutFiles() => [.. Directory.GetFileSystemEntries(_folder.Out).Select(path => Path.GetFileName(path)).OrderBy(n => n, StringComparer.Ordinal)];

    private static byte[] Jpeg(byte marker = 0) => [.. Fixtures.Jpeg(), .. new byte[marker]];

    // ---- the normal path ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AResultIsPublishedCompleteUnderItsPlannedNameAndNothingElseIsLeftBehind()
    {
        var source = SourceFile();
        var workspace = NewWorkspace();
        var bytes = Fixtures.Jpeg();
        var staged = Stage(workspace, bytes, ".jpg");

        var result = await Commit(new OutputCommitter(), SingleJob(source), workspace, staged);

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        var final = Assert.Single(result.Value);
        Assert.Equal(Path.Combine(_folder.Out, "photo.jpg"), final);
        Assert.Equal(bytes, File.ReadAllBytes(final));
        Assert.Equal(["photo.jpg"], OutFiles()); // no part file, no journal, no staging
    }

    [Fact]
    public async Task APagedResultIsPublishedAsNumberedFilesInPageOrder()
    {
        var source = _folder.Write("doc.pdf", Fixtures.Pdf(pages: 3));
        var workspace = NewWorkspace();
        var pages = Enumerable.Range(1, 3).Select(i => Fixtures.Png((uint)(i * 2), 2)).ToArray();
        var staged = pages.Select(b => Stage(workspace, b, ".png")).ToArray();

        var result = await Commit(new OutputCommitter(), PagedJob(source), workspace, staged);

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal(["doc_page_001.png", "doc_page_002.png", "doc_page_003.png"], result.Value.Select(Path.GetFileName));
        for (var i = 0; i < 3; i++)
            Assert.Equal(pages[i], File.ReadAllBytes(result.Value[i]));
        Assert.Equal(3, OutFiles().Length);
    }

    [Fact]
    public async Task ALargeResultIsCopiedExactly()
    {
        var source = SourceFile();
        var workspace = NewWorkspace();
        var big = new byte[5 * 1024 * 1024];
        new Random(3).NextBytes(big);
        var docx = Fixtures.Docx(("word/media/big.bin", big));
        var staged = Stage(workspace, docx, ".docx");
        var job = new PlannedConversion(0, source, FileFormat.Png, 1, DateTime.UtcNow, ConversionPolicy.Resolve(FileFormat.Png, FileFormat.Docx),
            _folder.Out, "big", ".docx", false, false);

        var result = await Commit(new OutputCommitter(), job, workspace, staged);

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal(docx, File.ReadAllBytes(result.Value[0]));
    }

    [Fact]
    public async Task AUnicodeAndLongNameIsPublished()
    {
        var source = SourceFile();
        var workspace = NewWorkspace();
        var name = "foto ürün 写真 " + new string('x', 150);

        var result = await Commit(new OutputCommitter(), SingleJob(source, name), workspace, Stage(workspace, Fixtures.Jpeg(), ".jpg"));

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.True(File.Exists(Path.Combine(_folder.Out, name + ".jpg")));
    }

    // ---- names taken -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFileThatAppearedAfterPlanningIsNeverOverwrittenAndTheResultTakesTheNextName()
    {
        var source = SourceFile();
        var workspace = NewWorkspace();
        var theirs = _folder.Write("photo.jpg", [9, 9, 9], _folder.Out);

        var result = await Commit(new OutputCommitter(), SingleJob(source), workspace, Stage(workspace, Fixtures.Jpeg(), ".jpg"));

        Assert.Equal(Path.Combine(_folder.Out, "photo (2).jpg"), Assert.Single(result.Value));
        Assert.Equal([9, 9, 9], File.ReadAllBytes(theirs));
    }

    [Fact]
    public async Task ANameThePlanAlreadyMovedAsideContinuesFromItsOwnNumber()
    {
        var source = SourceFile();
        var workspace = NewWorkspace();
        _folder.Write("photo (2).jpg", [1], _folder.Out);

        var result = await Commit(new OutputCommitter(), SingleJob(source, "photo (2)", wasRenamed: true), workspace, Stage(workspace, Fixtures.Jpeg(), ".jpg"));

        Assert.Equal("photo (3).jpg", Path.GetFileName(Assert.Single(result.Value)));
    }

    [Fact]
    public async Task AFolderWithTheOutputNameIsRespected()
    {
        var source = SourceFile();
        var workspace = NewWorkspace();
        Directory.CreateDirectory(Path.Combine(_folder.Out, "photo.jpg"));

        var result = await Commit(new OutputCommitter(), SingleJob(source), workspace, Stage(workspace, Fixtures.Jpeg(), ".jpg"));

        Assert.Equal("photo (2).jpg", Path.GetFileName(Assert.Single(result.Value)));
        Assert.True(Directory.Exists(Path.Combine(_folder.Out, "photo.jpg")));
    }

    [Fact]
    public async Task TheInputFileIsNeverTheOutputEvenWhenTheOutputFolderIsTheInputFolder()
    {
        var source = SourceFile("photo.png");
        var inputBytes = File.ReadAllBytes(source);
        var workspace = NewWorkspace();
        var job = new PlannedConversion(0, source, FileFormat.Png, inputBytes.Length, File.GetLastWriteTimeUtc(source),
            ConversionPolicy.Resolve(FileFormat.Png, FileFormat.Png), _folder.In, "photo", ".png", false, ReplacesExisting: true);

        var result = await Commit(new OutputCommitter(), job, workspace, Stage(workspace, Fixtures.Png(8, 8), ".png"));

        Assert.Equal("photo (2).png", Path.GetFileName(Assert.Single(result.Value)));
        Assert.Equal(inputBytes, File.ReadAllBytes(source));
    }

    [Fact]
    public async Task OverwriteReplacesTheExistingOutputAtomicallyWhenThePlanSaidSo()
    {
        var source = SourceFile();
        var workspace = NewWorkspace();
        var existing = _folder.Write("photo.jpg", Jpeg(7), _folder.Out);
        var fresh = Fixtures.Jpeg();

        var result = await Commit(new OutputCommitter(), SingleJob(source, replaces: true), workspace, Stage(workspace, fresh, ".jpg"));

        Assert.Equal(existing, Assert.Single(result.Value));
        Assert.Equal(fresh, File.ReadAllBytes(existing));
        Assert.Equal(["photo.jpg"], OutFiles());
    }

    [Fact]
    public async Task WithoutTheOverwriteChoiceAnExistingOutputIsNeverReplaced()
    {
        var source = SourceFile();
        var workspace = NewWorkspace();
        var existing = _folder.Write("photo.jpg", Jpeg(7), _folder.Out);
        var before = File.ReadAllBytes(existing);

        var result = await Commit(new OutputCommitter(), SingleJob(source, replaces: false), workspace, Stage(workspace, Fixtures.Jpeg(), ".jpg"));

        Assert.Equal("photo (2).jpg", Path.GetFileName(Assert.Single(result.Value)));
        Assert.Equal(before, File.ReadAllBytes(existing));
    }

    [Fact]
    public async Task AMovedAsideNameIsNotOverwrittenEvenWhenOverwriteWasChosen()
    {
        var source = SourceFile();
        var workspace = NewWorkspace();
        _folder.Write("photo.jpg", [1], _folder.Out);
        _folder.Write("photo (2).jpg", [2], _folder.Out);
        var job = SingleJob(source, "photo", replaces: true);
        // Something takes "photo" as a folder between planning and commit, so the first name is unusable.
        File.Delete(Path.Combine(_folder.Out, "photo.jpg"));
        Directory.CreateDirectory(Path.Combine(_folder.Out, "photo.jpg"));

        var result = await Commit(new OutputCommitter(), job, workspace, Stage(workspace, Fixtures.Jpeg(), ".jpg"));

        Assert.Equal("photo (3).jpg", Path.GetFileName(Assert.Single(result.Value)));
        Assert.Equal([2], File.ReadAllBytes(Path.Combine(_folder.Out, "photo (2).jpg")));
    }

    [Fact]
    public async Task OnePagedNameTakenMovesTheWholeSetToTheNextBaseName()
    {
        var source = _folder.Write("doc.pdf", Fixtures.Pdf(pages: 3));
        var workspace = NewWorkspace();
        var theirs = _folder.Write("doc_page_002.png", [5, 5], _folder.Out);
        var staged = Enumerable.Range(0, 3).Select(i => Stage(workspace, Fixtures.Png((uint)(i + 2), 2), ".png")).ToArray();

        var result = await Commit(new OutputCommitter(), PagedJob(source), workspace, staged);

        Assert.Equal(["doc (2)_page_001.png", "doc (2)_page_002.png", "doc (2)_page_003.png"], result.Value.Select(Path.GetFileName));
        Assert.Equal([5, 5], File.ReadAllBytes(theirs));
        Assert.Equal(4, OutFiles().Length);
    }

    [Fact]
    public async Task OverwriteOfAPagedSetReplacesTheExistingPagesAndCreatesTheRest()
    {
        var source = _folder.Write("doc.pdf", Fixtures.Pdf(pages: 2));
        var workspace = NewWorkspace();
        var old = _folder.Write("doc_page_001.png", Fixtures.Png(30, 30), _folder.Out);
        var pages = new[] { Fixtures.Png(2, 2), Fixtures.Png(3, 3) };
        var staged = pages.Select(b => Stage(workspace, b, ".png")).ToArray();

        var result = await Commit(new OutputCommitter(), PagedJob(source, replaces: true), workspace, staged);

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal(pages[0], File.ReadAllBytes(old));
        Assert.Equal(pages[1], File.ReadAllBytes(Path.Combine(_folder.Out, "doc_page_002.png")));
        Assert.Equal(2, OutFiles().Length);
    }

    // ---- a bad result is discarded before anything is written ---------------------------------------------------------

    [Fact]
    public async Task AResultThatIsEmptyOfTheWrongFormatOrCutShortIsRefusedAndTheFolderIsUntouched()
    {
        var source = SourceFile();
        var workspace = NewWorkspace();
        var cases = new (string Name, byte[] Bytes)[]
        {
            ("empty", []),
            ("wrongformat", Fixtures.Png()),                       // a PNG where a JPEG was promised
            ("cutshort", Fixtures.Jpeg(endMarker: false)),
            ("junk", new byte[2000])
        };

        foreach (var (_, bytes) in cases)
        {
            var result = await Commit(new OutputCommitter(), SingleJob(source), workspace, Stage(workspace, bytes, ".jpg"));

            Assert.True(result.IsFailure);
            Assert.Equal(ErrorCode.Unexpected, result.Error!.Code);
            Assert.Contains("original", result.Error.SafeMessage, StringComparison.Ordinal);
        }

        Assert.Empty(OutFiles());
    }

    [Fact]
    public async Task AFileOutsideTheWorkingFolderIsNeverCommitted()
    {
        var source = SourceFile();
        var workspace = NewWorkspace();
        var elsewhere = _folder.Write("elsewhere.jpg", Fixtures.Jpeg());
        var sneaky = Path.Combine(workspace.Directory, "..", "elsewhere.jpg");

        Assert.True((await Commit(new OutputCommitter(), SingleJob(source), workspace, elsewhere)).IsFailure);
        Assert.True((await Commit(new OutputCommitter(), SingleJob(source), workspace, sneaky)).IsFailure);
        Assert.True((await Commit(new OutputCommitter(), SingleJob(source), workspace, Path.Combine(workspace.Directory, "missing.jpg"))).IsFailure);
        Assert.Empty(OutFiles());
        Assert.True(File.Exists(elsewhere)); // never moved or deleted
    }

    [Fact]
    public async Task TheNumberOfResultsMustMatchTheRoute()
    {
        var source = SourceFile();
        var workspace = NewWorkspace();
        var a = Stage(workspace, Fixtures.Jpeg(), ".jpg");
        var b = Stage(workspace, Fixtures.Jpeg(), ".jpg");

        Assert.True((await Commit(new OutputCommitter(), SingleJob(source), workspace, a, b)).IsFailure);
        Assert.True((await Commit(new OutputCommitter(), SingleJob(source), workspace)).IsFailure);
        Assert.True((await Commit(new OutputCommitter(), PagedJob(_folder.Write("d.pdf", Fixtures.Pdf())), workspace)).IsFailure);
        var tooMany = Enumerable.Range(0, 3).Select(_ => Stage(workspace, Fixtures.Png(), ".png")).ToArray();
        var small = new ConversionLimits { MaxPages = 2 };
        Assert.True((await new OutputCommitter().CommitAsync(PagedJob(_folder.Write("e.pdf", Fixtures.Pdf())), workspace, tooMany, small, Ct)).IsFailure);
        Assert.Empty(OutFiles());
    }

    [Fact]
    public async Task ADestinationThatVanishedIsRefusedWithAMessageAndNothingIsWritten()
    {
        var source = SourceFile();
        var workspace = NewWorkspace();
        var staged = Stage(workspace, Fixtures.Jpeg(), ".jpg");
        var gone = SingleJob(source) with { OutputDirectory = Path.Combine(_folder.Root, "gone") };

        var result = await Commit(new OutputCommitter(), gone, workspace, staged);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCode.IoFailure, result.Error!.Code);
        Assert.True(result.Error.Retryable);
        Assert.False(Directory.Exists(Path.Combine(_folder.Root, "gone")));
    }

    // ---- interruptions -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACancelBeforeStartDoesNothing()
    {
        var source = SourceFile();
        var workspace = NewWorkspace();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await new OutputCommitter().CommitAsync(SingleJob(source), workspace, [Stage(workspace, Fixtures.Jpeg(), ".jpg")], _limits, cts.Token);

        Assert.True(result.IsCancelled);
        Assert.Empty(OutFiles());
    }

    [Fact]
    public async Task ACancelWhileCopyingRemovesThePartFilesAndPublishesNothing()
    {
        var source = SourceFile();
        var workspace = NewWorkspace();
        var staged = Enumerable.Range(0, 3).Select(i => Stage(workspace, Fixtures.Png((uint)(i + 2), 2), ".png")).ToArray();
        using var cts = new CancellationTokenSource();
        var fs = new FaultingFileSystem { BeforeCopy = (n, _, _) => { if (n == 2) cts.Cancel(); } };

        var result = await new OutputCommitter(fs, null).CommitAsync(PagedJob(_folder.Write("d.pdf", Fixtures.Pdf())), workspace, staged, _limits, cts.Token);

        Assert.True(result.IsCancelled);
        Assert.Empty(OutFiles());
    }

    [Fact]
    public async Task ADiskThatFillsUpWhileCopyingLeavesNoPartialOutputAndTheOriginalAndTheStagedResultsIntact()
    {
        var source = SourceFile();
        var sourceBytes = File.ReadAllBytes(source);
        var sourceTime = File.GetLastWriteTimeUtc(source);
        var workspace = NewWorkspace();
        var staged = Enumerable.Range(0, 3).Select(i => Stage(workspace, Fixtures.Png((uint)(i + 2), 2), ".png")).ToArray();
        var existing = _folder.Write("doc_page_001.png", [4, 4, 4], _folder.Out);
        var fs = new FaultingFileSystem { BeforeCopy = (n, _, _) => { if (n == 3) throw FaultingFileSystem.DiskFull(); } };

        var result = await new OutputCommitter(fs, null).CommitAsync(PagedJob(source, replaces: true), workspace, staged, _limits, Ct);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCode.IoFailure, result.Error!.Code);
        Assert.True(result.Error.Retryable);
        Assert.Contains("not enough space", result.Error.SafeMessage, StringComparison.Ordinal);
        Assert.Contains("original", result.Error.SafeMessage, StringComparison.Ordinal);
        Assert.Equal(["doc_page_001.png"], OutFiles());           // only what was there before, no part files, no new pages
        Assert.Equal([4, 4, 4], File.ReadAllBytes(existing));       // not even the file Overwrite would have replaced
        Assert.Equal(sourceBytes, File.ReadAllBytes(source));
        Assert.Equal(sourceTime, File.GetLastWriteTimeUtc(source));
        Assert.All(staged, p => Assert.True(File.Exists(p)));       // the job can be tried again
    }

    [Fact]
    public async Task NotEnoughFreeSpaceIsDetectedBeforeAnythingIsCopied()
    {
        var source = SourceFile();
        var workspace = NewWorkspace();
        var fs = new FaultingFileSystem { UseRealFreeBytes = false, FreeBytes = 10 };

        var result = await new OutputCommitter(fs, null).CommitAsync(SingleJob(source), workspace, [Stage(workspace, Fixtures.Jpeg(), ".jpg")], _limits, Ct);

        Assert.True(result.IsFailure);
        Assert.Contains("not enough space", result.Error!.SafeMessage, StringComparison.Ordinal);
        Assert.Equal(0, fs.Copies);
        Assert.Empty(OutFiles());
    }

    [Fact]
    public async Task AFolderThatCannotBeWrittenIsAnAccessErrorWithNoLeftovers()
    {
        var source = SourceFile();
        var workspace = NewWorkspace();
        var fs = new FaultingFileSystem { BeforeCopy = (_, _, _) => throw new UnauthorizedAccessException() };

        var result = await new OutputCommitter(fs, null).CommitAsync(SingleJob(source), workspace, [Stage(workspace, Fixtures.Jpeg(), ".jpg")], _limits, Ct);

        Assert.Equal(ErrorCode.AccessDenied, result.Error!.Code);
        Assert.Contains("write", result.Error.SafeMessage, StringComparison.Ordinal);
        Assert.Empty(OutFiles());
    }

    [Fact]
    public async Task AFailureWhilePublishingPutsBackWhatWasPublishedAndLeavesNothing()
    {
        var source = _folder.Write("d.pdf", Fixtures.Pdf(pages: 3));
        var workspace = NewWorkspace();
        var staged = Enumerable.Range(0, 3).Select(i => Stage(workspace, Fixtures.Png((uint)(i + 2), 2), ".png")).ToArray();
        var fs = new FaultingFileSystem { BeforeMove = (n, _, _) => { if (n == 3) throw new IOException("the volume went away"); } };

        var result = await new OutputCommitter(fs, null).CommitAsync(PagedJob(source), workspace, staged, _limits, Ct);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCode.IoFailure, result.Error!.Code);
        Assert.Empty(OutFiles()); // pages 1 and 2 were renamed back and removed with the part files
    }

    [Fact]
    public async Task ANameTakenByAnotherProgramAtTheLastMomentIsNotLostAndTheResultTakesTheNextName()
    {
        var source = SourceFile();
        var workspace = NewWorkspace();
        var theirs = Path.Combine(_folder.Out, "photo.jpg");
        var fs = new FaultingFileSystem
        {
            BeforeMove = (n, _, to) =>
            {
                if (n == 1)
                {
                    File.WriteAllBytes(theirs, [8, 8, 8]); // appears between the check and the rename
                }
            }
        };

        var result = await new OutputCommitter(fs, null).CommitAsync(SingleJob(source), workspace, [Stage(workspace, Fixtures.Jpeg(), ".jpg")], _limits, Ct);

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal("photo (2).jpg", Path.GetFileName(result.Value[0]));
        Assert.Equal([8, 8, 8], File.ReadAllBytes(theirs));
        Assert.Equal(["photo (2).jpg", "photo.jpg"], OutFiles());
    }

    [Fact]
    public async Task ARaceOnALaterPageRollsTheEarlierPagesBackAndMovesTheWholeSetToTheNextName()
    {
        var source = _folder.Write("d.pdf", Fixtures.Pdf(pages: 3));
        var workspace = NewWorkspace();
        var staged = Enumerable.Range(0, 3).Select(i => Stage(workspace, Fixtures.Png((uint)(i + 2), 2), ".png")).ToArray();
        var theirs = Path.Combine(_folder.Out, "doc_page_002.png");
        var fs = new FaultingFileSystem
        {
            BeforeMove = (n, _, _) =>
            {
                if (n == 2)
                    File.WriteAllBytes(theirs, [7, 7]);
            }
        };

        var result = await new OutputCommitter(fs, null).CommitAsync(PagedJob(source), workspace, staged, _limits, Ct);

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal(["doc (2)_page_001.png", "doc (2)_page_002.png", "doc (2)_page_003.png"], result.Value.Select(Path.GetFileName));
        Assert.Equal([7, 7], File.ReadAllBytes(theirs));
        Assert.DoesNotContain("doc_page_001.png", OutFiles()); // the first page published under the first name was taken back
        Assert.Equal(4, OutFiles().Length);
    }

    [Fact]
    public async Task EverythingIsCopiedBeforeAnythingIsPublishedSoAPublishedFileIsAlwaysComplete()
    {
        var source = _folder.Write("d.pdf", Fixtures.Pdf(pages: 3));
        var workspace = NewWorkspace();
        var pages = Enumerable.Range(0, 3).Select(i => Fixtures.Png((uint)(i + 2), 2)).ToArray();
        var staged = pages.Select(b => Stage(workspace, b, ".png")).ToArray();
        var checkedAtFirstRename = false;
        var fs = new FaultingFileSystem
        {
            BeforeMove = (n, from, _) =>
            {
                if (n != 1)
                    return;
                var parts = Directory.GetFiles(_folder.Out, "*.lunar-part");
                checkedAtFirstRename = parts.Length == 3
                    && parts.Select(p => new FileInfo(p).Length).Order().SequenceEqual(pages.Select(p => (long)p.Length).Order());
            }
        };

        var result = await new OutputCommitter(fs, null).CommitAsync(PagedJob(source), workspace, staged, _limits, Ct);

        Assert.True(result.IsSuccess);
        Assert.True(checkedAtFirstRename, "At the first rename all three part files had to be complete.");
    }

    [Fact]
    public async Task PartFilesThatCannotBeDeletedNowAreRemovedWhenTheWorkspaceIsDisposed()
    {
        var source = SourceFile();
        var workspace = NewWorkspace();
        var log = new RecordingLog();
        var fs = new FaultingFileSystem
        {
            BeforeCopy = (_, _, _) => { },
            BeforeMove = (_, _, _) => throw new IOException("boom"),
            DeleteFails = _ => true // a scanner holds the part file
        };

        var result = await new OutputCommitter(fs, log).CommitAsync(SingleJob(source), workspace, [Stage(workspace, Fixtures.Jpeg(), ".jpg")], _limits, Ct);

        Assert.True(result.IsFailure);
        Assert.Single(Directory.GetFiles(_folder.Out, "*.lunar-part")); // still there: the delete was blocked
        lock (log.Events)
            Assert.Contains(log.Events, e => e.EventId == "conversion.part.cleanup_failed");

        workspace.Dispose();

        Assert.Empty(Directory.GetFiles(_folder.Out, "*.lunar-part")); // the journal found it
    }

    // ---- the original and the messages -------------------------------------------------------------------------------

    [Fact]
    public async Task NoScenarioEverChangesTheInputFile()
    {
        var source = SourceFile();
        var bytesBefore = File.ReadAllBytes(source);
        var timeBefore = File.GetLastWriteTimeUtc(source);
        var attributesBefore = File.GetAttributes(source);
        var workspace = NewWorkspace();

        await Commit(new OutputCommitter(), SingleJob(source), workspace, Stage(workspace, Fixtures.Jpeg(), ".jpg"));
        await Commit(new OutputCommitter(), SingleJob(source), workspace, Stage(workspace, [], ".jpg"));
        await new OutputCommitter(new FaultingFileSystem { BeforeCopy = (_, _, _) => throw FaultingFileSystem.DiskFull() }, null)
            .CommitAsync(SingleJob(source), workspace, [Stage(workspace, Fixtures.Jpeg(), ".jpg")], _limits, Ct);

        Assert.Equal(bytesBefore, File.ReadAllBytes(source));
        Assert.Equal(timeBefore, File.GetLastWriteTimeUtc(source));
        Assert.Equal(attributesBefore, File.GetAttributes(source));
    }

    [Fact]
    public async Task NoFailureMessageContainsAPathOrAFileName()
    {
        var source = SourceFile("secret-plans.png");
        var workspace = NewWorkspace();
        var failures = new List<OperationError>();

        failures.Add((await Commit(new OutputCommitter(), SingleJob(source, "secret-plans"), workspace, Stage(workspace, [], ".jpg"))).Error!);
        failures.Add((await new OutputCommitter(new FaultingFileSystem { BeforeCopy = (_, _, _) => throw FaultingFileSystem.DiskFull() }, null)
            .CommitAsync(SingleJob(source, "secret-plans"), workspace, [Stage(workspace, Fixtures.Jpeg(), ".jpg")], _limits, Ct)).Error!);
        failures.Add((await new OutputCommitter(new FaultingFileSystem { BeforeCopy = (_, _, _) => throw new UnauthorizedAccessException(@"Access to the path 'C:\secret-plans' is denied.") }, null)
            .CommitAsync(SingleJob(source, "secret-plans"), workspace, [Stage(workspace, Fixtures.Jpeg(), ".jpg")], _limits, Ct)).Error!);
        failures.Add((await new OutputCommitter(new FaultingFileSystem { BeforeCopy = (_, _, _) => throw new IOException(@"The process cannot access C:\secret-plans.jpg") }, null)
            .CommitAsync(SingleJob(source, "secret-plans"), workspace, [Stage(workspace, Fixtures.Jpeg(), ".jpg")], _limits, Ct)).Error!);

        Assert.Equal(4, failures.Count);
        Assert.All(failures, e =>
        {
            Assert.DoesNotContain("secret", e.SafeMessage, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@":\", e.SafeMessage, StringComparison.Ordinal);
        });
    }

    // ---- many at once ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TwentyJobsPublishingTheSameNameAtTheSameTimeEachGetTheirOwnCompleteFile()
    {
        var source = SourceFile();
        var committer = new OutputCommitter();
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(async i =>
        {
            var workspace = NewWorkspace();
            var bytes = Fixtures.Jpeg(width: (ushort)(i + 1), height: 2);
            var staged = Stage(workspace, bytes, ".jpg");
            return (Bytes: bytes, Result: await committer.CommitAsync(SingleJob(source), workspace, [staged], _limits, Ct));
        }));

        Assert.All(results, r => Assert.True(r.Result.IsSuccess, r.Result.Error?.SafeMessage));
        var finals = results.Select(r => r.Result.Value[0]).ToList();
        Assert.Equal(20, finals.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var (bytes, result) in results)
            Assert.Equal(bytes, File.ReadAllBytes(result.Value[0])); // each file is exactly its own result
        Assert.Equal(20, OutFiles().Length);
        Assert.Empty(Directory.GetFiles(_folder.Out, "*.lunar-part"));
    }

    [Fact]
    public async Task TheCommitterNeedsItsArguments()
    {
        var committer = new OutputCommitter();
        var workspace = NewWorkspace();
        var source = SourceFile();

        await Assert.ThrowsAsync<ArgumentNullException>(() => committer.CommitAsync(null!, workspace, [], _limits, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => committer.CommitAsync(SingleJob(source), null!, [], _limits, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => committer.CommitAsync(SingleJob(source), workspace, null!, _limits, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => committer.CommitAsync(SingleJob(source), workspace, [], null!, Ct));
    }
}
