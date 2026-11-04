using Lunar.Application.Abstractions.Conversion;
using Lunar.Application.Conversion.Models;
using Lunar.Conversion.Planning;
using Lunar.Conversion.Tests.Support;
using Lunar.Domain.Conversion;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Conversion.Tests.Planning;

// BE-20: from a request to a plan. Real temporary files; nothing outside the scratch folder is touched.
public sealed class ConversionPlannerTests : IDisposable
{
    private sealed class FakeRenderer(bool available) : IRendererAvailability
    {
        public int Calls { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(available);
        }
    }

    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ConversionRequest Request(FileFormat target, params string[] paths) =>
        new(paths, target, _folder.Out);

    private async Task<ConversionPlan> Plan(ConversionRequest request, bool renderer = true)
    {
        var result = await new ConversionPlanner(new FakeRenderer(renderer)).PlanAsync(request, Ct);
        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        return result.Value;
    }

    private string Png(string name = "photo.png") => _folder.Write(name, Fixtures.Png());

    private static RejectedFile OneRejection(ConversionPlan plan, RejectionReason reason)
    {
        var rejected = Assert.Single(plan.Rejected);
        Assert.Equal(reason, rejected.Reason);
        Assert.Empty(plan.Jobs);
        return rejected;
    }

    // ---- a good file -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AValidPictureIsPlannedWithItsRouteAndOutputName()
    {
        var source = Png("photo.png");

        var plan = await Plan(Request(FileFormat.Jpg, source));

        var job = Assert.Single(plan.Jobs);
        Assert.Equal(0, job.Index);
        Assert.Equal(source, job.SourcePath);
        Assert.Equal(FileFormat.Png, job.SourceFormat);
        Assert.Equal(ConversionMode.Native, job.Route.Mode);
        Assert.True(job.Route.IsLossy);
        Assert.Equal(_folder.Out, job.OutputDirectory);
        Assert.Equal("photo", job.OutputBaseName);
        Assert.Equal(".jpg", job.OutputExtension);
        Assert.Equal("photo.jpg", job.OutputFileName);
        Assert.Equal(Path.Combine(_folder.Out, "photo.jpg"), job.OutputPath);
        Assert.False(job.WasRenamed);
        Assert.False(job.ReplacesExisting);
        Assert.Empty(plan.Rejected);
        Assert.Empty(plan.Skipped);
        Assert.False(plan.IsEmpty);
    }

    [Fact]
    public async Task ThePlanRecordsWhatTheFileLookedLikeSoALaterStepCanTellIfItChanged()
    {
        var source = Png();
        var info = new FileInfo(source);

        var job = Assert.Single((await Plan(Request(FileFormat.Pdf, source))).Jobs);

        Assert.Equal(info.Length, job.SourceLength);
        Assert.Equal(info.LastWriteTimeUtc, job.SourceLastWriteUtc);
    }

    [Fact]
    public async Task ThePlanCarriesTheLimitsInForce()
    {
        var limits = new ConversionLimits { MaxPages = 7 };
        var request = Request(FileFormat.Jpg, Png()) with { Limits = limits };

        var plan = await Plan(request);

        Assert.Equal(7, plan.Limits.MaxPages);
    }

    [Theory]
    [InlineData("a.png", FileFormat.Pdf, ConversionMode.Embed)]
    [InlineData("a.jpg", FileFormat.Docx, ConversionMode.Embed)]
    [InlineData("a.jpeg", FileFormat.Png, ConversionMode.Native)]
    [InlineData("a.pdf", FileFormat.Png, ConversionMode.Rasterize)]
    [InlineData("a.docx", FileFormat.Pdf, ConversionMode.ExternalRenderer)]
    [InlineData("a.png", FileFormat.Png, ConversionMode.SameFormat)]
    [InlineData("a.jpg", FileFormat.Jpeg, ConversionMode.SameFormat)]
    public async Task EveryFormatIsPlannedOnTheRouteTheMatrixSays(string name, FileFormat target, ConversionMode mode)
    {
        var bytes = name.EndsWith("png", StringComparison.Ordinal) ? Fixtures.Png()
            : name.EndsWith("pdf", StringComparison.Ordinal) ? Fixtures.Pdf()
            : name.EndsWith("docx", StringComparison.Ordinal) ? Fixtures.Docx()
            : Fixtures.Jpeg();
        var source = _folder.Write(name, bytes);

        var plan = await Plan(Request(target, source));

        Assert.Equal(mode, Assert.Single(plan.Jobs).Route.Mode);
    }

