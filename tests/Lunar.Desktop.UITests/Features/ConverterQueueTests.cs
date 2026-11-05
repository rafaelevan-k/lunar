using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Conversion.Models;
using Lunar.Application.Shell.Models;
using Lunar.Application.Shell.UseCases;
using Lunar.Desktop.Composition;
using Lunar.Desktop.Features.Converter;
using Lunar.Desktop.Shell;
using Lunar.Domain.Conversion;
using Lunar.Domain.Shared;
using Lunar.Windows.IntegrationTests.Support;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit;

namespace Lunar.Desktop.UITests.Features;

// FE-22: the Converter page starting real conversions through the container's queue, orchestrator and providers, on the real operating-system
// PDF renderer and the real disk. Only the dialogs, the file manager and the log are fakes. Synthetic files only.
public sealed class ConverterQueueTests : IDisposable
{
    private sealed class NoPicker : IFilePicker
    {
        public IReadOnlyList<string>? PickFiles() => null;
    }

    private sealed class NoFolders : IFolderPicker
    {
        public string? PickFolder() => null;
    }

    private sealed class NullLog : IDiagnosticLog
    {
        public void Write(DiagnosticEvent diagnosticEvent)
        {
        }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lunar-fe22-" + Guid.NewGuid().ToString("N"));
    private readonly ConverterRig _rig;

    public ConverterQueueTests()
    {
        Directory.CreateDirectory(In);
        Directory.CreateDirectory(Out);
        _rig = ConverterRig.Create(_root + "-rig", new NoPicker(), new NoFolders(), new NullLog());
    }

