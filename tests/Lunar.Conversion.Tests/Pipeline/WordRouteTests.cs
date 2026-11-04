using System.Diagnostics;
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

// BE-25 through BE-27: Word documents converted by the whole chain (planner, queue, orchestrator, engine provider, rasterizer, output step),
// with a stand-in for LibreOffice and the fake PDF engine. The real LibreOffice and a Word-versus-LibreOffice comparison are NOT_RUN.
[Collection("FakeOffice")]
public sealed class WordRouteTests : IAsyncDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeOffice _office = new(Fixtures.Pdf(3));
    private readonly FakePdfRasterizer _pdf = new() { Pages = 3 };
    private readonly DateTime _started = DateTime.Now.AddSeconds(-1);
    private readonly List<ConversionJobQueue> _queues = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var queue in _queues)
            await queue.DisposeAsync();
        _office.Dispose();
        _folder.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Work => Path.Combine(_folder.Root, "work");

    private (StartConversion Start, ConversionJobQueue Queue) Build(bool routesEnabled = true)
    {
        Directory.CreateDirectory(Work);
        var orchestrator = new ConversionOrchestrator(
            Work, new ImageConversionProvider(), new PdfCreationProvider(), new DocxEmbeddingProvider(), new PdfRasterizationProvider(_pdf),
            new LibreOfficeRendererProvider(_office, new LibreOfficeOptions { Timeout = TimeSpan.FromSeconds(60) }), new OutputCommitter());
        var queue = new ConversionJobQueue(orchestrator);
        _queues.Add(queue);
        return (new StartConversion(new ConversionPlanner(new LibreOfficeAvailability(_office, routesEnabled)), queue), queue);
    }

    private ConversionRequest Request(FileFormat target, string source) => new([source], target, _folder.Out);

    private string[] OutNames() => [.. Directory.GetFiles(_folder.Out).Select(f => Path.GetFileName(f)!).OrderBy(n => n, StringComparer.Ordinal)];

    [Fact]
    public async Task AWordFileBecomesAPdfThroughTheWholeChain()
    {
        var (start, queue) = Build();
        var source = _folder.Write("letter.docx", Fixtures.Docx());
        var original = File.ReadAllBytes(source);

        var started = await start.ExecuteAsync(Request(FileFormat.Pdf, source), null, Ct);
        await queue.WhenIdleAsync(Ct);

        Assert.True(started.IsSuccess, started.Error?.SafeMessage);
        var job = Assert.Single(queue.Jobs);
        Assert.Equal(ConversionJobState.Succeeded, job.State);
        Assert.Equal(["letter.pdf"], OutNames());
        Assert.Equal(Fixtures.Pdf(3), File.ReadAllBytes(Path.Combine(_folder.Out, "letter.pdf")));
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.Empty(Directory.GetFileSystemEntries(Work));
        Assert.Equal([Path.GetFileName(source)], Directory.GetFiles(_folder.In).Select(f => Path.GetFileName(f)));
    }

    [Fact]
    public async Task AWordFileBecomesNumberedPicturesWithRealPageProgress()
    {
        var (start, queue) = Build();
        var seen = new List<JobProgress>();
        using var watcher = queue.Changes.Subscribe(new Watcher(s => { lock (seen) seen.Add(s.Progress); }));
        var source = _folder.Write("report.docx", Fixtures.Docx());

        await start.ExecuteAsync(Request(FileFormat.Png, source), null, Ct);
        await queue.WhenIdleAsync(Ct);

        Assert.Equal(ConversionJobState.Succeeded, Assert.Single(queue.Jobs).State);
        Assert.Equal(["report_page_001.png", "report_page_002.png", "report_page_003.png"], OutNames());
        lock (seen)
        {
            Assert.Contains(new JobProgress(3, 3), seen);
            Assert.DoesNotContain(seen, p => p.PagesTotal is not null and not 3); // a page count only where the PDF was really counted
        }

        Assert.Empty(Directory.GetFileSystemEntries(Work));
    }

    [Fact]
    public async Task WhileTheHoldIsOnNoWordRouteIsPlannedEvenWithTheEngineInstalled()
    {
        var (start, queue) = Build(routesEnabled: false);
        var source = _folder.Write("letter.docx", Fixtures.Docx());

        var started = await start.ExecuteAsync(Request(FileFormat.Pdf, source), null, Ct);

        Assert.True(started.IsSuccess);
        Assert.Empty(started.Value.JobIds);
        var refused = Assert.Single(started.Value.Plan.Rejected);
        Assert.Equal(RejectionReason.RendererMissing, refused.Reason);
        Assert.Contains("LibreOffice", refused.Error.SafeMessage, StringComparison.Ordinal);
        Assert.Contains("doesn't switch it on yet", refused.Error.SafeMessage, StringComparison.Ordinal);
        Assert.False(_office.Started);
        Assert.Empty(queue.Jobs);
    }

    [Fact]
    public async Task AMissingEngineIsExplainedWhenTheRoutesAreOnButNothingIsInstalled()
    {
        _office.Installed = false;
        var (start, _) = Build();
        var source = _folder.Write("letter.docx", Fixtures.Docx());

        var started = await start.ExecuteAsync(Request(FileFormat.Pdf, source), null, Ct);

        Assert.Equal(RejectionReason.RendererMissing, Assert.Single(started.Value.Plan.Rejected).Reason);
        Assert.False(_office.Started);
    }

    [Fact]
    public async Task AnEngineThatFailsLeavesNothingAndTheJobFailsAlone()
    {
        _office.Mode = "fail";
        var (start, queue) = Build();
        var source = _folder.Write("letter.docx", Fixtures.Docx());

        await start.ExecuteAsync(Request(FileFormat.Pdf, source), null, Ct);
        await queue.WhenIdleAsync(Ct);

        var job = Assert.Single(queue.Jobs);
        Assert.Equal(ConversionJobState.Failed, job.State);
        Assert.Equal("LibreOffice could not convert this document.", job.Error!.SafeMessage);
        Assert.Empty(OutNames());
        Assert.Empty(Directory.GetFileSystemEntries(Work));
    }

    [Fact]
    public async Task CancellingAJobWhileTheEngineRunsStopsTheEngineAndLeavesNothing()
    {
        _office.Mode = "hang";
        var (start, queue) = Build();
        var source = _folder.Write("letter.docx", Fixtures.Docx());
        var before = _folder.Snapshot();

        var started = await start.ExecuteAsync(Request(FileFormat.Pdf, source), null, Ct);
        var end = DateTime.UtcNow.AddSeconds(30);
        while (!_office.Started && DateTime.UtcNow < end)
            await Task.Delay(20, Ct);
        Assert.True(_office.Started);
        await Task.Delay(500, Ct);

        var clock = Stopwatch.StartNew();
        Assert.True(queue.Cancel(started.Value.JobIds[0]).IsSuccess);
        await queue.WhenIdleAsync(Ct);

        Assert.Equal(ConversionJobState.Cancelled, Assert.Single(queue.Jobs).State);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"took {clock.Elapsed}");
        Assert.Empty(OutNames());
        Assert.Empty(Directory.GetFileSystemEntries(Work));
        Assert.Equal(0, Process.GetProcessesByName("PING").Count(p => { try { return !p.HasExited && p.StartTime >= _started; } catch (InvalidOperationException) { return false; } }));
        Assert.Equal(before.Replace(Environment.NewLine, "\n"), _folder.Snapshot().Replace(Environment.NewLine, "\n")); // inputs and folders exactly as they were
    }

    [Fact]
    public async Task ADocumentThatLinksOutsideFailsTheJobWithoutStartingTheEngine()
    {
        var rels = System.Text.Encoding.UTF8.GetBytes("""<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="r1" Type="x" Target="file:///C:/secret.txt" TargetMode="External"/></Relationships>""");
        var (start, queue) = Build();
        var source = _folder.Write("linked.docx", Fixtures.Docx(("word/_rels/document.xml.rels", rels)));

        await start.ExecuteAsync(Request(FileFormat.Pdf, source), null, Ct);
        await queue.WhenIdleAsync(Ct);

        var job = Assert.Single(queue.Jobs);
        Assert.Equal(ConversionJobState.Failed, job.State);
        Assert.Contains("links to content outside", job.Error!.SafeMessage, StringComparison.Ordinal);
        Assert.False(_office.Started);
        Assert.Empty(OutNames());
    }

    private sealed class Watcher(Action<ConversionJobSnapshot> onNext) : IObserver<ConversionJobSnapshot>
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
