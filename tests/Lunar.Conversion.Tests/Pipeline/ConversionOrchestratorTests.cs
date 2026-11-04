using Lunar.Application.Abstractions.Conversion;
using Lunar.Application.Conversion.Models;
using Lunar.Application.Conversion.UseCases;
using Lunar.Conversion.Output;
using Lunar.Conversion.Pipeline;
using Lunar.Conversion.Planning;
using Lunar.Conversion.Providers;
using Lunar.Conversion.Tests.Support;
using Lunar.Domain.Conversion;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Conversion.Tests.Pipeline;

// BE-27: the planner, the providers, the output step and the queue working as one, on the real disk. Only the PDF engine is a fake
// (the real one is covered by the Windows and composition tests). Every test also checks what is NOT left behind.
public sealed class ConversionOrchestratorTests : IAsyncDisposable
{
    private sealed class Renderer(bool available) : IRendererAvailability
    {
        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(available);
    }

    private readonly TempFolder _folder = new();
    private readonly FakePdfRasterizer _pdf = new();
    private readonly List<ConversionJobQueue> _queues = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Work => Path.Combine(_folder.Root, "work");

    public ConversionOrchestratorTests() => Directory.CreateDirectory(Work); // so that "as it was before" includes the empty working root

    public async ValueTask DisposeAsync()
    {
        foreach (var queue in _queues)
            await queue.DisposeAsync();
        _folder.Dispose();
    }

    private ConversionOrchestrator Orchestrator(IOutputFileSystem? fileSystem = null) => new(
        Work,
        new ImageConversionProvider(),
        new PdfCreationProvider(),
        new DocxEmbeddingProvider(),
        new PdfRasterizationProvider(_pdf),
        fileSystem is null ? new OutputCommitter() : new OutputCommitter(fileSystem, null));

    private (StartConversion Start, ConversionJobQueue Queue, ConversionOrchestrator Orchestrator) Build(
        bool rendererAvailable = false, IOutputFileSystem? fileSystem = null, ConversionQueueOptions? options = null)
    {
        var orchestrator = Orchestrator(fileSystem);
        var queue = new ConversionJobQueue(orchestrator, options);
        _queues.Add(queue);
        return (new StartConversion(new ConversionPlanner(new Renderer(rendererAvailable)), queue), queue, orchestrator);
    }

    private string Picture(string name, FileFormat format) => format == FileFormat.Png
        ? _folder.Write(name, PictureFixtures.Rgb(8, 6, (_, _) => PictureFixtures.Red))
        : _folder.Write(name, PictureFixtures.SkiaJpeg(8, 6, (_, _) => PictureFixtures.Green));

    private ConversionRequest Request(FileFormat target, CollisionPolicy collision = CollisionPolicy.Rename, params string[] sources) =>
        new(sources, target, _folder.Out, collision);

    private string[] OutNames() => [.. Directory.GetFiles(_folder.Out).Select(f => Path.GetFileName(f)!).OrderBy(n => n, StringComparer.Ordinal)];

    private async Task<IReadOnlyList<ConversionJobSnapshot>> RunAsync(
        (StartConversion Start, ConversionJobQueue Queue, ConversionOrchestrator Orchestrator) system, ConversionRequest request, ConversionChoices? choices = null)
    {
        var started = await system.Start.ExecuteAsync(request, choices, Ct);
        Assert.True(started.IsSuccess, started.Error?.SafeMessage);
        await system.Queue.WhenIdleAsync(Ct);
        return system.Queue.Jobs;
    }

    // ---- every supported route, end to end ---------------------------------------------------------------------------

    [Fact]
    public async Task PngBecomesAJpegAndNothingIsLeftBehind()
    {
        var system = Build();
        var source = Picture("photo.png", FileFormat.Png);
        var original = File.ReadAllBytes(source);

        var jobs = await RunAsync(system, Request(FileFormat.Jpeg, CollisionPolicy.Rename, source));

        var job = Assert.Single(jobs);
        Assert.Equal(ConversionJobState.Succeeded, job.State);
        Assert.Equal(["photo.jpeg"], OutNames());
        var red = PictureFixtures.PixelAt(Path.Combine(_folder.Out, "photo.jpeg"), 2, 2);
        Assert.True(red.Red > 200 && red.Green < 60, "the picture is the red one");
        Assert.Empty(Directory.GetFileSystemEntries(Work));
        Assert.Equal(original, File.ReadAllBytes(source));
    }

