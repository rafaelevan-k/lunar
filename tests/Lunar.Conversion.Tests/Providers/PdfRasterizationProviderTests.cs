using System.Buffers.Binary;
using Lunar.Application.Conversion.Models;
using Lunar.Conversion.Output;
using Lunar.Conversion.Pipeline;
using Lunar.Conversion.Providers;
using Lunar.Conversion.Tests.Support;
using Lunar.Conversion.Validation;
using Lunar.Domain.Conversion;
using Lunar.Domain.Shared;
using SkiaSharp;
using Xunit;

namespace Lunar.Conversion.Tests.Providers;

// BE-24: what Lunar asks of the PDF engine and what it does with the answers. The engine is a fake that draws real PNGs of exactly the
// requested size; the operating system's renderer is tested in the Windows project.
public sealed class PdfRasterizationProviderTests : IDisposable
{
    private sealed class Progress : IProgress<JobProgress>
    {
        public List<JobProgress> Reports { get; } = [];

        public void Report(JobProgress value) => Reports.Add(value);
    }

    private readonly TempFolder _folder = new();
    private readonly FakePdfRasterizer _engine = new();
    private readonly TempWorkspace _workspace;
    private readonly ConversionLimits _limits = new();

    public PdfRasterizationProviderTests() => _workspace = TempWorkspace.Create(Path.Combine(_folder.Root, "work"));