    [Fact]
    public async Task AUpperCaseExtensionIsAccepted()
    {
        var source = _folder.Write("SCAN.PNG", Fixtures.Png());

        var job = Assert.Single((await Plan(Request(FileFormat.Jpg, source))).Jobs);

        Assert.Equal("SCAN", job.OutputBaseName);
    }

    [Fact]
    public async Task AUnicodeFileNameIsKeptInTheOutputName()
    {
        var source = _folder.Write("foto ürün 写真.png", Fixtures.Png());

        var job = Assert.Single((await Plan(Request(FileFormat.Jpg, source))).Jobs);

        Assert.Equal("foto ürün 写真", job.OutputBaseName);
    }

    [Fact]
    public async Task AVeryLongFileNameIsShortenedSoTheResultFits()
    {
        var name = new string('n', 240);
        var source = _folder.Write(name + ".png", Fixtures.Png());

        var job = Assert.Single((await Plan(Request(FileFormat.Pdf, source))).Jobs);

        Assert.True(job.OutputFileName!.Length <= 255);
        Assert.StartsWith("nnnn", job.OutputBaseName, StringComparison.Ordinal);
        Assert.True(job.OutputPath!.Length <= new ConversionLimits().MaxPathChars);
    }

    [Fact]
    public async Task PagedOutputsKeepRoomForThePageNumber()
    {
        var name = new string('p', 240);
        var source = _folder.Write(name + ".pdf", Fixtures.Pdf());

        var job = Assert.Single((await Plan(Request(FileFormat.Png, source))).Jobs);

        Assert.True(job.Route.ProducesOneOutputPerPage);
        Assert.Null(job.OutputFileName);
        Assert.Null(job.OutputPath);
        var longest = OutputNaming.PageFileName(job.OutputBaseName, 999_999, 999_999, job.OutputExtension);
        Assert.True(longest.Length <= 255, $"A page name would be {longest.Length} characters.");
    }

    // ---- many files --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AMixedBatchIsPlannedFileByFileAndEveryFileAppearsOnce()
    {
        var good1 = Png("one.png");
        var bad = _folder.Write("two.png", Fixtures.Png(endChunk: false));
        var good2 = _folder.Write("three.jpg", Fixtures.Jpeg());
        var unsupported = _folder.Write("four.gif", [1, 2, 3]);
        var missing = Path.Combine(_folder.In, "five.png");

        var plan = await Plan(Request(FileFormat.Pdf, good1, bad, good2, unsupported, missing));

        Assert.Equal([0, 2], plan.Jobs.Select(j => j.Index));
        Assert.Equal([(1, RejectionReason.Corrupt), (3, RejectionReason.UnsupportedType), (4, RejectionReason.NotAFile)],
            plan.Rejected.Select(r => (r.Index, r.Reason)));
        Assert.Equal(["two.png", "four.gif", "five.png"], plan.Rejected.Select(r => r.FileName));
        Assert.Equal(5, plan.Jobs.Count + plan.Skipped.Count + plan.Rejected.Count);
    }

    [Fact]
    public async Task TheSameFileTwiceIsPlannedOnceAndTheRepeatIsSaidToBeARepeat()
    {
        var source = Png();
        var again = source.Replace('\\', '/').ToUpperInvariant();

        var plan = await Plan(Request(FileFormat.Jpg, source, source, again));

        Assert.Single(plan.Jobs);
        Assert.Equal([RejectionReason.Duplicate, RejectionReason.Duplicate], plan.Rejected.Select(r => r.Reason));
    }

