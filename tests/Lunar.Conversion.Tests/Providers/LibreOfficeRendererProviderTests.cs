using System.Diagnostics;
using Lunar.Application.Conversion.Models;
using Lunar.Conversion.Pipeline;
using Lunar.Conversion.Providers;
using Lunar.Conversion.Tests.Support;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Conversion.Tests.Providers;

// BE-25: how Lunar starts, feeds, times out and stops the optional layout engine, proved with a stand-in program. What the real LibreOffice
// does with a real document is NOT_RUN (it is not installed on this PC and the fidelity check is deferred by ADR-0005).
[Collection("FakeOffice")]
public sealed class LibreOfficeRendererProviderTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeOffice _office = new(Fixtures.Pdf(3));
    private readonly DateTime _started = DateTime.Now.AddSeconds(-1);
    private readonly ConversionLimits _limits = new();
    private readonly List<TempWorkspace> _workspaces = [];

    public void Dispose()
    {
        foreach (var workspace in _workspaces)
            workspace.Dispose();
        _office.Dispose();
        _folder.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TempWorkspace Workspace()
    {
        var workspace = TempWorkspace.Create(Path.Combine(_folder.Root, "work"));
        _workspaces.Add(workspace);
        return workspace;
    }

    private LibreOfficeRendererProvider Provider(TimeSpan? timeout = null) =>
        new(_office, new LibreOfficeOptions { Timeout = timeout ?? TimeSpan.FromSeconds(60), StopWait = TimeSpan.FromSeconds(10) });

    private string Docx(string name = "letter.docx", params (string Name, byte[] Data)[] extra) => _folder.Write(name, Fixtures.Docx(extra));

    private static int PingsAlive(DateTime since) => Process.GetProcessesByName("PING").Count(p =>
    {
        try
        {
            return !p.HasExited && p.StartTime >= since;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    });

    // ---- the happy path and how the engine is started ---------------------------------------------------------------------

    [Fact]
    public async Task AWordFileBecomesAPdfAndTheOriginalIsNeverGivenToTheEngine()
    {
        var source = Docx();
        var before = File.ReadAllBytes(source);
        var workspace = Workspace();
        var pdf = workspace.NewFilePath(".pdf");

        var result = await Provider().ConvertToPdfAsync(source, pdf, workspace, _limits, Ct);

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal(Fixtures.Pdf(3), File.ReadAllBytes(pdf));
        Assert.Equal(result.Value, new FileInfo(pdf).Length);
        Assert.Equal(before, File.ReadAllBytes(source));
        var arguments = _office.Arguments;
        Assert.DoesNotContain(source, arguments); // the engine worked on a copy inside the job's folder
        var input = arguments[^1];
        Assert.StartsWith(workspace.Directory, input, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".docx", input, StringComparison.Ordinal);
        Assert.Equal([Path.GetFileName(source)], Directory.GetFiles(_folder.In).Select(f => Path.GetFileName(f))); // no lock or temporary file next to the original
    }

    [Fact]
    public async Task TheCommandLineIsSeparateSafeArgumentsWithAFreshProfileInTheJobFolder()
    {
        var workspace = Workspace();

        await Provider().ConvertToPdfAsync(Docx(), workspace.NewFilePath(".pdf"), workspace, _limits, Ct);

        var arguments = _office.Arguments;
        Assert.Equal("--headless", arguments[0]);
        Assert.Contains("--norestore", arguments);
        Assert.Contains("--nologo", arguments);
        Assert.Contains("--nolockcheck", arguments);
        Assert.Contains("--nodefault", arguments);
        Assert.Equal("pdf:writer_pdf_Export", arguments[Array.IndexOf(arguments, "--convert-to") + 1]);
        var outdir = arguments[Array.IndexOf(arguments, "--outdir") + 1];
        Assert.StartsWith(workspace.Directory, outdir, StringComparison.OrdinalIgnoreCase);
        var profile = Assert.Single(_office.CommandLine.Split(' '), a => a.StartsWith("-env:UserInstallation=file:///", StringComparison.Ordinal));
        Assert.Contains(Path.GetFileName(workspace.Directory), profile, StringComparison.Ordinal); // inside this job's folder
    }

    [Fact]
    public async Task EachJobGetsItsOwnProfile()
    {
        var first = Workspace();
        await Provider().ConvertToPdfAsync(Docx("a.docx"), first.NewFilePath(".pdf"), first, _limits, Ct);
        var profileOne = ProfileOf(_office.CommandLine);
        _office.Reset();

        var second = Workspace();
        await Provider().ConvertToPdfAsync(Docx("b.docx"), second.NewFilePath(".pdf"), second, _limits, Ct);
        var profileTwo = ProfileOf(_office.CommandLine);

        Assert.NotEqual(profileOne, profileTwo);
    }

    private static string ProfileOf(string commandLine) => commandLine.Split(' ').Single(a => a.StartsWith("-env:UserInstallation=", StringComparison.Ordinal));

    [Fact]
    public void ArgumentsWithSpacesStayOneElementEach()
    {
        var arguments = LibreOfficeRendererProvider.ArgumentsFor(@"C:\my work\profile", @"C:\my work\out", @"C:\my work\in put.docx").ToArray();

        Assert.Equal(@"C:\my work\out", arguments[Array.IndexOf(arguments, "--outdir") + 1]);
        Assert.Equal(@"C:\my work\in put.docx", arguments[^1]);
        Assert.Equal("-env:UserInstallation=file:///C:/my%20work/profile", Assert.Single(arguments, a => a.StartsWith("-env:", StringComparison.Ordinal)));
    }

    // ---- the engine is missing, fails, or produces nothing usable ---------------------------------------------------------

    [Fact]
    public async Task AMissingEngineIsExplainedAndNothingIsStarted()
    {
        _office.Installed = false;
        var workspace = Workspace();

        var result = await Provider().ConvertToPdfAsync(Docx(), workspace.NewFilePath(".pdf"), workspace, _limits, Ct);

        Assert.Equal(ErrorCode.DependencyMissing, result.Error!.Code);
        Assert.Contains("LibreOffice", result.Error.SafeMessage, StringComparison.Ordinal);
        Assert.Contains("never installs", result.Error.SafeMessage, StringComparison.Ordinal);
        Assert.False(_office.Started);
    }

    [Theory]
    [InlineData("fail")]
    [InlineData("garbage")]
    [InlineData("nooutput")]
    public async Task AnEngineThatFailsOrProducesNothingUsableIsAPlainFailureWithNothingLeft(string mode)
    {
        _office.Mode = mode;
        var workspace = Workspace();
        var pdf = workspace.NewFilePath(".pdf");

        var result = await Provider().ConvertToPdfAsync(Docx(), pdf, workspace, _limits, Ct);

        Assert.Equal(ErrorCode.Unexpected, result.Error!.Code);
        Assert.Equal("LibreOffice could not convert this document.", result.Error.SafeMessage);
        Assert.False(File.Exists(pdf));
    }

    [Fact]
    public async Task AWordFileThatLinksOutsideIsRefusedBeforeTheEngineStarts()
    {
        var rels = System.Text.Encoding.UTF8.GetBytes("""<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="r1" Type="x" Target="https://example.invalid/pic.png" TargetMode="External"/></Relationships>""");
        var source = Docx("linked.docx", ("word/_rels/document.xml.rels", rels));
        var workspace = Workspace();

        var result = await Provider().ConvertToPdfAsync(source, workspace.NewFilePath(".pdf"), workspace, _limits, Ct);

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.Contains("links to content outside", result.Error.SafeMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("example.invalid", result.Error.SafeMessage, StringComparison.Ordinal);
        Assert.False(_office.Started);
    }

    [Fact]
    public async Task AFileThatIsNotAWordFileNeverReachesTheEngine()
    {
        var notWord = _folder.Write("fake.docx", Fixtures.Png());
        var workspace = Workspace();

        var result = await Provider().ConvertToPdfAsync(notWord, workspace.NewFilePath(".pdf"), workspace, _limits, Ct);

        Assert.True(result.IsFailure);
        Assert.False(_office.Started);
    }

    [Fact]
    public async Task AnOutputPathOutsideTheJobFolderIsRefused()
    {
        var workspace = Workspace();

        var result = await Provider().ConvertToPdfAsync(Docx(), Path.Combine(_folder.Out, "x.pdf"), workspace, _limits, Ct);

        Assert.True(result.IsFailure);
        Assert.False(_office.Started);
        Assert.Empty(Directory.GetFiles(_folder.Out));
    }

    [Fact]
    public async Task APdfThatIsTooLargeIsRefused()
    {
        var workspace = Workspace();
        var pdf = workspace.NewFilePath(".pdf");

        var result = await Provider().ConvertToPdfAsync(Docx(), pdf, workspace, new ConversionLimits { MaxFileBytes = 10 }, Ct);

        Assert.True(result.IsFailure);
        Assert.False(File.Exists(pdf));
    }

    // ---- time limit and cancel: the engine and its children are gone ------------------------------------------------------

    [Fact]
    public async Task AnEngineThatTakesTooLongIsStoppedWithItsChildrenAndReportedAsATimeout()
    {
        _office.Mode = "hang";
        var workspace = Workspace();
        var clock = Stopwatch.StartNew();

        var result = await Provider(TimeSpan.FromSeconds(2)).ConvertToPdfAsync(Docx(), workspace.NewFilePath(".pdf"), workspace, _limits, Ct);

        Assert.Equal(ErrorCode.Timeout, result.Error!.Code);
        Assert.True(result.Error.Retryable);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"took {clock.Elapsed}");
        Assert.Equal(0, PingsAlive(_started));
    }

    [Fact]
    public async Task CancellingStopsTheEngineAndItsChildrenAndLeavesAFolderThatCanBeDeleted()
    {
        _office.Mode = "hang";
        var workspace = Workspace();
        using var cancel = new CancellationTokenSource();

        var running = Provider().ConvertToPdfAsync(Docx(), workspace.NewFilePath(".pdf"), workspace, _limits, cancel.Token);
        var end = DateTime.UtcNow.AddSeconds(30);
        while (!_office.Started && DateTime.UtcNow < end)
            await Task.Delay(20, Ct);
        Assert.True(_office.Started);
        await Task.Delay(500, Ct); // let the child process start
        var clock = Stopwatch.StartNew();
        await cancel.CancelAsync();
        var result = await running;

        Assert.True(result.IsCancelled);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"took {clock.Elapsed}");
        Assert.Equal(0, PingsAlive(_started));
        workspace.Dispose(); // no process still holds a file in the job's folder
        Assert.False(Directory.Exists(workspace.Directory));
    }

    [Fact]
    public async Task ACancelBeforeAnythingStartsStartsNothing()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        var workspace = Workspace();

        var result = await Provider().ConvertToPdfAsync(Docx(), workspace.NewFilePath(".pdf"), workspace, _limits, cancel.Token);

        Assert.True(result.IsCancelled);
        Assert.False(_office.Started);
    }
}
