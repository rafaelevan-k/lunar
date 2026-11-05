using Lunar.Application.Conversion.Models;
using Lunar.Conversion.Output;
using Lunar.Conversion.Pipeline;
using Lunar.Conversion.Tests.Support;
using Lunar.Domain.Conversion;
using Xunit;

namespace Lunar.Conversion.Tests.Output;

// BE-28: a result is never written through a link to somewhere else. An existing output name that is a symbolic link must not
// make Overwrite change the file the link points to (it could be a file the user never chose).
public sealed class OutputLinkSafetyTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly List<TempWorkspace> _workspaces = [];

    public void Dispose()
    {
        foreach (var workspace in _workspaces)
            workspace.Dispose();
        _folder.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OverwriteNeverReplacesOrWritesThroughAnExistingSymbolicLink()
    {
        var source = _folder.Write("photo.png", Fixtures.Png());
        var precious = _folder.Write("precious.jpg", [9, 9, 9, 9, 9], _folder.In);
        var preciousBefore = File.ReadAllBytes(precious);
        var linkPath = Path.Combine(_folder.Out, "photo.jpg");
        try
        {
            File.CreateSymbolicLink(linkPath, precious);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This account may not create symbolic links (needs Developer Mode or administrator rights); NOT_RUN here.");
            return;
        }

        var workspace = TempWorkspace.Create(Path.Combine(_folder.Root, "work"));
        _workspaces.Add(workspace);
        var staged = workspace.NewFilePath(".jpg");
        File.WriteAllBytes(staged, Fixtures.Jpeg());
        var job = new PlannedConversion(
            0, source, FileFormat.Png, new FileInfo(source).Length, File.GetLastWriteTimeUtc(source),
            ConversionPolicy.Resolve(FileFormat.Png, FileFormat.Jpg), _folder.Out, "photo", ".jpg", false, ReplacesExisting: true);

        var result = await new OutputCommitter().CommitAsync(job, workspace, [staged], new ConversionLimits(), Ct);

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal(preciousBefore, File.ReadAllBytes(precious)); // the file behind the link is untouched
        var final = Assert.Single(result.Value);
        Assert.Equal(Path.Combine(_folder.Out, "photo (2).jpg"), final); // the link kept its name; the result took the next free one
        Assert.NotNull(new FileInfo(linkPath).LinkTarget);
        Assert.Equal(Fixtures.Jpeg(), File.ReadAllBytes(final));
        Assert.Equal(preciousBefore, File.ReadAllBytes(precious));
    }
}
