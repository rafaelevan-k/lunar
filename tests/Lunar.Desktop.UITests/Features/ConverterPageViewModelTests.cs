using Lunar.Application.Abstractions.Conversion;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Shell.UseCases;
using Lunar.Conversion.Planning;
using Lunar.Desktop.Composition;
using Lunar.Desktop.Features.Converter;
using Lunar.Desktop.Shell;
using Lunar.Domain.Conversion;
using SkiaSharp;
using Xunit;

namespace Lunar.Desktop.UITests.Features;

// FE-20: the Converter page's view model over the real planner and real (synthetic) files on disk. Only the renderer answer, the
// file dialog and the log are fakes.
public sealed class ConverterPageViewModelTests : IDisposable
{
    private sealed class Renderer(bool available, bool throws = false) : IRendererAvailability
    {
        public Task<bool> IsAvailableAsync(CancellationToken ct) => throws ? throw new InvalidOperationException("boom") : Task.FromResult(available);
    }

    private sealed class Picker : IFilePicker
    {
        public IReadOnlyList<string>? Answer { get; set; }

        public Exception? Throws { get; set; }

        public Func<bool>? WhileOpen { get; set; }

        public bool DialogWasOpen { get; private set; }

        public int Calls { get; private set; }

        public IReadOnlyList<string>? PickFiles()
        {
            Calls++;
            DialogWasOpen = WhileOpen?.Invoke() ?? false;
            return Throws is null ? Answer : throw Throws;
        }
    }

    private sealed class RecordingLog : IDiagnosticLog
    {
        public List<DiagnosticEvent> Events { get; } = [];

        public void Write(DiagnosticEvent diagnosticEvent)
        {
            lock (Events)
                Events.Add(diagnosticEvent);
        }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lunar-fe20-" + Guid.NewGuid().ToString("N"));
    private readonly Picker _picker = new();
    private readonly RecordingLog _log = new();
    private readonly List<ConverterPageViewModel> _models = [];

    public ConverterPageViewModelTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var model in _models)
            model.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ConverterPageViewModel Model(bool renderer = false, bool rendererThrows = false)
    {
        var model = new ConverterPageViewModel(new ConversionPlanner(new Renderer(renderer, rendererThrows)), new Renderer(renderer, rendererThrows), _picker, work => work(), _log);
        _models.Add(model);
        return model;
    }

    private string Png(string name)
    {
        var path = Path.Combine(_root, name);
        using var bitmap = new SKBitmap(16, 12);
        bitmap.Erase(new SKColor(10, 120, 200));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private string Pdf(string name)
    {
        var path = Path.Combine(_root, name);
        using var stream = new FileStream(path, FileMode.Create);
        using var document = SKDocument.CreatePdf(stream);
        document.BeginPage(200, 100);
        document.EndPage();
        document.Close();
        return path;
    }

    private string Docx(string name)
    {
        var path = Path.Combine(_root, name);
        using var zip = new System.IO.Compression.ZipArchive(File.Create(path), System.IO.Compression.ZipArchiveMode.Create);
        foreach (var entry in new[] { "[Content_Types].xml", "word/document.xml" })
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
            writer.Write("<x/>");
        }

        return path;
    }