    public void Dispose()
    {
        _workspace.Dispose();
        _folder.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Pdf(int pages = 3, string name = "doc.pdf") => _folder.Write(name, Fixtures.Pdf(pages));

    private PdfRasterizationProvider Provider() => new(_engine);

    private Task<Result<PdfRasterizationResult>> Run(
        string source,
        FileFormat target = FileFormat.Png,
        PdfRasterizationOptions? options = null,
        string? password = null,
        ImageEncodingOptions? image = null,
        ConversionLimits? limits = null,
        IProgress<JobProgress>? progress = null,
        CancellationToken? ct = null) =>
        Provider().RasterizeAsync(source, password, target, options ?? new PdfRasterizationOptions(), image ?? new ImageEncodingOptions(), _workspace, limits ?? _limits, progress, ct ?? Ct);

    private static void AssertOk(Result<PdfRasterizationResult> result) => Assert.True(result.IsSuccess, result.Error?.SafeMessage);

    private string[] WorkspaceFiles() => [.. Directory.GetFiles(_workspace.Directory).Where(f => !f.EndsWith("journal.txt", StringComparison.Ordinal))];

    private static (uint X, uint Y, byte Unit)? Density(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var position = 8;
        while (position + 12 <= bytes.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position));
            if (System.Text.Encoding.ASCII.GetString(bytes, position + 4, 4) == "pHYs")
                return (BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position + 8)), BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position + 12)), bytes[position + 16]);
            position += 12 + length;
        }

        return null;
    }

    // ---- pages and sizes ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryPageBecomesOnePngWithItsOwnNumberAndPicture()
    {
        var result = await Run(Pdf(3));

        AssertOk(result);
        Assert.Equal(3, result.Value.DocumentPageCount);
        Assert.Equal([1, 2, 3], result.Value.Pages.Select(p => p.PageNumber));
        Assert.Equal(3, result.Value.StagedFiles.Distinct().Count());
        Assert.All(result.Value.StagedFiles, f =>
        {
            Assert.True(_workspace.Owns(f));
            Assert.EndsWith(".png", f, StringComparison.Ordinal);
            Assert.Null(SourceFileValidator.Inspect(f, FileFormat.Png, _limits).Failure);
        });
        for (var i = 0; i < 3; i++)
        {
            var c = PictureFixtures.PixelAt(result.Value.StagedFiles[i], 5, 5);
            var e = FakePdfRasterizer.ColorOfPage(i + 1);
            Assert.Equal((e.R, e.G, e.B), (c.Red, c.Green, c.Blue));
        }
    }

    [Theory]
    [InlineData(72, 100, 150)]
    [InlineData(150, 208, 313)]
    [InlineData(300, 417, 625)]
    public async Task ThePictureSizeFollowsThePageSizeInPointsAndTheResolution(int dpi, int width, int height)
    {
        _engine.Pages = 1;
        var result = await Run(Pdf(1), options: new PdfRasterizationOptions { Dpi = dpi });

        AssertOk(result);
        Assert.Equal((width, height), (result.Value.Pages[0].WidthPixels, result.Value.Pages[0].HeightPixels));
        Assert.Equal((0, width, height), _engine.Renders.Single());
    }

    [Fact]
    public async Task ADefaultRunIs150PixelsPerInch()
    {
        Assert.Equal(150, new PdfRasterizationOptions().Dpi);
        var result = await Run(Pdf(1));

        AssertOk(result);
        Assert.Equal(208, result.Value.Pages[0].WidthPixels);
    }

    [Fact]
    public async Task ChosenPagesKeepTheirOwnNumbers()
    {
        _engine.Pages = 9;
        PageSelection.TryParse("2, 5-6, 9", out var pages, out _);

        var result = await Run(Pdf(9), options: new PdfRasterizationOptions { Pages = pages });

        AssertOk(result);
        Assert.Equal(9, result.Value.DocumentPageCount);
        Assert.Equal([2, 5, 6, 9], result.Value.Pages.Select(p => p.PageNumber));
        Assert.Equal([1, 4, 5, 8], _engine.Renders.Select(r => r.Index)); // zero based inside the engine
        Assert.Equal(4, result.Value.StagedFiles.Count);
        Assert.Equal(new PageNumbering([2, 5, 6, 9], 9), result.Value.Numbering, new NumberingComparer());
    }

    private sealed class NumberingComparer : IEqualityComparer<PageNumbering>
    {
        public bool Equals(PageNumbering? x, PageNumbering? y) =>
            x is not null && y is not null && x.DocumentPageCount == y.DocumentPageCount && x.PageNumbers.SequenceEqual(y.PageNumbers);

        public int GetHashCode(PageNumbering obj) => obj.DocumentPageCount;
    }

    [Fact]
    public async Task APageTheDocumentDoesNotHaveIsRefusedWithoutDrawingAnything()
    {
        PageSelection.TryParse("2, 7", out var pages, out _);

        var result = await Run(Pdf(3), options: new PdfRasterizationOptions { Pages = pages });

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.Contains("3 pages", result.Error.SafeMessage, StringComparison.Ordinal);
        Assert.Empty(_engine.Renders);
        Assert.Empty(WorkspaceFiles());
    }

    [Fact]
    public async Task AnImpossibleResolutionIsRefused()
    {
        foreach (var dpi in new[] { 0, 96, 600, -150 })
        {
            var result = await Run(Pdf(1, $"d{dpi}.pdf"), options: new PdfRasterizationOptions { Dpi = dpi });

            Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
            Assert.Contains("72, 150 or 300", result.Error.SafeMessage, StringComparison.Ordinal);
        }

        Assert.Equal(0, _engine.OpenCalls);
    }

    // ---- limits ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task MorePagesThanTheLimitAreRefusedWithTheNumbersInTheMessage()
    {
        _engine.Pages = 201;

        var result = await Run(Pdf(201));

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.Contains("201 pages", result.Error.SafeMessage, StringComparison.Ordinal);
        Assert.Contains("200", result.Error.SafeMessage, StringComparison.Ordinal);
        Assert.Empty(_engine.Renders);
    }

    [Fact]
    public async Task TheLimitIsOnThePagesConvertedSoFewPagesOfAHugePdfAreFine()
    {
        _engine.Pages = 5000;
        PageSelection.TryParse("4000-4002", out var pages, out _);

        var result = await Run(Pdf(3, "big.pdf"), options: new PdfRasterizationOptions { Pages = pages });

        AssertOk(result);
        Assert.Equal([4000, 4001, 4002], result.Value.Pages.Select(p => p.PageNumber));
        Assert.Equal(5000, result.Value.DocumentPageCount);
    }

    [Fact]
    public async Task ExactlyTheLimitPassesAndTheLimitIsConfigurable()
    {
        _engine.Pages = 4;
        var source = Pdf(4);

        AssertOk(await Run(source, limits: _limits with { MaxPages = 4 }));

        var refused = await Run(source, limits: _limits with { MaxPages = 3 });
        Assert.True(refused.IsFailure);
    }

    [Fact]
    public async Task APageThatWouldBeTooLargeStopsTheJobBeforeAnyPageIsDrawn()
    {
        // pages 1 and 2 are fine; page 3 is a poster (3 x 4 metres) that would be about 340 million pixels at 300 pixels per inch
        _engine.PageSize = i => i == 2 ? (8500, 11500) : (100, 150);

        var result = await Run(Pdf(3), options: new PdfRasterizationOptions { Dpi = 300 });

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.Contains("Page 3", result.Error.SafeMessage, StringComparison.Ordinal);
        Assert.Contains("lower resolution", result.Error.SafeMessage, StringComparison.Ordinal);
        Assert.Empty(_engine.Renders);       // nothing was drawn, not even the pages before it
        Assert.Empty(WorkspaceFiles());
    }

    [Fact]
    public async Task TheSamePosterPassesAtALowerResolution()
    {
        _engine.PageSize = _ => (2000, 2500); // 28 by 35 inches: 5 megapixels at 72 pixels per inch

        var result = await Run(Pdf(1), options: new PdfRasterizationOptions { Dpi = 72 });

        AssertOk(result);
        Assert.Equal((2000, 2500), (result.Value.Pages[0].WidthPixels, result.Value.Pages[0].HeightPixels));
    }

    [Fact]
    public async Task APageExactlyAtThePixelLimitPassesAndOnePixelOverIsRefused()
    {
        _engine.PageSize = _ => (1000, 1000);
        var source = Pdf(1);

        AssertOk(await Run(source, options: new PdfRasterizationOptions { Dpi = 72 }, limits: _limits with { MaxImagePixels = 1_000_000 }));
        var over = await Run(source, options: new PdfRasterizationOptions { Dpi = 72 }, limits: _limits with { MaxImagePixels = 999_999 });

        Assert.True(over.IsFailure);
    }

    [Fact]
    public async Task APageSizeOfZeroStillAsksForAtLeastOnePixel()
    {
        _engine.PageSize = _ => (0, 0);

        var result = await Run(Pdf(1));

        // The fake reports 0 x 0 points; the provider never asks for a picture smaller than one pixel.
        AssertOk(result);
        Assert.Equal((1, 1), (result.Value.Pages[0].WidthPixels, result.Value.Pages[0].HeightPixels));
    }

    [Fact]
    public async Task ADocumentWithoutPagesIsRefused()
    {
        _engine.Pages = 0;

        var result = await Run(Pdf(1));

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
    }

    // ---- results -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task APngPageCarriesTheChosenResolutionAndKeepsItsPixels()
    {
        foreach (var dpi in new[] { 72, 150, 300 })
        {
            var result = await Run(Pdf(1, $"p{dpi}.pdf"), options: new PdfRasterizationOptions { Dpi = dpi });

            AssertOk(result);
            var path = result.Value.StagedFiles[0];
            var density = Density(path);
            Assert.NotNull(density);
            Assert.Equal(PngDensity.PixelsPerMetre(dpi), density.Value.X);
            Assert.Equal(density.Value.X, density.Value.Y);
            Assert.Equal(1, density.Value.Unit);
            Assert.Null(SourceFileValidator.Inspect(path, FileFormat.Png, _limits).Failure);
            var c = PictureFixtures.PixelAt(path, 3, 3);
            var e = FakePdfRasterizer.ColorOfPage(1);
            Assert.Equal((e.R, e.G, e.B), (c.Red, c.Green, c.Blue));
        }
    }

    [Theory]
    [InlineData(FileFormat.Jpg)]
    [InlineData(FileFormat.Jpeg)]
    public async Task AJpegTargetGivesValidJpegPagesOfTheSameSizeAndRemovesTheTemporaryPng(FileFormat target)
    {
        _engine.Pages = 2;
        _engine.PageSize = _ => (200, 100);

        var result = await Run(Pdf(2), target, new PdfRasterizationOptions { Dpi = 72 }, image: new ImageEncodingOptions { JpegQuality = 100 });

        AssertOk(result);
        Assert.All(result.Value.StagedFiles, f =>
        {
            Assert.EndsWith(".jpg", f, StringComparison.Ordinal);
            Assert.Null(SourceFileValidator.Inspect(f, target, _limits).Failure);
        });
        Assert.Equal((200, 100), (result.Value.Pages[0].WidthPixels, result.Value.Pages[0].HeightPixels));
        Assert.Equal(2, WorkspaceFiles().Length); // the two JPEGs only
        var c = PictureFixtures.PixelAt(result.Value.StagedFiles[1], 50, 50);
        var e = FakePdfRasterizer.ColorOfPage(2);
        Assert.True(Math.Abs(c.Red - e.R) <= 8 && Math.Abs(c.Green - e.G) <= 8 && Math.Abs(c.Blue - e.B) <= 8, $"got {c}");
    }

    [Fact]
    public async Task JpegQualityIsUsed()
    {
        _engine.PageSize = _ => (200, 200);
        _engine.RenderOverride = (_, w, h, destination, _) =>
        {
            var random = new Random(5);
            destination.Write(PictureFixtures.Rgb(w, h, (_, _) => ((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256))));
            return Task.FromResult(Result.Success(new RenderedPage(w, h)));
        };
        var source = Pdf(1);

        var low = await Run(source, FileFormat.Jpg, new PdfRasterizationOptions { Dpi = 72 }, image: new ImageEncodingOptions { JpegQuality = 20 });
        var high = await Run(source, FileFormat.Jpg, new PdfRasterizationOptions { Dpi = 72 }, image: new ImageEncodingOptions { JpegQuality = 95 });

        AssertOk(low);
        AssertOk(high);
        Assert.True(new FileInfo(low.Value.StagedFiles[0]).Length < new FileInfo(high.Value.StagedFiles[0]).Length);
    }

    [Fact]
    public async Task AnImpossibleJpegQualityIsRefused()
    {
        var result = await Run(Pdf(1), FileFormat.Jpg, image: new ImageEncodingOptions { JpegQuality = 0 });

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.Contains("quality", result.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(FileFormat.Pdf)]
    [InlineData(FileFormat.Docx)]
    [InlineData((FileFormat)77)]
    public async Task OnlyPictureTargetsAreAccepted(FileFormat target)
    {
        var result = await Run(Pdf(1), target);

        Assert.Equal(ErrorCode.Unsupported, result.Error!.Code);
        Assert.Equal(0, _engine.OpenCalls);
    }

    // ---- naming together with the output step ------------------------------------------------------------------------

    [Fact]
    public async Task TheOutputStepNamesChosenPagesWithTheirOwnNumbers()
    {
        _engine.Pages = 9;
        PageSelection.TryParse("3, 5, 7", out var pages, out _);
        var source = Pdf(9);
        var result = await Run(source, options: new PdfRasterizationOptions { Pages = pages });
        AssertOk(result);
        var job = new PlannedConversion(0, source, FileFormat.Pdf, 1, DateTime.UtcNow, ConversionPolicy.Resolve(FileFormat.Pdf, FileFormat.Png),
            _folder.Out, "doc", ".png", false, false);

        var commit = await new OutputCommitter().CommitAsync(job, _workspace, result.Value.StagedFiles, result.Value.Numbering, _limits, Ct);

        Assert.True(commit.IsSuccess, commit.Error?.SafeMessage);
        Assert.Equal(["doc_page_003.png", "doc_page_005.png", "doc_page_007.png"], commit.Value.Select(Path.GetFileName));
    }

    [Fact]
    public async Task ThePageNumbersAreAsWideAsTheWholeDocumentNeeds()
    {
        _engine.Pages = 1200;
        PageSelection.TryParse("7, 1150", out var pages, out _);
        var source = Pdf(3, "long.pdf");
        var result = await Run(source, options: new PdfRasterizationOptions { Pages = pages });
        AssertOk(result);
        var job = new PlannedConversion(0, source, FileFormat.Pdf, 1, DateTime.UtcNow, ConversionPolicy.Resolve(FileFormat.Pdf, FileFormat.Png),
            _folder.Out, "long", ".png", false, false);

        var commit = await new OutputCommitter().CommitAsync(job, _workspace, result.Value.StagedFiles, result.Value.Numbering, _limits, Ct);

        Assert.Equal(["long_page_0007.png", "long_page_1150.png"], commit.Value.Select(Path.GetFileName));
    }

    // ---- passwords ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AProtectedPdfNeedsThePasswordWhichIsPassedOnAndNotKept()
    {
        _engine.RequiredPassword = "s3cret-pw";
        var source = Pdf(2);

        var without = await Run(source);
        var wrong = await Run(source, password: "nope");
        var right = await Run(source, password: "s3cret-pw");

        Assert.Equal(ErrorCode.AccessDenied, without.Error!.Code);
        Assert.Contains("protected by a password", without.Error.SafeMessage, StringComparison.Ordinal);
        Assert.Equal(ErrorCode.AccessDenied, wrong.Error!.Code);
        Assert.Contains("didn't open", wrong.Error.SafeMessage, StringComparison.Ordinal);
        AssertOk(right);
        Assert.Equal("s3cret-pw", _engine.LastPassword);
        Assert.DoesNotContain("s3cret", without.Error.SafeMessage + wrong.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePasswordIsNotPartOfTheOptionsSoLoggingThemCannotShowIt()
    {
        var names = typeof(PdfRasterizationOptions).GetProperties().Select(p => p.Name);

        Assert.DoesNotContain(names, n => n.Contains("Password", StringComparison.OrdinalIgnoreCase));
        await Task.CompletedTask;
    }

    // ---- bad PDFs and failures ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ADamagedPdfThatTheEngineRejectsIsAnInputErrorAndTheEngineErrorIsPassedOn()
    {
        _engine.OpenFailure = Result.Failure<Application.Abstractions.Conversion.IPdfDocument>(new OperationError(ErrorCode.InvalidInput, "This PDF is damaged or in a form Lunar can't read. Try getting a fresh copy of it."));

        var result = await Run(Pdf(2));

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.Contains("damaged", result.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SomethingThatIsNotAPdfNeverReachesTheEngine()
    {
        var png = _folder.Write("pretend.pdf", Fixtures.Png());
        var cut = _folder.Write("cut.pdf", Fixtures.Pdf(endMarker: false));
        var empty = _folder.Write("empty.pdf", []);

        foreach (var source in new[] { png, cut, empty })
            Assert.True((await Run(source)).IsFailure);

        Assert.Equal(0, _engine.OpenCalls);
    }

    [Fact]
    public async Task AFailureOnALaterPageRemovesEveryPictureMadeBeforeIt()
    {
        _engine.Pages = 5;
        _engine.RenderOverride = (index, w, h, destination, _) =>
            index == 3
                ? Task.FromResult(Result.Failure<RenderedPage>(new OperationError(ErrorCode.InvalidInput, "This page can't be drawn. The PDF may be damaged.")))
                : null;

        var result = await Run(Pdf(5));

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.Equal(4, _engine.Renders.Count);
        Assert.Empty(WorkspaceFiles()); // pages 1 to 3 were drawn and are gone again, and so is the half-written page 4
    }

    [Fact]
    public async Task AJpegJobThatFailsLeavesNoJpegOrPngBehind()
    {
        _engine.RenderOverride = (index, _, _, _, _) =>
            index == 2 ? Task.FromResult(Result.Failure<RenderedPage>(new OperationError(ErrorCode.Timeout, "Drawing this page took too long.", retryable: true))) : null;

        var result = await Run(Pdf(4), FileFormat.Jpg);

        Assert.Equal(ErrorCode.Timeout, result.Error!.Code);
        Assert.Empty(WorkspaceFiles());
    }

    [Fact]
    public async Task ACancelInTheMiddleStopsAndRemovesWhatWasMade()
    {
        _engine.Pages = 6;
        using var cts = new CancellationTokenSource();
        _engine.RenderOverride = (index, _, _, _, _) =>
        {
            if (index == 2)
                cts.Cancel();
            return null;
        };

        var result = await Run(Pdf(6), ct: cts.Token);

        Assert.True(result.IsCancelled);
        Assert.True(_engine.Renders.Count <= 4, "It kept drawing long after the cancel.");
        Assert.Empty(WorkspaceFiles());
    }

    [Fact]
    public async Task ACancelBeforeStartDoesNothing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await Run(Pdf(2), ct: cts.Token);

        Assert.True(result.IsCancelled);
        Assert.Equal(0, _engine.OpenCalls);
    }

    [Fact]
    public async Task AnEngineThatReturnsSomethingThatIsNotAPngIsCaught()
    {
        _engine.RenderOverride = (_, w, h, destination, _) =>
        {
            destination.Write(new byte[2048]);
            return Task.FromResult(Result.Success(new RenderedPage(w, h)));
        };

        var result = await Run(Pdf(2));

        Assert.Equal(ErrorCode.Unexpected, result.Error!.Code);
        Assert.Empty(WorkspaceFiles());
    }

    [Fact]
    public async Task AnEngineThatReportsOneSizeAndDrawsAnotherIsCaught()
    {
        _engine.RenderOverride = (_, w, h, destination, _) =>
        {
            destination.Write(PictureFixtures.Rgb(w + 5, h, (_, _) => (1, 2, 3)));
            return Task.FromResult(Result.Success(new RenderedPage(w, h)));
        };

        var result = await Run(Pdf(1));

        Assert.Equal(ErrorCode.Unexpected, result.Error!.Code);
        Assert.Empty(WorkspaceFiles());
    }

    [Fact]
    public async Task AnEngineThatThrowsBecomesAFixedMessageWithoutTheExceptionText()
    {
        _engine.RenderOverride = (_, _, _, _, _) => throw new InvalidOperationException(@"native failure at C:\secret\plans.pdf");

        var result = await Run(Pdf(2));

        Assert.Equal(ErrorCode.Unexpected, result.Error!.Code);
        Assert.DoesNotContain("secret", result.Error.SafeMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(WorkspaceFiles());
    }

    // ---- progress, files and hygiene ---------------------------------------------------------------------------------

    [Fact]
    public async Task ProgressCountsFinishedPagesOfThoseChosen()
    {
        _engine.Pages = 8;
        PageSelection.TryParse("2-4, 8", out var pages, out _);
        var progress = new Progress();

        AssertOk(await Run(Pdf(8), options: new PdfRasterizationOptions { Pages = pages }, progress: progress));

        Assert.Equal([0, 1, 2, 3, 4], progress.Reports.Select(r => r.PagesCompleted!.Value));
        Assert.All(progress.Reports, r => Assert.Equal(4, r.PagesTotal));
        Assert.All(progress.Reports, r => Assert.True(r.IsDeterminate));
    }

    [Fact]
    public async Task TheSourceIsReleasedAndNeverChanged()
    {
        var source = Pdf(2);
        var bytes = File.ReadAllBytes(source);
        var time = File.GetLastWriteTimeUtc(source);

        AssertOk(await Run(source));

        Assert.True(_engine.DocumentDisposed);
        Assert.True(_engine.StreamStillOpenWhenDisposed, "The source stream was closed before the document was disposed.");
        Assert.Equal(bytes, File.ReadAllBytes(source));
        Assert.Equal(time, File.GetLastWriteTimeUtc(source));
        var moved = source + ".moved";
        File.Move(source, moved); // fails if a handle is still open
        Assert.True(File.Exists(moved));
    }

    [Fact]
    public async Task TheDocumentIsDisposedOnFailureToo()
    {
        _engine.RenderOverride = (_, _, _, _, _) => Task.FromResult(Result.Failure<RenderedPage>(new OperationError(ErrorCode.Unexpected, "no")));

        await Run(Pdf(2));

        Assert.True(_engine.DocumentDisposed);
    }

    [Fact]
    public async Task NoMessageContainsAPathOrTheFileName()
    {
        var secret = _folder.Write("secret-plans.pdf", Fixtures.Pdf(3));
        _engine.RequiredPassword = "x";
        var failures = new[]
        {
            await Run(secret),
            await Run(_folder.Write("secret-bad.pdf", Fixtures.Pdf(endMarker: false))),
            await Run(Path.Combine(_folder.In, "secret-missing.pdf")),
            await Run(secret, FileFormat.Docx)
        };

        Assert.All(failures, f =>
        {
            Assert.True(f.IsFailure);
            Assert.DoesNotContain("secret", f.Error!.SafeMessage, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@":\", f.Error.SafeMessage, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task UnicodeAndLongSourceNamesWork()
    {
        var source = _folder.Write("фото-ürün-写真-😀-" + new string('x', 100) + ".pdf", Fixtures.Pdf(2));

        AssertOk(await Run(source));
    }

    [Fact]
    public async Task AHundredPagesAreDrawnInOrderWithNothingLeftOver()
    {
        _engine.Pages = 100;
        var progress = new Progress();

        var result = await Run(Pdf(100, "hundred.pdf"), options: new PdfRasterizationOptions { Dpi = 72 }, progress: progress);

        AssertOk(result);
        Assert.Equal(Enumerable.Range(1, 100), result.Value.Pages.Select(p => p.PageNumber));
        Assert.Equal(100, result.Value.StagedFiles.Distinct().Count());
        Assert.Equal(100, WorkspaceFiles().Length);
        Assert.Equal(101, progress.Reports.Count);
    }

    [Fact]
    public async Task TwoJobsAtOnceKeepToTheirOwnWorkspaces()
    {
        using var other = TempWorkspace.Create(Path.Combine(_folder.Root, "work2"));
        var a = Provider().RasterizeAsync(Pdf(3, "a.pdf"), null, FileFormat.Png, new PdfRasterizationOptions(), new ImageEncodingOptions(), _workspace, _limits, null, Ct);
        var b = Provider().RasterizeAsync(Pdf(3, "b.pdf"), null, FileFormat.Png, new PdfRasterizationOptions(), new ImageEncodingOptions(), other, _limits, null, Ct);

        var results = await Task.WhenAll(a, b);

        Assert.All(results, AssertOk);
        Assert.All(results[0].Value.StagedFiles, f => Assert.True(_workspace.Owns(f)));
        Assert.All(results[1].Value.StagedFiles, f => Assert.True(other.Owns(f)));
    }

    [Fact]
    public async Task TheArgumentsAreRequired()
    {
        Assert.Throws<ArgumentNullException>(() => new PdfRasterizationProvider(null!));
        var provider = Provider();
        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.RasterizeAsync(null!, null, FileFormat.Png, new PdfRasterizationOptions(), new ImageEncodingOptions(), _workspace, _limits, null, Ct));
    }
}