    // ---- refusals ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("photo.gif")]
    [InlineData("notes.txt")]
    [InlineData("setup.exe")]
    [InlineData("archive.png.exe")]
    [InlineData("noextension")]
    [InlineData("document.doc")]
    public async Task AnUnsupportedTypeIsRefusedWithTheListOfSupportedOnes(string name)
    {
        var source = _folder.Write(name, Fixtures.Png());

        var rejected = OneRejection(await Plan(Request(FileFormat.Jpg, source)), RejectionReason.UnsupportedType);

        Assert.Equal(ErrorCode.InvalidInput, rejected.Error.Code);
        Assert.Contains("PNG, JPG, JPEG, DOCX and PDF", rejected.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APictureWithTheWrongExtensionIsRefusedAndSaysWhatItReallyIs()
    {
        var source = _folder.Write("actually-png.jpg", Fixtures.Png());

        var rejected = OneRejection(await Plan(Request(FileFormat.Pdf, source)), RejectionReason.SignatureMismatch);

        Assert.Contains("PNG", rejected.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APdfToDocxIsRefusedAsNotAvailableInThisVersion()
    {
        var source = _folder.Write("report.pdf", Fixtures.Pdf());

        var rejected = OneRejection(await Plan(Request(FileFormat.Docx, source)), RejectionReason.RouteUnsupported);

        Assert.Equal(ErrorCode.Unsupported, rejected.Error.Code);
        Assert.Contains("isn't available", rejected.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARouteNeedsNoFileReadingToBeRefused()
    {
        // The file does not even have to be valid: an impossible route is refused first.
        var source = _folder.Write("report.pdf", [1, 2, 3]);

        OneRejection(await Plan(Request(FileFormat.Docx, source)), RejectionReason.RouteUnsupported);
    }

    [Fact]
    public async Task AWordFileIsRefusedWhenNoRendererIsInstalledAndNothingIsDownloaded()
    {
        var source = _folder.Write("letter.docx", Fixtures.Docx());

        var rejected = OneRejection(await Plan(Request(FileFormat.Pdf, source), renderer: false), RejectionReason.RendererMissing);

        Assert.Equal(ErrorCode.DependencyMissing, rejected.Error.Code);
        Assert.Contains("LibreOffice", rejected.Error.SafeMessage, StringComparison.Ordinal);
        Assert.Contains("never installs or downloads", rejected.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWordFileIsPlannedWhenARendererIsAvailableAndSaysSo()
    {
        var source = _folder.Write("letter.docx", Fixtures.Docx());

        var plan = await Plan(Request(FileFormat.Pdf, source), renderer: true);

        Assert.True(plan.RequiresExternalRenderer);
        Assert.Equal(ConversionFidelity.LayoutDependent, Assert.Single(plan.Jobs).Route.Fidelity);
    }

    [Fact]
    public async Task TheRendererIsAskedOnceForABatchAndNeverWhenNoRouteNeedsIt()
    {
        var renderer = new FakeRenderer(available: true);
        var planner = new ConversionPlanner(renderer);
        var docs = Enumerable.Range(0, 3).Select(i => _folder.Write($"d{i}.docx", Fixtures.Docx())).ToArray();

        Assert.True((await planner.PlanAsync(Request(FileFormat.Pdf, docs), Ct)).IsSuccess);
        Assert.Equal(1, renderer.Calls);

        Assert.True((await planner.PlanAsync(Request(FileFormat.Jpg, Png("x.png")), Ct)).IsSuccess);
        Assert.Equal(1, renderer.Calls);
    }

    [Fact]
    public async Task APictureIsStillPlannedWhenARendererIsMissingForOtherFiles()
    {
        var picture = Png();
        var word = _folder.Write("letter.docx", Fixtures.Docx());

        var plan = await Plan(Request(FileFormat.Pdf, picture, word), renderer: false);

        Assert.Equal(0, Assert.Single(plan.Jobs).Index);
        Assert.Equal(RejectionReason.RendererMissing, Assert.Single(plan.Rejected).Reason);
    }

    [Fact]
    public async Task AFolderListedAsAFileIsRefused()
    {
        var dir = Path.Combine(_folder.In, "stuff.png");
        Directory.CreateDirectory(dir);

        OneRejection(await Plan(Request(FileFormat.Jpg, dir)), RejectionReason.NotAFile);
        OneRejection(await Plan(Request(FileFormat.Jpg, dir + Path.DirectorySeparatorChar)), RejectionReason.NotAFile);
    }

    [Theory]
    [InlineData(@"photo.png", RejectionReason.UnsafePath)]
    [InlineData(@"\\server\share\photo.png", RejectionReason.NetworkPath)]
    [InlineData(@"\\?\C:\photo.png", RejectionReason.UnsafePath)]
    [InlineData(@"C:\a\..\photo.png", RejectionReason.UnsafePath)]
    [InlineData(@"C:\a\photo.png:stream", RejectionReason.UnsafePath)]
    [InlineData(@"C:\a\CON.png", RejectionReason.UnsafePath)]
    [InlineData("", RejectionReason.UnsafePath)]
    public async Task UnsafePathsAreRefusedBeforeAnythingIsOpened(string path, RejectionReason reason)
    {
        var plan = await Plan(Request(FileFormat.Jpg, path));

        OneRejection(plan, reason);
    }

    [Fact]
    public async Task APathLongerThanTheLimitIsRefused()
    {
        var path = @"C:\" + string.Join('\\', Enumerable.Repeat(new string('d', 50), 25)) + @"\f.png";

        OneRejection(await Plan(Request(FileFormat.Jpg, path)), RejectionReason.PathTooLong);
    }

    [Fact]
    public async Task NoRefusalContainsAPathOrTheFileName()
    {
        var secret = _folder.Write("secret-plans.png", Fixtures.Png(endChunk: false));
        var paths = new[]
        {
            secret,
            _folder.Write("secret-gif.gif", [1]),
            Path.Combine(_folder.In, "secret-missing.png"),
            @"C:\secret\..\x.png",
            _folder.Write("secret-doc.pdf", Fixtures.Pdf())
        };

        var plan = await Plan(Request(FileFormat.Docx, paths));

        Assert.Equal(5, plan.Rejected.Count);
        foreach (var rejected in plan.Rejected)
        {
            Assert.DoesNotContain("secret", rejected.Error.SafeMessage, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(_folder.Root, rejected.Error.SafeMessage, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ARefusalDoesNotStopTheOtherFiles()
    {
        var plan = await Plan(Request(FileFormat.Pdf, Path.Combine(_folder.In, "missing.png"), Png("ok.png")));

        Assert.Equal(1, Assert.Single(plan.Jobs).Index);
        Assert.Equal(0, Assert.Single(plan.Rejected).Index);
    }

    // ---- collisions --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ATakenOutputNameGetsACounterByDefaultAndTheExistingFileStays()
    {
        var source = Png("photo.png");
        var existing = _folder.Write("photo.jpg", [9, 9, 9], _folder.Out);

        var job = Assert.Single((await Plan(Request(FileFormat.Jpg, source))).Jobs);

        Assert.Equal("photo (2)", job.OutputBaseName);
        Assert.True(job.WasRenamed);
        Assert.False(job.ReplacesExisting);
        Assert.Equal([9, 9, 9], File.ReadAllBytes(existing));
    }

    [Fact]
    public async Task TheCounterSkipsEveryNameAlreadyTaken()
    {
        var source = Png("photo.png");
        _folder.Write("photo.jpg", [1], _folder.Out);
        _folder.Write("photo (2).jpg", [1], _folder.Out);
        _folder.Write("photo (3).jpg", [1], _folder.Out);

        var job = Assert.Single((await Plan(Request(FileFormat.Jpg, source))).Jobs);

        Assert.Equal("photo (4)", job.OutputBaseName);
    }

    [Fact]
    public async Task AFolderWithTheOutputNameIsAlsoATakenName()
    {
        var source = Png("photo.png");
        Directory.CreateDirectory(Path.Combine(_folder.Out, "photo.jpg"));

        Assert.Equal("photo (2)", Assert.Single((await Plan(Request(FileFormat.Jpg, source))).Jobs).OutputBaseName);
    }

    [Fact]
    public async Task OverwriteReplacesTheExistingOutputAndSaysSo()
    {
        var source = Png("photo.png");
        _folder.Write("photo.jpg", [1], _folder.Out);
        var request = Request(FileFormat.Jpg, source) with { Collision = CollisionPolicy.Overwrite };

        var job = Assert.Single((await Plan(request)).Jobs);

        Assert.Equal("photo", job.OutputBaseName);
        Assert.True(job.ReplacesExisting);
        Assert.False(job.WasRenamed);
    }

    [Fact]
    public async Task SkipLeavesAnExistingOutputAloneAndReportsTheSkip()
    {
        var source = Png("photo.png");
        _folder.Write("photo.jpg", [1], _folder.Out);
        var request = Request(FileFormat.Jpg, source) with { Collision = CollisionPolicy.Skip };

        var plan = await Plan(request);

        Assert.Empty(plan.Jobs);
        Assert.Empty(plan.Rejected);
        var skipped = Assert.Single(plan.Skipped);
        Assert.Equal("photo.png", skipped.FileName);
        Assert.Equal(0, skipped.Index);
    }

    [Fact]
    public async Task SkipStillConvertsFilesWhoseOutputIsFree()
    {
        var a = Png("a.png");
        var b = Png("b.png");
        _folder.Write("a.jpg", [1], _folder.Out);
        var request = Request(FileFormat.Jpg, a, b) with { Collision = CollisionPolicy.Skip };

        var plan = await Plan(request);

        Assert.Equal("b", Assert.Single(plan.Jobs).OutputBaseName);
        Assert.Equal(0, Assert.Single(plan.Skipped).Index);
    }

    [Fact]
    public async Task AnOutputThatWouldBeTheInputItselfIsNeverOverwritten()
    {
        // PNG to PNG into the folder the picture is already in.
        var source = Png("photo.png");
        var request = new ConversionRequest([source], FileFormat.Png, _folder.In, CollisionPolicy.Overwrite);

        var rejected = OneRejection(await Plan(request), RejectionReason.OutputWouldReplaceOriginal);

        Assert.Contains("original", rejected.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSameConversionIntoTheInputFolderIsRenamedUnderTheDefaultPolicy()
    {
        var source = Png("photo.png");
        var request = new ConversionRequest([source], FileFormat.Png, _folder.In);

        var job = Assert.Single((await Plan(request)).Jobs);

        Assert.Equal("photo (2)", job.OutputBaseName);
        Assert.NotEqual(source, job.OutputPath);
    }

    [Fact]
    public async Task SkipTreatsAnOutputThatWouldBeTheInputAsTaken()
    {
        var source = Png("photo.png");
        var request = new ConversionRequest([source], FileFormat.Png, _folder.In, CollisionPolicy.Skip);

        var plan = await Plan(request);

        Assert.Single(plan.Skipped);
        Assert.Empty(plan.Jobs);
    }

    [Fact]
    public async Task AnOutputThatWouldBeAnotherInputOfTheSameRequestIsNotOverwrittenEither()
    {
        var a = Png("photo.png");
        var b = _folder.Write("photo.jpg", Fixtures.Jpeg());
        var request = new ConversionRequest([a, b], FileFormat.Jpg, _folder.In, CollisionPolicy.Overwrite);

        var plan = await Plan(request);

        // photo.png -> photo.jpg would replace the second input; photo.jpg -> photo.jpg is the input itself.
        Assert.Equal([RejectionReason.OutputWouldReplaceOriginal, RejectionReason.OutputWouldReplaceOriginal], plan.Rejected.Select(r => r.Reason));
    }

    [Theory]
    [InlineData(CollisionPolicy.Rename)]
    [InlineData(CollisionPolicy.Overwrite)]
    [InlineData(CollisionPolicy.Skip)]
    public async Task TwoInputsWithTheSameOutputNameNeverOverwriteEachOther(CollisionPolicy policy)
    {
        var a = _folder.Write("scan.png", Fixtures.Png());
        var b = _folder.Write("scan.jpg", Fixtures.Jpeg());
        var request = Request(FileFormat.Pdf, a, b) with { Collision = policy };

        var plan = await Plan(request);

        Assert.Equal(["scan", "scan (2)"], plan.Jobs.Select(j => j.OutputBaseName));
        Assert.Equal(1, plan.Jobs.Count(j => j.WasRenamed));
        Assert.Equal(2, plan.Jobs.Select(j => j.OutputPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task OutputNamesDifferingOnlyInCaseAreTheSameName()
    {
        var a = _folder.Write("Scan.png", Fixtures.Png());
        var b = _folder.Write("scan.jpg", Fixtures.Jpeg());

        var plan = await Plan(Request(FileFormat.Pdf, a, b));

        Assert.Equal(2, plan.Jobs.Select(j => j.OutputPath!.ToUpperInvariant()).Distinct().Count());
    }

    [Fact]
    public async Task AMoveAsideNameThatAlreadyExistsIsNotOverwrittenEvenWhenOverwriteIsChosen()
    {
        var a = _folder.Write("scan.png", Fixtures.Png());
        var b = _folder.Write("scan.jpg", Fixtures.Jpeg());
        _folder.Write("scan (2).pdf", [7], _folder.Out);
        var request = Request(FileFormat.Pdf, a, b) with { Collision = CollisionPolicy.Overwrite };

        var plan = await Plan(request);

        Assert.Equal(["scan", "scan (3)"], plan.Jobs.Select(j => j.OutputBaseName));
        Assert.All(plan.Jobs.Skip(1), j => Assert.False(j.ReplacesExisting));
    }

    // ---- one output per page -----------------------------------------------------------------------------------------

    [Fact]
    public async Task PageOutputsRenameWhenAnyPageFileOfThatNameExists()
    {
        var source = _folder.Write("doc.pdf", Fixtures.Pdf(pages: 3));
        _folder.Write("doc_page_001.png", [1], _folder.Out);

        var job = Assert.Single((await Plan(Request(FileFormat.Png, source))).Jobs);

        Assert.Equal("doc (2)", job.OutputBaseName);
        Assert.True(job.WasRenamed);
    }

    [Fact]
    public async Task PageOutputsOverwriteAnExistingSetWhenAsked()
    {
        var source = _folder.Write("doc.pdf", Fixtures.Pdf());
        _folder.Write("doc_page_002.png", [1], _folder.Out);
        var request = Request(FileFormat.Png, source) with { Collision = CollisionPolicy.Overwrite };

        var job = Assert.Single((await Plan(request)).Jobs);

        Assert.Equal("doc", job.OutputBaseName);
        Assert.True(job.ReplacesExisting);
    }

    [Fact]
    public async Task PageOutputsOfOtherFormatsDoNotCollide()
    {
        var source = _folder.Write("doc.pdf", Fixtures.Pdf());
        _folder.Write("doc_page_001.jpg", [1], _folder.Out);

        Assert.Equal("doc", Assert.Single((await Plan(Request(FileFormat.Png, source))).Jobs).OutputBaseName);
    }

    [Fact]
    public async Task TwoPagedSourcesWithTheSameNameGetDifferentPageSets()
    {
        var a = _folder.Write("doc.pdf", Fixtures.Pdf());
        var other = Path.Combine(_folder.In, "more");
        Directory.CreateDirectory(other);
        var b = _folder.Write("doc.pdf", Fixtures.Pdf(), other);

        var plan = await Plan(Request(FileFormat.Png, a, b));

        Assert.Equal(["doc", "doc (2)"], plan.Jobs.Select(j => j.OutputBaseName));
    }

    [Fact]
    public async Task AWordToPicturesRouteAlsoGivesOneNameForAllPages()
    {
        var source = _folder.Write("letter.docx", Fixtures.Docx());

        var job = Assert.Single((await Plan(Request(FileFormat.Jpeg, source))).Jobs);

        Assert.True(job.Route.ProducesOneOutputPerPage);
        Assert.Equal(".jpeg", job.OutputExtension);
    }

    // ---- the request as a whole --------------------------------------------------------------------------------------

    [Fact]
    public async Task NothingChosenIsARequestFailureNotAnEmptyPlan()
    {
        var result = await new ConversionPlanner(new FakeRenderer(true)).PlanAsync(Request(FileFormat.Jpg), Ct);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.Contains("at least one file", result.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TooManyFilesIsARequestFailureThatSaysTheLimit()
    {
        var limits = new ConversionLimits { MaxFilesPerRequest = 2 };
        var request = Request(FileFormat.Jpg, Png("a.png"), Png("b.png"), Png("c.png")) with { Limits = limits };

        var result = await new ConversionPlanner(new FakeRenderer(true)).PlanAsync(request, Ct);

        Assert.True(result.IsFailure);
        Assert.Contains("at most 2", result.Error!.SafeMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("out")]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\?\C:\out")]
    [InlineData(@"C:\a\..\out")]
    [InlineData(@"C:\this\folder\does\not\exist-lunar-test")]
    public async Task AnUnusableOutputFolderIsARequestFailure(string directory)
    {
        var request = Request(FileFormat.Jpg, Png()) with { OutputDirectory = directory };

        var result = await new ConversionPlanner(new FakeRenderer(true)).PlanAsync(request, Ct);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.DoesNotContain(directory.Length > 3 ? directory : "\u0001", result.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileAsTheOutputFolderIsARequestFailure()
    {
        var file = _folder.Write("not-a-folder", [1]);
        var request = Request(FileFormat.Jpg, Png()) with { OutputDirectory = file };

        Assert.True((await new ConversionPlanner(new FakeRenderer(true)).PlanAsync(request, Ct)).IsFailure);
    }

    [Fact]
    public async Task AnOutputFolderThatIsAJunctionToAnotherPlaceIsARequestFailure()
    {
        var junction = Path.Combine(_folder.Root, "link");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(junction);
        start.ArgumentList.Add(_folder.Out);
        using (var process = System.Diagnostics.Process.Start(start)!)
        {
            await process.WaitForExitAsync(Ct);
            Assert.True(process.ExitCode == 0 && Directory.Exists(junction), "The test could not create a junction on this machine.");
        }

        var request = Request(FileFormat.Jpg, Png()) with { OutputDirectory = junction };

        var result = await new ConversionPlanner(new FakeRenderer(true)).PlanAsync(request, Ct);

        Assert.True(result.IsFailure);
        Assert.Contains("link", result.Error!.SafeMessage, StringComparison.Ordinal);
        Directory.Delete(junction); // removes the link only, never what it points to
    }

    [Fact]
    public async Task AnOutputFolderWithATrailingSeparatorWorks()
    {
        var request = Request(FileFormat.Jpg, Png()) with { OutputDirectory = _folder.Out + Path.DirectorySeparatorChar };

        var job = Assert.Single((await Plan(request)).Jobs);

        Assert.Equal(_folder.Out, job.OutputDirectory);
    }

    [Fact]
    public async Task InvalidLimitsAndUnknownEnumValuesAreRequestFailures()
    {
        var planner = new ConversionPlanner(new FakeRenderer(true));
        var png = Png();

        Assert.True((await planner.PlanAsync(Request(FileFormat.Jpg, png) with { Limits = new ConversionLimits { MaxPages = 0 } }, Ct)).IsFailure);
        Assert.True((await planner.PlanAsync(Request(FileFormat.Jpg, png) with { Limits = new ConversionLimits { MaxPathChars = 5 } }, Ct)).IsFailure);
        Assert.True((await planner.PlanAsync(Request((FileFormat)99, png), Ct)).IsFailure);
        Assert.True((await planner.PlanAsync(Request(FileFormat.Jpg, png) with { Collision = (CollisionPolicy)99 }, Ct)).IsFailure);
    }

    [Fact]
    public async Task AnOutputFolderPathThatIsTooLongForAnyNameIsRefusedPerFile()
    {
        var limits = new ConversionLimits { MaxPathChars = _folder.Out.Length + 12 };
        var request = Request(FileFormat.Pdf, Png()) with { Limits = limits };

        var plan = await Plan(request);

        OneRejection(plan, RejectionReason.PathTooLong);
    }

    // ---- cancellation and side effects -------------------------------------------------------------------------------

    [Fact]
    public async Task ACancelledCallReturnsCancelledAndPlansNothing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await new ConversionPlanner(new FakeRenderer(true)).PlanAsync(Request(FileFormat.Jpg, Png()), cts.Token);

        Assert.True(result.IsCancelled);
    }

    [Fact]
    public async Task PlanningChangesNothingOnDisk()
    {
        var files = new[]
        {
            Png("a.png"),
            _folder.Write("b.pdf", Fixtures.Pdf(pages: 5)),
            _folder.Write("c.docx", Fixtures.Docx()),
            _folder.Write("bad.png", Fixtures.Png(endChunk: false))
        };
        _folder.Write("a.jpg", [1], _folder.Out);
        var before = _folder.Snapshot();

        foreach (var policy in Enum.GetValues<CollisionPolicy>())
        {
            await Plan(Request(FileFormat.Png, files) with { Collision = policy });
            await Plan(Request(FileFormat.Jpg, files) with { Collision = policy });
            await Plan(Request(FileFormat.Pdf, files) with { Collision = policy });
        }

        Assert.Equal(before, _folder.Snapshot());
    }

    [Fact]
    public async Task APlanDoesNotCreateTheOutputFolderEither()
    {
        var missing = Path.Combine(_folder.Root, "later");
        var request = Request(FileFormat.Jpg, Png()) with { OutputDirectory = missing };

        Assert.True((await new ConversionPlanner(new FakeRenderer(true)).PlanAsync(request, Ct)).IsFailure);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void ThePlannerNeedsARenderer()
    {
        Assert.Throws<ArgumentNullException>(() => new ConversionPlanner(null!));
    }

    [Fact]
    public async Task ThePlanTimeComesFromTheClockGiven()
    {
        var time = new FixedTime(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
        var planner = new ConversionPlanner(new FakeRenderer(true), time);

        var result = await planner.PlanAsync(Request(FileFormat.Jpg, Png()), Ct);

        Assert.Equal(time.GetUtcNow(), result.Value.PlannedAt);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
