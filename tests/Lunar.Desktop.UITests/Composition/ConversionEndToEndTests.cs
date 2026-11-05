using Lunar.Application.Abstractions.Conversion;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Conversion.Models;
using Lunar.Application.Conversion.UseCases;
using Lunar.Conversion.Output;
using Lunar.Conversion.Pipeline;
using Lunar.Conversion.Providers;
using Lunar.Desktop.Composition;
using Lunar.Desktop.UITests.Features;
using Lunar.Domain.Conversion;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit;

namespace Lunar.Desktop.UITests.Composition;

// BE-27: the whole conversion pipeline as the container builds it, on the real operating-system PDF renderer and the real disk:
// the use case the screens call, the planner, the queue, the orchestrator, the providers and the output step. Only the folder for
// working files is moved into a scratch folder, so a test never writes into the user's %LocalAppData%. Synthetic files only.
public sealed class ConversionEndToEndTests : IDisposable
{
    private sealed class NullLog : IDiagnosticLog
    {
        public void Write(DiagnosticEvent diagnosticEvent)
        {
        }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lunar-be27-" + Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _services;

    public ConversionEndToEndTests()
    {
        Directory.CreateDirectory(In);
        Directory.CreateDirectory(Out);
        _services = ServiceRegistration.BuildServiceProvider(s =>
        {
            s.AddSingleton<IDiagnosticLog>(new NullLog());
            // Same parts as the default registration, with the working folder inside the scratch folder.
            s.AddSingleton<ConversionOrchestrator>(sp => new ConversionOrchestrator(
                Work,
                new ImageConversionProvider(),
                new PdfCreationProvider(),
                new DocxEmbeddingProvider(),
                new PdfRasterizationProvider(sp.GetRequiredService<IPdfPageRasterizer>()),
                new LibreOfficeRendererProvider(new NoLibreOffice()),
                new OutputCommitter()));
        });
    }