    public void Dispose()
    {
        _rig.Dispose();
        foreach (var dir in new[] { _root, _root + "-rig" })
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string In => Path.Combine(_root, "in");

    private string Out => Path.Combine(_root, "out");

    private ConverterPageViewModel Page => _rig.Page;

    private ConverterQueueViewModel Queue => _rig.Queue;

    private string Png(string name)
    {
        var path = Path.Combine(In, name);
        using var bitmap = new SKBitmap(32, 24);
        bitmap.Erase(new SKColor(200, 30, 30));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private string Pdf(string name, int pages, float width = 300, float height = 200)
    {
        var path = Path.Combine(In, name);
        using var stream = new FileStream(path, FileMode.Create);
        using var document = SKDocument.CreatePdf(stream);
        for (var i = 0; i < pages; i++)
        {
            document.BeginPage(width, height).Clear(new SKColor((byte)(40 + (i * 7 % 200)), 90, 160));
            document.EndPage();
        }

        document.Close();
        return path;
    }

    // The condition reads a collection the queue updates from another thread, so a read that lands in the middle of a change is retried
    // (it throws InvalidOperationException) instead of failing the test. This race made CancellingARunningJobLeavesNothingAndItCanBeTriedAgain fail once.
    private static bool Holds(Func<bool> condition)
    {
        try
        {
            return condition();
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task WaitUntil(Func<bool> condition, int seconds = 60)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!Holds(condition) && DateTime.UtcNow < end)
            await Task.Delay(10, Ct);
        Assert.True(Holds(condition), "The condition was not met in time.");
    }

    private async Task Choose(FileFormat format, params string[] files)
    {
        Page.AddFiles(files);
        await Page.WhenCheckedAsync();
        Page.Targets.Single(t => t.Format == format).SelectCommand.Execute(null);
        await Page.WhenCheckedAsync();
        Page.Options.SetChosenFolder(Out);
        await Page.WhenCheckedAsync();
    }

    private async Task Start()
    {
        var command = (AsyncRelayCommand)Page.StartCommand;
        Assert.True(command.CanExecute(null), Page.StartHint);
        command.Execute(null);
        await WaitUntil(() => !command.IsRunning);
    }

    private async Task Idle() => await _rig.RawQueue.WhenIdleAsync(Ct);

    private string[] OutNames() => [.. Directory.GetFiles(Out, "*", SearchOption.AllDirectories).Select(f => Path.GetFileName(f)!).OrderBy(n => n, StringComparer.Ordinal)];

    // ---- starting and finishing --------------------------------------------------------------------------------------

    [Fact]
    public async Task ConvertingPicturesQueuesThemShowsDoneAndEmptiesTheList()
    {
        await Choose(FileFormat.Jpg, Png("a.png"), Png("b.png"));
        Assert.True(Page.CanStart);

        await Start();
        await Idle();

        Assert.True(Page.IsEmpty);
        Assert.Equal(ConverterStep.Progress, Page.Step); // the started conversions are shown, one step of their own
        Assert.Equal(2, Queue.Rows.Count);
        Assert.All(Queue.Rows, r => Assert.Equal(ConversionJobState.Succeeded, r.State));
        Assert.Equal(["Saved as a.jpg", "Saved as b.jpg"], Queue.Rows.Select(r => r.ResultText));
        Assert.Equal(["a.jpg", "b.jpg"], OutNames());
        Assert.Equal("2 done", Queue.SummaryText);
        Assert.False(Queue.IsBusy);
    }

    [Fact]
    public async Task EachFinishedConversionRaisesOneCelebrationAndNothingElseDoes()
    {
        var done = 0;
        Queue.JobSucceeded += (_, _) => Interlocked.Increment(ref done);
        await Choose(FileFormat.Jpg, Png("a.png"), Png("b.png"));
        await Start();
        await Idle();
        Assert.Equal(2, done);

        Queue.ClearFinishedCommand.Execute(null);
        await Choose(FileFormat.Png, Pdf("long.pdf", 100, 600, 800));
        Page.Options.Dpi.Select(PdfRasterizationOptions.Dpi300);
        await Page.WhenCheckedAsync();
        await Start();
        await WaitUntil(() => Queue.Rows.Any(r => r.Fraction is > 0.02));
        Queue.CancelAllCommand.Execute(null);
        await Idle();

        Assert.Equal(2, done); // a cancelled conversion is not a celebration
    }

    [Fact]
    public async Task ShowInFolderAsksTheFileManagerAndReportsItsFailure()
    {
        await Choose(FileFormat.Jpg, Png("a.png"));
        await Start();
        await Idle();
        var row = Assert.Single(Queue.Rows);
        Assert.True(row.CanReveal);

        row.RevealCommand.Execute(null);
        await WaitUntil(() => _rig.Revealer.Shown.Count == 1);
        Assert.Equal(Path.Combine(Out, "a.jpg"), _rig.Revealer.Shown[0]);
        Assert.Null(Queue.ErrorMessage);

        _rig.Revealer.Failure = new OperationError(ErrorCode.IoFailure, "Windows Explorer could not be opened.");
        row.RevealCommand.Execute(null);
        await WaitUntil(() => Queue.ErrorMessage is not null);
        Assert.Equal("Windows Explorer could not be opened.", Queue.ErrorMessage);
    }

    [Fact]
    public async Task PdfPagesShowOnlyRealProgressAndEndAsOneNumberedSet()
    {
        var seen = new List<(double? Row, JobProgress Queue)>();
        using var watcher = _rig.RawQueue.Changes.Subscribe(new Watcher(s =>
        {
            var row = Queue.Rows.FirstOrDefault(r => r.JobId == s.JobId);
            if (row is not null)
            {
                lock (seen)
                    seen.Add((row.Fraction, s.Progress));
            }
        }));
        await Choose(FileFormat.Png, Pdf("book.pdf", 5));

        await Start();
        await Idle();

        Assert.Equal("5 pictures saved", Assert.Single(Queue.Rows).ResultText);
        Assert.Equal(["book_page_001.png", "book_page_002.png", "book_page_003.png", "book_page_004.png", "book_page_005.png"], OutNames());
        lock (seen)
        {
            Assert.NotEmpty(seen);
            foreach (var (row, queue) in seen)
            {
                // A bar value exists only where the converter reported a page count, and it is exactly that fraction.
                if (row is { } value)
                {
                    Assert.True(queue.IsDeterminate);
                    Assert.Equal(queue.Fraction!.Value, value, 6);
                }
            }

            var values = seen.Where(p => p.Row is not null).Select(p => p.Row!.Value).ToList();
            Assert.Equal(values.OrderBy(v => v), values); // never backwards
        }
    }

    [Fact]
    public async Task APictureNeverShowsAMadeUpPercentage()
    {
        var seen = new List<JobRow>();
        using var watcher = _rig.RawQueue.Changes.Subscribe(new Watcher(s =>
        {
            var row = Queue.Rows.FirstOrDefault(r => r.JobId == s.JobId);
            if (row is not null)
            {
                lock (seen)
                    seen.Add(row);
            }
        }));
        await Choose(FileFormat.Jpg, Png("a.png"));

        await Start();
        await Idle();

        lock (seen)
        {
            Assert.NotEmpty(seen);
            Assert.All(seen, r => Assert.Null(r.Fraction));
            Assert.All(seen, r => Assert.Equal(string.Empty, r.ProgressText));
            Assert.All(seen.Where(r => r.State == ConversionJobState.Running), r => Assert.True(r.IsIndeterminate));
            Assert.All(seen.Where(r => r.State == ConversionJobState.Queued), r => Assert.False(r.ShowProgressBar));
        }
    }

    // ---- cancel, retry, and the island ------------------------------------------------------------------------------

    private async Task StartSlowPdf()
    {
        // 100 big pages: far too slow to finish before the test cancels it.
        await Choose(FileFormat.Png, Pdf("long.pdf", 100, 600, 800));
        Page.Options.Dpi.Select(PdfRasterizationOptions.Dpi300);
        await Page.WhenCheckedAsync();
        await Start();
        await WaitUntil(() => Queue.Rows.Any(r => r.Fraction is > 0.02));
    }

    [Fact]
    public async Task CancellingARunningJobLeavesNothingAndItCanBeTriedAgain()
    {
        await StartSlowPdf();
        var running = Assert.Single(Queue.Rows);
        Assert.True(running.CanCancel);
        Assert.NotNull(running.Fraction);
        Assert.Matches(@"^\d+ of 100 pages$", running.ProgressText);

        running.CancelCommand.Execute(null);
        await Idle();

        var cancelled = Assert.Single(Queue.Rows);
        Assert.Equal(ConversionJobState.Cancelled, cancelled.State);
        Assert.Equal("Cancelled. Nothing was saved.", cancelled.StatusText);
        Assert.True(cancelled.CanRetry);
        Assert.False(cancelled.CanCancel);
        Assert.Empty(OutNames());
        Assert.Empty(Directory.GetFileSystemEntries(_rig.Work));

        cancelled.RetryCommand.Execute(null);
        await WaitUntil(() => Queue.Rows.Count == 2 && Queue.Rows[1].State == ConversionJobState.Running);
        Queue.CancelAllCommand.Execute(null);
        await Idle();

        Assert.All(Queue.Rows, r => Assert.Equal(ConversionJobState.Cancelled, r.State));
        Assert.Empty(OutNames());
    }

    [Fact]
    public async Task CollapsingTheIslandNeverCancelsAJobAndTheIslandShowsBusy()
    {
        var island = new TransitionIsland(SystemClock.Instance);
        var viewModel = new IslandViewModel(island);
        using var busy = new ConverterBusyLink(viewModel, Queue);
        viewModel.Toggle();
        viewModel.CompleteExpansion();
        Assert.False(island.IsBusy);

        await StartSlowPdf();
        Assert.True(island.IsBusy);
        Assert.Equal(IslandState.Busy, island.State);

        viewModel.Collapse();
        viewModel.CompleteCollapse();
        await Task.Delay(300, Ct);

        Assert.Equal(IslandState.Idle, island.State);
        Assert.True(island.IsBusy); // the pill still knows work is going on
        Assert.True(Queue.IsBusy);
        Assert.Equal(ConversionJobState.Running, Assert.Single(Queue.Rows).State);

        Queue.CancelAllCommand.Execute(null);
        await Idle();
        Assert.False(island.IsBusy);
    }

    // ---- failures ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFailedJobSaysWhyAndThatNothingWasSaved()
    {
        // A file that looks like a PDF to the check but that the renderer cannot open.
        var path = Path.Combine(In, "broken.pdf");
        File.WriteAllText(path, "%PDF-1.7\n1 0 obj\n<< /Type /Catalog >>\nendobj\ntrailer\n<< /Root 1 0 R >>\nstartxref\n0\n%%EOF\n");
        await Choose(FileFormat.Png, path);

        await Start();
        await Idle();

        var row = Assert.Single(Queue.Rows);
        Assert.Equal(ConversionJobState.Failed, row.State);
        Assert.EndsWith("Nothing was saved.", row.StatusText, StringComparison.Ordinal);
        Assert.True(row.CanRetry);
        Assert.Equal("1 failed", Queue.SummaryText);
        Assert.Empty(OutNames());
        Assert.Empty(Directory.GetFileSystemEntries(_rig.Work));
    }

    [Fact]
    public async Task AProtectedPdfNeedsThePasswordTypedOnThePageAndForgetsItAfterwards()
    {
        var path = Path.Combine(In, "locked.pdf");
        File.WriteAllBytes(path, PdfFixtures.Build(2, userPassword: "open-sesame"));
        await Choose(FileFormat.Png, path);
        await Start();
        await Idle();
        Assert.Equal(ConversionJobState.Failed, Assert.Single(Queue.Rows).State);
        Assert.Empty(OutNames());

        Queue.ClearFinishedCommand.Execute(null);
        await Choose(FileFormat.Png, path);
        Page.Options.SetPdfPassword("open-sesame");
        await Start();
        await Idle();

        Assert.Equal(ConversionJobState.Succeeded, Assert.Single(Queue.Rows).State);
        Assert.Equal(2, OutNames().Length);
        Assert.False(Page.Options.HasPassword); // forgotten once the files were handed to the queue
        Assert.DoesNotContain("open-sesame", Queue.Rows[0].AutomationName, StringComparison.Ordinal);
        Assert.DoesNotContain("open-sesame", Queue.Rows[0].StatusText, StringComparison.Ordinal);
    }

    // ---- replacing needs a yes ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ReplacingExistingResultsAsksFirstAndCancellingChangesNothing()
    {
        File.WriteAllBytes(Path.Combine(Out, "a.jpg"), [1, 2, 3]);
        await Choose(FileFormat.Jpg, Png("a.png"));
        Page.Options.Collisions.Select(CollisionPolicy.Overwrite);
        await Page.WhenCheckedAsync();
        var island = new TransitionIsland(SystemClock.Instance);
        using var guards = new ConverterGuardLink(new IslandViewModel(island), Page);

        ((AsyncRelayCommand)Page.StartCommand).Execute(null);
        await WaitUntil(() => Page.IsConfirmingOverwrite);

        Assert.Empty(Queue.Rows);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(Out, "a.jpg")));
        Assert.True(island.Guards.ConfirmationPending);
        Assert.False(Page.StartCommand.CanExecute(null));

        Page.CancelOverwriteCommand.Execute(null);
        Assert.False(Page.IsConfirmingOverwrite);
        Assert.False(island.Guards.ConfirmationPending);
        Assert.Empty(Queue.Rows);
        Assert.False(Page.IsEmpty); // the list is still there
    }

