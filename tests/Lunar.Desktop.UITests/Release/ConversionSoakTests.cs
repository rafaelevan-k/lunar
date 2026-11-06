using System.Diagnostics;
using System.Security.Cryptography;
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

namespace Lunar.Desktop.UITests.Release;

// BE-30 (end-to-end stress, cancellation, failure injection, resource measurement): the conversion pipeline exactly as the container
// builds it, on the real disk and the real Windows PDF renderer, under a mixed load. Long jobs hold both workers while failures are
// injected into the waiting ones (source deleted, source locked by another program, source broken after the type check); some jobs
// are cancelled at once and some while running. Afterwards every job must have ended, every original must be byte-for-byte unchanged,
// no part file or working folder may be left, and managed memory must come back down. Synthetic files only; working folders in scratch.
// Runs on its own (no other test class at the same time): its load on the Windows PDF renderer would otherwise slow the page tests
// that wait on that renderer (the known ConverterPageTests timeout under load, FE-23).
[CollectionDefinition(nameof(ConversionSoakTests), DisableParallelization = true)]
public sealed class SoakCollection;

[Collection(nameof(ConversionSoakTests))]
public sealed class ConversionSoakTests : IDisposable
{
    private sealed class NullLog : IDiagnosticLog
    {
        public void Write(DiagnosticEvent diagnosticEvent)
        {
        }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lunar-be30-" + Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _services;

