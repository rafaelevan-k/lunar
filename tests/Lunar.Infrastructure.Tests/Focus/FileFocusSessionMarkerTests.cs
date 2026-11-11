using Lunar.Infrastructure.Focus;
using Xunit;

namespace Lunar.Infrastructure.Tests.Focus;

public sealed class FileFocusSessionMarkerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("lunar-focus-").FullName;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Folder => Path.Combine(_root, "Lunar");

    [Fact]
    public async Task NothingSavedReadsAsNone() => Assert.Null((await new FileFocusSessionMarker(Folder).ReadAsync(Ct)).Value);

    [Fact]
    public async Task ASavedTokenSurvivesARestartAndClearingRemovesIt()
    {
        Assert.True((await new FileFocusSessionMarker(Folder).SaveAsync("Microsoft.QuietHoursProfile.Unrestricted", Ct)).IsSuccess);

        var again = new FileFocusSessionMarker(Folder);
        Assert.Equal("Microsoft.QuietHoursProfile.Unrestricted", (await again.ReadAsync(Ct)).Value);

        Assert.True((await again.ClearAsync(Ct)).IsSuccess);
        Assert.Null((await again.ReadAsync(Ct)).Value);
        Assert.False(File.Exists(Path.Combine(Folder, FileFocusSessionMarker.FileName)));
    }

    [Fact]
    public async Task AnOversizedFileReadsAsNone()
    {
        Directory.CreateDirectory(Folder);
        await File.WriteAllBytesAsync(Path.Combine(Folder, FileFocusSessionMarker.FileName), new byte[100_000], Ct);

        Assert.Null((await new FileFocusSessionMarker(Folder).ReadAsync(Ct)).Value);
    }

    [Fact]
    public async Task ClearingWhenThereIsNoneIsFine() => Assert.True((await new FileFocusSessionMarker(Folder).ClearAsync(Ct)).IsSuccess);
}