    [Fact]
    public async Task ConfirmingReplacesOnlyTheResultAndNeverTheOriginal()
    {
        var source = Png("a.png");
        var original = File.ReadAllBytes(source);
        File.WriteAllBytes(Path.Combine(Out, "a.jpg"), [1, 2, 3]);
        await Choose(FileFormat.Jpg, source);
        Page.Options.Collisions.Select(CollisionPolicy.Overwrite);
        await Page.WhenCheckedAsync();
        ((AsyncRelayCommand)Page.StartCommand).Execute(null);
        await WaitUntil(() => Page.IsConfirmingOverwrite);

        var confirm = (AsyncRelayCommand)Page.ConfirmOverwriteCommand;
        Assert.True(confirm.CanExecute(null));
        confirm.Execute(null);
        await WaitUntil(() => !confirm.IsRunning && Queue.Rows.Count == 1);
        await Idle();

        Assert.Equal(ConversionJobState.Succeeded, Assert.Single(Queue.Rows).State);
        Assert.Equal(["a.jpg"], OutNames());
        Assert.NotEqual([1, 2, 3], File.ReadAllBytes(Path.Combine(Out, "a.jpg")));
        Assert.Equal(original, File.ReadAllBytes(source));
    }

    // ---- who may start, and housekeeping -----------------------------------------------------------------------------

    [Fact]
    public async Task NothingStartsWithoutAValidList()
    {
        Assert.False(Page.StartCommand.CanExecute(null));

        Page.AddFiles([Path.Combine(In, "notes.txt")]);
        await Page.WhenCheckedAsync();
        Assert.False(Page.StartCommand.CanExecute(null));
        Assert.Empty(Queue.Rows);
    }

    [Fact]
    public async Task ClearFinishedForgetsOnlyFinishedJobsAndAFreshViewFollowsTheQueue()
    {
        await Choose(FileFormat.Jpg, Png("a.png"));
        await Start();
        await Idle();
        using var second = new ConverterQueueViewModel(
            _rig.Services.GetRequiredService<Lunar.Application.Conversion.UseCases.StartConversion>(), _rig.RawQueue, _rig.Revealer, a => a());
        Assert.Single(second.Rows); // a view created later starts from what the queue already holds

        Assert.True(Queue.ClearFinishedCommand.CanExecute(null));
        Queue.ClearFinishedCommand.Execute(null);

        Assert.Empty(Queue.Rows);
        Assert.False(Queue.HasRows);
        Assert.Empty(_rig.RawQueue.Jobs);
        Assert.False(Queue.ClearFinishedCommand.CanExecute(null));
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