    [Fact]
    public async Task JpegBecomesAPng()
    {
        var system = Build();

        var jobs = await RunAsync(system, Request(FileFormat.Png, CollisionPolicy.Rename, Picture("shot.jpg", FileFormat.Jpg)));

        Assert.Equal(ConversionJobState.Succeeded, Assert.Single(jobs).State);
        Assert.Equal(["shot.png"], OutNames());
        Assert.Equal(0x89, File.ReadAllBytes(Path.Combine(_folder.Out, "shot.png"))[0]);
    }

    [Fact]
    public async Task APictureBecomesAPdfAndAWordFile()
    {
        var system = Build();
        var pdfJob = await RunAsync(system, Request(FileFormat.Pdf, CollisionPolicy.Rename, Picture("a.png", FileFormat.Png)));
        var docxJob = await RunAsync(system, Request(FileFormat.Docx, CollisionPolicy.Rename, Picture("b.png", FileFormat.Png)));

        Assert.All(new[] { pdfJob[0], docxJob[^1] }, j => Assert.Equal(ConversionJobState.Succeeded, j.State));
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(_folder.Out, "a.pdf")), 0, 4));
        Assert.Equal("PK", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(_folder.Out, "b.docx")), 0, 2));
    }

    [Fact]
    public async Task ACopyUnderANewNameKeepsTheBytes()
    {
        var system = Build();
        var source = Picture("same.png", FileFormat.Png);

        var request = new ConversionRequest([source], FileFormat.Png, Directory.CreateDirectory(Path.Combine(_folder.Root, "copies")).FullName);
        var jobs = await RunAsync(system, request);

        Assert.Equal(ConversionJobState.Succeeded, Assert.Single(jobs).State);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(_folder.Root, "copies", "same.png")));
    }

    [Fact]
    public async Task ChosenPagesOfAPdfBecomeNumberedPicturesWithProgress()
    {
        _pdf.Pages = 5;
        var system = Build();
        var seen = new List<JobProgress>();
        using var watcher = system.Queue.Changes.Subscribe(new Observer(s => { lock (seen) seen.Add(s.Progress); }));
        var source = _folder.Write("book.pdf", Fixtures.Pdf(5));
        var choices = new ConversionChoices { Pdf = new PdfRasterizationOptions { Pages = Select("2-3") } };

        var jobs = await RunAsync(system, Request(FileFormat.Png, CollisionPolicy.Rename, source), choices);

        Assert.Equal(ConversionJobState.Succeeded, Assert.Single(jobs).State);
        Assert.Equal(["book_page_002.png", "book_page_003.png"], OutNames());
        lock (seen)
            Assert.Contains(new JobProgress(2, 2), seen);
    }

    [Fact]
    public async Task APasswordOpensAProtectedPdfAndIsNeverShown()
    {
        _pdf.RequiredPassword = "hunter2-secret";
        var system = Build();
        var source = _folder.Write("locked.pdf", Fixtures.Pdf(2));

        var refused = await RunAsync(system, Request(FileFormat.Png, CollisionPolicy.Rename, source));
        var failed = Assert.Single(refused);
        Assert.Equal(ConversionJobState.Failed, failed.State);
        Assert.Equal(ErrorCode.AccessDenied, failed.Error!.Code);
        Assert.Empty(OutNames());

        system.Queue.ClearFinished();
        var opened = await RunAsync(system, Request(FileFormat.Png, CollisionPolicy.Rename, source), new ConversionChoices { PdfPassword = "hunter2-secret" });

        var job = Assert.Single(opened);
        Assert.Equal(ConversionJobState.Succeeded, job.State);
        Assert.Equal("hunter2-secret", _pdf.LastPassword);
        Assert.DoesNotContain("hunter2-secret", job.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2-secret", job.Job.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeveralFilesRunTogetherAndEachGetsItsOwnName()
    {
        var system = Build(options: new ConversionQueueOptions { MaxConcurrency = 3 });
        var sources = Enumerable.Range(0, 8).Select(i => Picture($"p{i}.png", FileFormat.Png)).ToArray();

        var jobs = await RunAsync(system, Request(FileFormat.Jpg, CollisionPolicy.Rename, sources));

        Assert.Equal(8, jobs.Count);
        Assert.All(jobs, j => Assert.Equal(ConversionJobState.Succeeded, j.State));
        Assert.Equal(8, OutNames().Length);
    }

    [Fact]
    public async Task ATakenNameIsNeverOverwrittenByDefault()
    {
        var system = Build();
        File.WriteAllBytes(Path.Combine(_folder.Out, "a.jpg"), [1, 2, 3]);

        var jobs = await RunAsync(system, Request(FileFormat.Jpg, CollisionPolicy.Rename, Picture("a.png", FileFormat.Png)));

        Assert.Equal(ConversionJobState.Succeeded, Assert.Single(jobs).State);
        Assert.Equal(["a (2).jpg", "a.jpg"], OutNames());
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(_folder.Out, "a.jpg")));
    }

    // ---- routes that are not available -------------------------------------------------------------------------------

    [Fact]
    public async Task AWordFileIsRefusedWhileNoLayoutEngineIsInstalled()
    {
        var system = Build(rendererAvailable: false);
        var source = _folder.Write("letter.docx", Fixtures.Docx());

        var started = await system.Start.ExecuteAsync(Request(FileFormat.Pdf, CollisionPolicy.Rename, source), null, Ct);

        Assert.True(started.IsSuccess);
        Assert.Empty(started.Value.JobIds);
        Assert.Equal(RejectionReason.RendererMissing, Assert.Single(started.Value.Plan.Rejected).Reason);
        Assert.Empty(system.Queue.Jobs);
        Assert.Empty(OutNames());
    }

    [Fact]
    public async Task AWordJobThatIsStillQueuedFailsHonestlyAndLeavesNothing()
    {
        // Even if a layout engine were reported, this version has none wired: the job must fail cleanly, not pretend.
        var system = Build(rendererAvailable: true);
        var source = _folder.Write("letter.docx", Fixtures.Docx());

        var jobs = await RunAsync(system, Request(FileFormat.Pdf, CollisionPolicy.Rename, source));

        var job = Assert.Single(jobs);
        Assert.Equal(ConversionJobState.Failed, job.State);
        Assert.Equal(ErrorCode.DependencyMissing, job.Error!.Code);
        Assert.Empty(OutNames());
        Assert.Empty(Directory.GetFileSystemEntries(Work));
    }

    [Fact]
    public async Task PdfToWordIsNotAPlannedRoute()
    {
        var system = Build();
        var source = _folder.Write("a.pdf", Fixtures.Pdf(1));

        var started = await system.Start.ExecuteAsync(Request(FileFormat.Docx, CollisionPolicy.Rename, source), null, Ct);

        Assert.Equal(RejectionReason.RouteUnsupported, Assert.Single(started.Value.Plan.Rejected).Reason);
        Assert.Empty(started.Value.JobIds);
    }

    // ---- failure and recovery ----------------------------------------------------------------------------------------

    [Fact]
    public async Task AFailureHalfWayLeavesNothingAndATryAgainSucceeds()
    {
        _pdf.Pages = 5;
        var failing = true;
        _pdf.RenderOverride = (index, w, h, dest, ct) => failing && index == 3
            ? Task.FromResult(Result.Failure<RenderedPage>(new OperationError(ErrorCode.Unexpected, "The page could not be drawn.", retryable: true)))
            : null;
        var system = Build();
        var source = _folder.Write("big.pdf", Fixtures.Pdf(5));
        var before = _folder.Snapshot();

        var first = await RunAsync(system, Request(FileFormat.Png, CollisionPolicy.Rename, source));

        var failed = Assert.Single(first);
        Assert.Equal(ConversionJobState.Failed, failed.State);
        Assert.Empty(OutNames());
        Assert.Empty(Directory.GetFileSystemEntries(Work));
        Assert.Equal(before, _folder.Snapshot()); // the input and the folders are exactly as they were

        failing = false;
        var again = system.Queue.Retry(failed.JobId);
        Assert.True(again.IsSuccess);
        await system.Queue.WhenIdleAsync(Ct);

        Assert.Equal(ConversionJobState.Succeeded, system.Queue.Jobs.Single(j => j.JobId == again.Value).State);
        Assert.Equal(5, OutNames().Length);
    }

    [Fact]
    public async Task AFullDiskWhilePublishingLeavesNothing()
    {
        var disk = new FaultingFileSystem { BeforeCopy = (_, _, _) => throw FaultingFileSystem.DiskFull() };
        var system = Build(fileSystem: disk);

        var jobs = await RunAsync(system, Request(FileFormat.Jpg, CollisionPolicy.Rename, Picture("a.png", FileFormat.Png)));

        var job = Assert.Single(jobs);
        Assert.Equal(ConversionJobState.Failed, job.State);
        Assert.Equal(ErrorCode.IoFailure, job.Error!.Code);
        Assert.True(job.Error.Retryable);
        Assert.Empty(OutNames());
        Assert.Empty(Directory.GetFileSystemEntries(Work));
    }

    [Fact]
    public async Task AFileChangedAfterPlanningIsNotTrusted()
    {
        var orchestrator = Orchestrator();
        var planner = new ConversionPlanner(new Renderer(false));
        var source = Picture("late.png", FileFormat.Png);
        var plan = (await planner.PlanAsync(Request(FileFormat.Jpg, CollisionPolicy.Rename, source), Ct)).Value;
        File.WriteAllBytes(source, PictureFixtures.Rgb(20, 20, (_, _) => PictureFixtures.Blue)); // swapped after planning

        var result = await orchestrator.ExecuteAsync(plan.Jobs[0], new Progress<JobProgress>(), Ct);

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.Contains("changed", result.Error.SafeMessage, StringComparison.Ordinal);
        Assert.Empty(OutNames());
    }

    [Fact]
    public async Task AFileDeletedAfterPlanningFailsCleanly()
    {
        var orchestrator = Orchestrator();
        var planner = new ConversionPlanner(new Renderer(false));
        var source = Picture("gone.png", FileFormat.Png);
        var plan = (await planner.PlanAsync(Request(FileFormat.Jpg, CollisionPolicy.Rename, source), Ct)).Value;
        File.Delete(source);

        var result = await orchestrator.ExecuteAsync(plan.Jobs[0], new Progress<JobProgress>(), Ct);

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.DoesNotContain("gone", result.Error.SafeMessage, StringComparison.Ordinal);
        Assert.Empty(OutNames());
    }

    [Fact]
    public async Task FoldersLeftByAnEarlierRunAreRemovedBeforeTheFirstJob()
    {
        var leftover = Directory.CreateDirectory(Path.Combine(Work, "job-" + Guid.NewGuid().ToString("N"))).FullName;
        File.WriteAllText(Path.Combine(leftover, "x.png"), "left by a killed run");
        var system = Build();

        var jobs = await RunAsync(system, Request(FileFormat.Jpg, CollisionPolicy.Rename, Picture("a.png", FileFormat.Png)));

        Assert.Equal(ConversionJobState.Succeeded, Assert.Single(jobs).State);
        Assert.False(Directory.Exists(leftover));
        Assert.Equal(1, await system.Orchestrator.RecoverAbandonedAsync()); // the first call's answer, kept
    }

    // ---- cancelling: no partial file, anywhere ------------------------------------------------------------------------

    [Fact]
    public async Task CancellingInTheMiddleOfAPdfLeavesNoFileAnywhere()
    {
        _pdf.Pages = 6;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pdf.RenderOverride = (index, w, h, dest, ct) => index == 3 ? HoldAsync(reached, ct) : null;
        var system = Build();
        var source = _folder.Write("long.pdf", Fixtures.Pdf(6));
        var before = _folder.Snapshot();

        var started = await system.Start.ExecuteAsync(Request(FileFormat.Png, CollisionPolicy.Rename, source), null, Ct);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct); // three pages are drawn into the working folder by now
        Assert.NotEmpty(Directory.GetFiles(Directory.GetDirectories(Work)[0], "*.png"));

        Assert.True(system.Queue.Cancel(started.Value.JobIds[0]).IsSuccess);
        await system.Queue.WhenIdleAsync(Ct);

        var job = Assert.Single(system.Queue.Jobs);
        Assert.Equal(ConversionJobState.Cancelled, job.State);
        Assert.Empty(job.OutputPaths);
        Assert.Empty(OutNames());
        Assert.Empty(Directory.GetFileSystemEntries(Work));
        Assert.Equal(before, _folder.Snapshot());
    }

    private static async Task<Result<RenderedPage>> HoldAsync(TaskCompletionSource reached, CancellationToken ct)
    {
        reached.TrySetResult();
        await Task.Delay(Timeout.Infinite, ct);
        return Result.Success(new RenderedPage(1, 1));
    }

    [Fact]
    public async Task CancellingWhileThePublishIsAboutToStartLeavesNoPartFile()
    {
        using var cancel = new CancellationTokenSource();
        var disk = new FaultingFileSystem { BeforeCopy = (_, _, _) => cancel.Cancel() };
        var orchestrator = Orchestrator(disk);
        var plan = (await new ConversionPlanner(new Renderer(false)).PlanAsync(Request(FileFormat.Jpg, CollisionPolicy.Rename, Picture("a.png", FileFormat.Png)), Ct)).Value;

        var result = await orchestrator.ExecuteAsync(plan.Jobs[0], new Progress<JobProgress>(), cancel.Token);

        Assert.True(result.IsCancelled);
        Assert.Empty(OutNames());
        Assert.Empty(Directory.GetFileSystemEntries(Work));
    }

    [Fact]
    public async Task DisposingTheQueueWhileAJobRunsCancelsItAndLeavesNothing()
    {
        _pdf.Pages = 4;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pdf.RenderOverride = (index, w, h, dest, ct) => index == 1 ? HoldAsync(reached, ct) : null;
        var system = Build();
        var source = _folder.Write("exit.pdf", Fixtures.Pdf(4));

        await system.Start.ExecuteAsync(Request(FileFormat.Png, CollisionPolicy.Rename, source), null, Ct);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await system.Queue.DisposeAsync();

        Assert.Empty(OutNames());
        Assert.Empty(Directory.GetFileSystemEntries(Work));
    }

    // ---- the use case ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task InvalidChoicesAreRefusedBeforeAnythingIsPlanned()
    {
        var system = Build();
        var choices = new ConversionChoices { Image = new ImageEncodingOptions { JpegQuality = 0 } };

        var started = await system.Start.ExecuteAsync(Request(FileFormat.Jpg, CollisionPolicy.Rename, Picture("a.png", FileFormat.Png)), choices, Ct);

        Assert.Equal(ErrorCode.InvalidInput, started.Error!.Code);
        Assert.Empty(system.Queue.Jobs);
    }

    [Fact]
    public async Task AFullQueueTakesNoneOfTheBatch()
    {
        var system = Build(options: new ConversionQueueOptions { MaxQueued = 1 });
        var sources = new[] { Picture("a.png", FileFormat.Png), Picture("b.png", FileFormat.Png) };

        var started = await system.Start.ExecuteAsync(Request(FileFormat.Jpg, CollisionPolicy.Rename, sources), null, Ct);

        Assert.True(started.IsFailure);
        Assert.True(started.Error!.Retryable);
        Assert.Empty(system.Queue.Jobs);
    }

    [Fact]
    public async Task AGoodFileIsConvertedEvenWhenAnotherIsRefused()
    {
        var system = Build();
        var good = Picture("good.png", FileFormat.Png);
        var bad = _folder.Write("bad.png", [1, 2, 3, 4, 5, 6, 7, 8]);

        var jobs = await RunAsync(system, Request(FileFormat.Jpg, CollisionPolicy.Rename, good, bad));

        Assert.Equal(ConversionJobState.Succeeded, Assert.Single(jobs).State);
        Assert.Equal(["good.jpg"], OutNames());
    }

    [Fact]
    public async Task ACancelledTokenQueuesNothing()
    {
        var system = Build();
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        var started = await system.Start.ExecuteAsync(Request(FileFormat.Jpg, CollisionPolicy.Rename, Picture("a.png", FileFormat.Png)), null, cancel.Token);

        Assert.True(started.IsCancelled);
        Assert.Empty(system.Queue.Jobs);
    }

    [Fact]
    public void ChoicesNeverPrintThePassword()
    {
        var text = new ConversionChoices { PdfPassword = "top-secret" }.ToString();

        Assert.DoesNotContain("top-secret", text, StringComparison.Ordinal);
        Assert.Contains("PdfPassword = set", text, StringComparison.Ordinal);
    }

    private static PageSelection Select(string text)
    {
        Assert.True(PageSelection.TryParse(text, out var selection, out _));
        return selection;
    }

    private sealed class Observer(Action<ConversionJobSnapshot> onNext) : IObserver<ConversionJobSnapshot>
    {
        public void OnNext(ConversionJobSnapshot value) => onNext(value);

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }
}