    public ConversionSoakTests()
    {
        Directory.CreateDirectory(In);
        Directory.CreateDirectory(Out);
        _services = ServiceRegistration.BuildServiceProvider(s =>
        {
            s.AddSingleton<IDiagnosticLog>(new NullLog());
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
        catch (UnauthorizedAccessException)
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
        using (var stream = new FileStream(path, FileMode.CreateNew))
        using (var document = SKDocument.CreatePdf(stream))
        {
            for (var page = 1; page <= pages; page++)
            {
                var canvas = document.BeginPage(width, height);
                canvas.Clear(new SKColor((byte)(40 + (page * 7 % 200)), 90, (byte)(200 - (page * 5 % 150))));
                document.EndPage();
            }

            document.Close();
        }

        return path;
    }

    private string MakeImage(string name, SKEncodedImageFormat format, int width = 320, int height = 240)
    {
        var path = Path.Combine(In, name);
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = new SKColor(30, 160, 90, 180) };
            canvas.DrawCircle(width / 2f, height / 2f, height / 3f, paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private async Task<int> StartOne(StartConversion start, string source, FileFormat target, ConversionChoices? choices = null)
    {
        var started = await start.ExecuteAsync(new ConversionRequest([source], target, Out), choices, Ct);
        Assert.True(started.IsSuccess, started.Error?.SafeMessage);
        return Assert.Single(started.Value.JobIds);
    }

    [Fact]
    public async Task AMixedLoadWithCancellationsAndInjectedFailuresEndsCleanlyAndLeavesOriginalsUntouched()
    {
        var start = _services.GetRequiredService<StartConversion>();
        var queue = _services.GetRequiredService<IConversionQueue>();
        var output = TestContext.Current.TestOutputHelper ?? throw new InvalidOperationException("No test output.");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var managedBefore = GC.GetTotalMemory(forceFullCollection: true);
        using var process = Process.GetCurrentProcess();
        var peakWorkingSet = process.WorkingSet64;
        using var sampler = new Timer(_ =>
        {
            process.Refresh();
            peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
        }, null, 0, 50);
        var clock = Stopwatch.StartNew();

        // Two long PDF jobs first: with two workers (the default) they keep everything else waiting while failures are injected.
        var longA = MakePdf("long-a.pdf", 40, 600, 800);
        var longB = MakePdf("long-b.pdf", 40, 600, 800);
        var hi = new ConversionChoices { Pdf = new PdfRasterizationOptions { Dpi = PdfRasterizationOptions.Dpi150 } };
        var longJobA = await StartOne(start, longA, FileFormat.Png, hi);
        var longJobB = await StartOne(start, longB, FileFormat.Png, hi);

        // The ordinary load: every supported non-Word route, several times.
        var sources = new List<string>();
        var expectSuccess = new List<int>();
        for (var i = 0; i < 6; i++)
        {
            var png = MakeImage($"pic{i}.png", SKEncodedImageFormat.Png);
            var jpg = MakeImage($"photo{i}.jpg", SKEncodedImageFormat.Jpeg);
            var pdf = MakePdf($"doc{i}.pdf", 3);
            sources.AddRange([png, jpg, pdf]);
            expectSuccess.Add(await StartOne(start, png, FileFormat.Jpg));
            expectSuccess.Add(await StartOne(start, png, FileFormat.Docx));
            expectSuccess.Add(await StartOne(start, jpg, FileFormat.Png));
            expectSuccess.Add(await StartOne(start, jpg, FileFormat.Pdf));
            expectSuccess.Add(await StartOne(start, pdf, FileFormat.Jpg));
        }

        // Injected failures on waiting jobs. Each passed the planner's checks when it was queued; the world changes afterwards.
        var deleted = MakeImage("deleted.png", SKEncodedImageFormat.Png);
        var deletedJob = await StartOne(start, deleted, FileFormat.Jpg);
        File.Delete(deleted);

        var locked = MakeImage("locked.png", SKEncodedImageFormat.Png);
        var lockedHash = Hash(locked);
        var lockedJob = await StartOne(start, locked, FileFormat.Pdf);
        // Held by "another program" until that job has ended, then released.
        var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        var lockedEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lockWatch = queue.Changes.Subscribe(new Watcher(s =>
        {
            if (s.JobId == lockedJob && s.IsFinal)
                lockedEnded.TrySetResult();
        }));

        var broken = MakeImage("broken.png", SKEncodedImageFormat.Png);
        var brokenJob = await StartOne(start, broken, FileFormat.Jpg);
        var head = File.ReadAllBytes(broken).AsSpan(0, 40).ToArray(); // the PNG signature and header survive; the pictures do not
        File.WriteAllBytes(broken, [.. head, .. new byte[200]]);
        sources.Add(broken);

        // Cancellations: two waiting jobs at once, and one of the long jobs while it runs.
        var cancelledWaiting = new[] { expectSuccess[3], expectSuccess[11] };
        foreach (var id in cancelledWaiting)
        {
            Assert.True(queue.Cancel(id).IsSuccess);
            expectSuccess.Remove(id);
        }

        sources.AddRange([longA, longB]);
        var originals = sources.ToDictionary(s => s, Hash);
        originals[locked] = lockedHash; // it cannot be read while held
        await Task.Delay(300, Ct);
        Assert.True(queue.Cancel(longJobA).IsSuccess);

        try
        {
            await lockedEnded.Task.WaitAsync(TimeSpan.FromMinutes(5), Ct);
        }
        finally
        {
            hold.Dispose();
        }

        await queue.WhenIdleAsync(Ct).WaitAsync(TimeSpan.FromMinutes(5), Ct);
        clock.Stop();

        var jobs = queue.Jobs.ToDictionary(j => j.JobId);
        output.WriteLine($"jobs={jobs.Count} elapsed={clock.Elapsed.TotalSeconds:F1}s peakWorkingSet={peakWorkingSet / 1048576.0:F0}MB");
        foreach (var job in jobs.Values.OrderBy(j => j.JobId))
            output.WriteLine($"  job {job.JobId}: {job.State} {job.Error?.Code}");

        // Every job ended; nothing is left half-way.
        Assert.All(jobs.Values, j => Assert.Contains(j.State, new[] { ConversionJobState.Succeeded, ConversionJobState.Failed, ConversionJobState.Cancelled }));
        Assert.All(expectSuccess, id => Assert.True(jobs[id].State == ConversionJobState.Succeeded, $"job {id}: {jobs[id].State} {jobs[id].Error?.SafeMessage}"));
        Assert.Equal(ConversionJobState.Succeeded, jobs[longJobB].State);
        Assert.All(cancelledWaiting, id => Assert.Equal(ConversionJobState.Cancelled, jobs[id].State));
        Assert.Contains(jobs[longJobA].State, new[] { ConversionJobState.Cancelled, ConversionJobState.Succeeded }); // it may have finished first
        Assert.Equal(ConversionJobState.Failed, jobs[deletedJob].State);
        Assert.Equal(ConversionJobState.Failed, jobs[brokenJob].State);
        Assert.Equal(ConversionJobState.Failed, jobs[lockedJob].State); // held by another program for its whole turn

        // Originals are byte-for-byte unchanged; failed and cancelled jobs published nothing; no part file, no working folder.
        Assert.All(originals, pair => Assert.Equal(pair.Value, Hash(pair.Key)));
        Assert.All(jobs.Values.Where(j => j.State != ConversionJobState.Succeeded), j => Assert.Empty(j.OutputPaths));
        Assert.All(jobs.Values.Where(j => j.State == ConversionJobState.Succeeded).SelectMany(j => j.OutputPaths), p => Assert.True(File.Exists(p), p));
        var everyOutput = Directory.GetFiles(Out, "*", SearchOption.AllDirectories);
        Assert.Equal(jobs.Values.Sum(j => j.OutputPaths.Count), everyOutput.Length);
        Assert.Empty(Directory.GetFileSystemEntries(Work));

        // Every successful picture decodes; every PDF and Word file starts with its signature.
        foreach (var path in everyOutput)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".png" or ".jpg" or ".jpeg")
            {
                using var decoded = SKBitmap.Decode(path);
                Assert.True(decoded is { Width: > 0, Height: > 0 }, path);
            }
            else if (ext == ".pdf")
            {
                Assert.Equal("%PDF"u8.ToArray(), File.ReadAllBytes(path)[..4]);
            }
            else if (ext == ".docx")
            {
                Assert.Equal("PK"u8.ToArray(), File.ReadAllBytes(path)[..2]);
            }
        }

        // Managed memory comes back down once the jobs are over (no buffers or bitmaps kept by the pipeline).
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var managedAfter = GC.GetTotalMemory(forceFullCollection: true);
        output.WriteLine($"managed before={managedBefore / 1048576.0:F1}MB after={managedAfter / 1048576.0:F1}MB");
        Assert.True(managedAfter - managedBefore < 64L * 1024 * 1024, $"managed memory grew by {(managedAfter - managedBefore) / 1048576.0:F1} MB");
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