    private string Junk(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static async Task<ConverterPageViewModel> Added(ConverterPageViewModel model, params string[] paths)
    {
        model.AddFiles(paths);
        await model.WhenCheckedAsync();
        return model;
    }

    private static TargetOption Target(ConverterPageViewModel model, FileFormat format) => model.Targets.Single(t => t.Format == format);

    // ---- the empty page ----------------------------------------------------------------------------------------------

    [Fact]
    public void AnEmptyPageOffersNothingAndSaysToAddFiles()
    {
        var model = Model();

        Assert.True(model.IsEmpty);
        Assert.False(model.HasItems);
        Assert.Equal(5, model.Targets.Count);
        Assert.All(model.Targets, t => Assert.False(t.IsAvailable));
        Assert.All(model.Targets, t => Assert.Contains("Add files first", t.Note, StringComparison.Ordinal));
        Assert.Null(model.SelectedFormat);
    }

    // ---- formats filtered by what the list can do --------------------------------------------------------------------

    [Fact]
    public async Task APictureOffersEveryFormatAndPicksTheFirstThatDoesSomething()
    {
        var model = await Added(Model(), Png("a.png"));

        var item = Assert.Single(model.Items);
        Assert.Equal(ConverterItemStatus.Ready, item.Status);
        Assert.All(model.Targets, t => Assert.True(t.IsAvailable, t.Note));
        Assert.Equal(FileFormat.Jpg, model.SelectedFormat); // PNG itself would only be a copy
        Assert.True(Target(model, FileFormat.Jpg).IsSelected);
        Assert.Equal("1 file ready", model.SummaryText);
    }

    [Fact]
    public async Task APdfCannotBecomeWordAndSaysWhy()
    {
        var model = await Added(Model(), Pdf("a.pdf"));

        var word = Target(model, FileFormat.Docx);
        Assert.False(word.IsAvailable);
        Assert.Contains("PDF files can't become DOCX", word.Note, StringComparison.Ordinal);
        Assert.Equal(FileFormat.Png, model.SelectedFormat);
        Assert.Equal(ConverterItemStatus.Ready, model.Items[0].Status);
    }

    [Fact]
    public async Task AMixedListOffersOnlyWhatEveryFileCanBecome()
    {
        var model = await Added(Model(), Png("a.png"), Pdf("b.pdf"));

        Assert.False(Target(model, FileFormat.Docx).IsAvailable);
        Assert.True(Target(model, FileFormat.Pdf).IsAvailable);
        Assert.True(Target(model, FileFormat.Jpg).IsAvailable);
        Assert.All(model.Items, i => Assert.Equal(ConverterItemStatus.Ready, i.Status));
    }

    [Fact]
    public async Task AWordFileNeedsTheLayoutEngineAndTheNoteSaysSo()
    {
        var model = await Added(Model(renderer: false), Docx("w.docx"));

        var pdf = Target(model, FileFormat.Pdf);
        Assert.False(pdf.IsAvailable);
        Assert.Contains("LibreOffice", pdf.Note, StringComparison.Ordinal);
        Assert.Contains("never installs", pdf.Note, StringComparison.Ordinal);
        Assert.True(Target(model, FileFormat.Docx).IsAvailable); // a plain copy needs no engine
        Assert.Equal(FileFormat.Docx, model.SelectedFormat); // the only thing it can do now is be copied
        Assert.Equal(ConverterItemStatus.Ready, model.Items[0].Status);
    }

    [Fact]
    public async Task AWordFileCanBecomeAPdfWhenALayoutEngineIsInstalled()
    {
        var model = await Added(Model(renderer: true), Docx("w.docx"));

        var pdf = Target(model, FileFormat.Pdf);
        Assert.True(pdf.IsAvailable);
        Assert.Contains("layout may differ", pdf.Note, StringComparison.Ordinal);
        Assert.Equal(FileFormat.Png, model.SelectedFormat);
    }

    [Fact]
    public async Task ARendererThatFailsToAnswerJustMeansNoWordRoutes()
    {
        var model = await Added(Model(rendererThrows: true), Png("a.png"), Docx("w.docx"));

        Assert.False(Target(model, FileFormat.Pdf).IsAvailable);
        Assert.Contains(_log.Events, e => e.EventId == "converter.renderer.check_failed");
        Assert.Null(model.ErrorMessage);
    }

    [Fact]
    public async Task TheUsersChoiceStaysWhileItFitsAndYieldsWhenItDoesNot()
    {
        var model = await Added(Model(), Png("a.png"));
        model.Targets.Single(t => t.Format == FileFormat.Docx).SelectCommand.Execute(null);
        await model.WhenCheckedAsync();
        Assert.Equal(FileFormat.Docx, model.SelectedFormat);

        await Added(model, Png("b.png"));
        Assert.Equal(FileFormat.Docx, model.SelectedFormat); // still fits both

        await Added(model, Pdf("c.pdf"));
        Assert.Equal(FileFormat.Png, model.SelectedFormat); // a PDF cannot become Word, so the page moved on
    }

    [Fact]
    public async Task PressingAnUnavailableFormatDoesNothing()
    {
        var model = await Added(Model(), Pdf("a.pdf"));
        var before = model.SelectedFormat;

        Assert.False(Target(model, FileFormat.Docx).SelectCommand.CanExecute(null));
        Target(model, FileFormat.Docx).SelectCommand.Execute(null);
        await model.WhenCheckedAsync();

        Assert.Equal(before, model.SelectedFormat);
    }

    // ---- invalid files are explained ---------------------------------------------------------------------------------

    [Fact]
    public async Task AnUnsupportedTypeIsRefusedWithAReason()
    {
        var model = await Added(Model(), Junk("notes.txt", [1, 2, 3]));

        var item = Assert.Single(model.Items);
        Assert.Equal(ConverterItemStatus.Refused, item.Status);
        Assert.Equal("Unknown type", item.KindText);
        Assert.Contains("isn't supported", item.Message, StringComparison.Ordinal);
        Assert.Equal("1 can't be converted", model.SummaryText);
        Assert.Null(model.SelectedFormat);
    }

    [Fact]
    public async Task AFileThatIsNotWhatItsNameSaysIsRefusedWithAReason()
    {
        var model = await Added(Model(), Junk("fake.png", [0x25, 0x50, 0x44, 0x46, 1, 2, 3, 4, 5, 6, 7, 8]), Png("good.png"));

        Assert.Equal(ConverterItemStatus.Refused, model.Items[0].Status);
        Assert.False(string.IsNullOrWhiteSpace(model.Items[0].Message));
        Assert.Equal(ConverterItemStatus.Ready, model.Items[1].Status);
        Assert.Equal("1 file ready, 1 can't be converted", model.SummaryText);
    }

    [Fact]
    public async Task AFolderAndAMissingFileAreRefusedWithAReason()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "dir.png")).FullName;
        var model = await Added(Model(), folder, Path.Combine(_root, "gone.png"));

        Assert.All(model.Items, i => Assert.Equal(ConverterItemStatus.Refused, i.Status));
        Assert.All(model.Items, i => Assert.False(string.IsNullOrWhiteSpace(i.Message)));
    }

    [Fact]
    public async Task AFailedCheckOfTheWholeListIsShownOnThePage()
    {
        var model = await Added(Model(), Path.Combine(_root, "no-such-folder", "a.png"));

        Assert.Contains("folder", model.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ConverterItemStatus.NotChecked, model.Items[0].Status);
    }

    // ---- the list ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFileAlreadyListedIsNotAddedTwiceInAnyLetterCase()
    {
        var path = Png("a.png");
        var model = await Added(Model(), path);

        var added = model.AddFiles([path.ToUpperInvariant(), path]);
        await model.WhenCheckedAsync();

        Assert.Equal(0, added);
        Assert.Single(model.Items);
        Assert.Contains("already in the list", model.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AtMostTheBatchLimitIsAddedAndTheRestIsReported()
    {
        var model = Model();
        var paths = Enumerable.Range(0, 103).Select(i => Path.Combine(_root, $"f{i}.png")).ToArray();

        var added = model.AddFiles(paths);
        await model.WhenCheckedAsync();

        Assert.Equal(100, added);
        Assert.Equal(100, model.Items.Count);
        Assert.Contains("at most 100", model.Notice, StringComparison.Ordinal);
        Assert.Contains("3 files were not added", model.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BlankEntriesAreIgnored()
    {
        var model = Model();

        Assert.Equal(0, model.AddFiles([null, "", "   "]));
        Assert.Equal(0, model.AddFiles(null));
        await model.WhenCheckedAsync();

        Assert.True(model.IsEmpty);
    }

    [Fact]
    public async Task RemovingAFileRechecksTheListAndTheFormats()
    {
        var model = await Added(Model(), Png("a.png"), Pdf("b.pdf"));
        Assert.False(Target(model, FileFormat.Docx).IsAvailable);

        model.Items.Single(i => i.Name == "b.pdf").RemoveCommand.Execute(null);
        await model.WhenCheckedAsync();

        Assert.Equal(["a.png"], model.Items.Select(i => i.Name));
        Assert.True(Target(model, FileFormat.Docx).IsAvailable);
    }

    [Fact]
    public async Task ClearingEmptiesTheListAndTheFormats()
    {
        var model = await Added(Model(), Png("a.png"), Png("b.png"));

        model.ClearCommand.Execute(null);
        await model.WhenCheckedAsync();

        Assert.True(model.IsEmpty);
        Assert.Null(model.SelectedFormat);
        Assert.All(model.Targets, t => Assert.False(t.IsAvailable));
        Assert.Equal(string.Empty, model.OutputFolder);
        Assert.False(model.ClearCommand.CanExecute(null));
    }

    [Fact]
    public async Task TwoQuickAdditionsEndInOneConsistentAnswer()
    {
        var model = Model();

        model.AddFiles([Png("a.png")]);
        model.AddFiles([Png("b.png")]); // the first check is replaced before it finishes
        await model.WhenCheckedAsync();

        Assert.Equal(2, model.Items.Count);
        Assert.All(model.Items, i => Assert.Equal(ConverterItemStatus.Ready, i.Status));
        Assert.Equal(2, model.Plan!.Jobs.Count);
    }

    [Fact]
    public async Task AResultNameThatIsTakenIsShownBeforeConverting()
    {
        var path = Png("a.png");
        File.WriteAllBytes(Path.Combine(_root, "a.jpg"), [1, 2, 3]);
        var model = await Added(Model(), path);

        Assert.Equal(FileFormat.Jpg, model.SelectedFormat);
        Assert.Contains("a (2).jpg", model.Items[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheResultsFolderIsTheFolderOfTheFirstFile()
    {
        var model = await Added(Model(), Png("a.png"));

        Assert.Equal(_root, model.OutputFolder);
    }

    // ---- the file dialog and drag and drop ---------------------------------------------------------------------------

    [Fact]
    public async Task TheFileDialogAddsWhatWasChosenAndIsMarkedOpenWhileItIsUp()
    {
        var model = Model();
        _picker.Answer = [Png("a.png"), Png("b.png")];
        _picker.WhileOpen = () => model.IsDialogOpen;

        model.AddFilesCommand.Execute(null);
        await model.WhenCheckedAsync();

        Assert.Equal(1, _picker.Calls);
        Assert.True(_picker.DialogWasOpen);
        Assert.False(model.IsDialogOpen);
        Assert.Equal(2, model.Items.Count);
    }

    [Fact]
    public void ACancelledFileDialogAddsNothing()
    {
        var model = Model();
        _picker.Answer = null;

        model.AddFilesCommand.Execute(null);

        Assert.True(model.IsEmpty);
        Assert.False(model.IsDialogOpen);
    }

    [Fact]
    public void AFileDialogThatFailsIsExplainedAndDoesNotCrashTheApp()
    {
        var model = Model();
        _picker.Throws = new InvalidOperationException("no shell");

        model.AddFilesCommand.Execute(null);

        Assert.Contains("drag files", model.ErrorMessage, StringComparison.Ordinal);
        Assert.False(model.IsDialogOpen);
        Assert.True(model.AddFilesCommand.CanExecute(null));
    }

    [Fact]
    public void DraggingOverThePageIsReportedAndEnds()
    {
        var model = Model();

        model.SetDragActive(true);
        Assert.True(model.IsDragActive);
        model.SetDragActive(false);
        Assert.False(model.IsDragActive);
    }

    [Fact]
    public void TheIslandStaysOpenWhileADialogIsUpOrAFilesAreDragged()
    {
        var island = new TransitionIsland(SystemClock.Instance);
        var viewModel = new IslandViewModel(island);
        var model = Model();
        using var link = new ConverterGuardLink(viewModel, model);
        _picker.WhileOpen = () => island.Guards.DialogOpen;
        _picker.Answer = null;

        model.AddFilesCommand.Execute(null);
        Assert.True(_picker.DialogWasOpen);
        Assert.False(island.Guards.DialogOpen);

        model.SetDragActive(true);
        Assert.True(island.Guards.DragActive);
        Assert.True(island.Guards.BlocksOutsideClick);
        model.SetDragActive(false);
        Assert.False(island.Guards.DragActive);
    }

    // ---- privacy -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task NoLogEventCarriesAFileNameOrPath()
    {
        _picker.Throws = new InvalidOperationException(Path.Combine(_root, "secret-name.png"));
        var model = Model(rendererThrows: true);
        model.AddFilesCommand.Execute(null);
        await Added(model, Png("secret-name.png"), Junk("also-secret.txt", [1]));

        Assert.NotEmpty(_log.Events);
        lock (_log.Events)
        {
            foreach (var logged in _log.Events)
            {
                var text = logged.ToString();
                Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(_root, text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task ThePageNeverChangesAFile()
    {
        var path = Png("a.png");
        var before = File.ReadAllBytes(path);
        var stamp = File.GetLastWriteTimeUtc(path);

        await Added(Model(), path);

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
        Assert.Equal(["a.png"], Directory.GetFiles(_root).Select(f => Path.GetFileName(f)));
    }

    [Fact]
    public async Task DisposingStopsEverythingQuietly()
    {
        var model = Model();
        model.AddFiles([Png("a.png")]);
        model.Dispose();
        await model.WhenCheckedAsync();

        Assert.Equal(0, model.AddFiles([Png("b.png")]));
        model.ClearCommand.Execute(null);
        model.SetDragActive(true);
        Assert.False(model.IsDragActive);
    }
}