    public void Dispose()
    {
        _services.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string In => Path.Combine(_root, "in");

    private string Out => Path.Combine(_root, "out");

    private string Work => Path.Combine(_root, "work");

    private string MakePdf(string name, int pages, float width = 300, float height = 200)
    {
        var path = Path.Combine(In, name);
        using var stream = new FileStream(path, FileMode.CreateNew);
        using var document = SKDocument.CreatePdf(stream);
        for (var page = 1; page <= pages; page++)
        {
            var canvas = document.BeginPage(width, height);
            canvas.Clear(new SKColor((byte)(40 + (page * 7 % 200)), 90, (byte)(200 - (page * 5 % 150))));
            document.EndPage();
        }

        document.Close();
        return path;
    }

    private string MakePng(string name)
    {
        var path = Path.Combine(In, name);
        using var bitmap = new SKBitmap(32, 24);
        bitmap.Erase(new SKColor(200, 30, 30));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private string[] OutNames() => [.. Directory.GetFiles(Out).Select(f => Path.GetFileName(f)!).OrderBy(n => n, StringComparer.Ordinal)];

    [Fact]
    public void TheContainerBuildsTheWholePipelineAndClosesItCleanly()
    {
        using var services = ServiceRegistration.BuildServiceProvider(s => s.AddSingleton<IDiagnosticLog>(new NullLog()));

        Assert.NotNull(services.GetRequiredService<StartConversion>());
        Assert.Same(services.GetRequiredService<IConversionQueue>(), services.GetRequiredService<IConversionQueue>());
        Assert.Same(services.GetRequiredService<ConversionOrchestrator>(), services.GetRequiredService<ConversionOrchestrator>());
        // Disposing here, with the queue resolved, is the app's exit: it must not throw.
    }

    [Fact]
    public async Task TheUsageSourceAndTokenStoreAreInTheContainerAndAskNothingUntilUsed()
    {
        using var services = ServiceRegistration.BuildServiceProvider(s => s.AddSingleton<IDiagnosticLog>(new NullLog()));

        var store = services.GetRequiredService<Lunar.Application.Abstractions.Usage.IUsageTokenStore>();
        Assert.Same(store, services.GetRequiredService<Lunar.Application.Abstractions.Usage.IUsageCredentialSource>());
        Assert.NotNull(services.GetRequiredService<Lunar.Application.Abstractions.Usage.IUsageSource>());
        Assert.Equal(Lunar.Windows.Security.DpapiUsageTokenStore.DefaultPath, Lunar.Windows.Security.DpapiUsageTokenStore.DefaultPath);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task TheDefaultRegistrationKeepsWordRoutesOffEvenIfLibreOfficeWereInstalled()
    {
        using var services = ServiceRegistration.BuildServiceProvider(s => s.AddSingleton<IDiagnosticLog>(new NullLog()));

        Assert.IsAssignableFrom<ILibreOfficeLocator>(services.GetRequiredService<ILibreOfficeLocator>());
        Assert.False(ServiceRegistration.DocxRoutesEnabled); // the hold of ADR-0005
        Assert.False(await services.GetRequiredService<IRendererAvailability>().IsAvailableAsync(Ct));
    }

    [Fact]
    public async Task WordRoutesAreRefusedUntilALayoutEngineIsWired()
    {
        var start = _services.GetRequiredService<StartConversion>();
        var docx = Path.Combine(In, "w.docx");
        using (var zip = new System.IO.Compression.ZipArchive(File.Create(docx), System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "[Content_Types].xml", "word/document.xml" })
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write("<x/>");
            }
        }

        var started = await start.ExecuteAsync(new ConversionRequest([docx], FileFormat.Pdf, Out), null, Ct);

        Assert.True(started.IsSuccess);
        Assert.Empty(started.Value.JobIds);
        Assert.Equal(RejectionReason.RendererMissing, Assert.Single(started.Value.Plan.Rejected).Reason);
    }

    [Fact]
    public async Task AMixedBatchOfPicturesAndAPdfConvertsOnTheRealRenderer()
    {
        var start = _services.GetRequiredService<StartConversion>();
        var queue = _services.GetRequiredService<IConversionQueue>();
        var pdf = MakePdf("doc.pdf", 3);
        var png = MakePng("pic.png");

        var asPng = await start.ExecuteAsync(new ConversionRequest([pdf], FileFormat.Png, Out), null, Ct);
        var asJpg = await start.ExecuteAsync(new ConversionRequest([png], FileFormat.Jpg, Out), null, Ct);
        var asPdf = await start.ExecuteAsync(new ConversionRequest([png], FileFormat.Pdf, Out), null, Ct);
        Assert.True(asPng.IsSuccess && asJpg.IsSuccess && asPdf.IsSuccess);
        await queue.WhenIdleAsync(Ct);

        Assert.All(queue.Jobs, j => Assert.True(j.State == ConversionJobState.Succeeded, j.Error?.SafeMessage));
        Assert.Equal(["doc_page_001.png", "doc_page_002.png", "doc_page_003.png", "pic.jpg", "pic.pdf"], OutNames());
        Assert.Empty(Directory.GetFileSystemEntries(Work));
    }

    [Fact]
    public async Task CancellingARealPdfJobAfterPagesWereDrawnLeavesNoFileAnywhere()
    {
        var start = _services.GetRequiredService<StartConversion>();
        var queue = _services.GetRequiredService<IConversionQueue>();
        // 100 big pages: far too slow to finish before the cancel below, and several are in the working folder when it comes.
        var pdf = MakePdf("long.pdf", 100, 600, 800);
        var inputBefore = File.ReadAllBytes(pdf);
        var drawn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = queue.Changes.Subscribe(new Watcher(s =>
        {
            if (s.Progress.PagesCompleted >= 3)
                drawn.TrySetResult();
        }));

        var started = await start.ExecuteAsync(new ConversionRequest([pdf], FileFormat.Png, Out), new ConversionChoices { Pdf = new PdfRasterizationOptions { Dpi = PdfRasterizationOptions.Dpi300 } }, Ct);
        await drawn.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        var working = Directory.GetDirectories(Work).SelectMany(d => Directory.GetFiles(d, "*.png")).Count();
        Assert.True(working >= 3, $"pages were in the working folder when the cancel came ({working})");

        Assert.True(queue.Cancel(started.Value.JobIds[0]).IsSuccess);
        await queue.WhenIdleAsync(Ct);

        var job = Assert.Single(queue.Jobs);
        Assert.Equal(ConversionJobState.Cancelled, job.State);
        Assert.Empty(job.OutputPaths);
        Assert.Empty(OutNames());
        Assert.Empty(Directory.GetFileSystemEntries(Work));
        Assert.Equal(inputBefore, File.ReadAllBytes(pdf));
        Assert.Empty(Directory.GetFiles(Out, "*", SearchOption.AllDirectories)); // not even a part file
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
