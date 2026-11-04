using System.ComponentModel;
using System.Diagnostics;
using Lunar.Domain.Shared;
using Lunar.Windows.Environment;
using Xunit;

namespace Lunar.Windows.IntegrationTests.Environment;

// FE-22: the part of "show in folder" that decides what Explorer is asked. Starting a real Explorer window is a manual check (NOT_RUN here).
public sealed class WindowsOutputRevealerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lunar-reveal-" + Guid.NewGuid().ToString("N"));
    private readonly List<ProcessStartInfo> _started = [];

    public WindowsOutputRevealerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WindowsOutputRevealer Revealer(Action<ProcessStartInfo>? start = null) => new(start ?? _started.Add);

    [Fact]
    public async Task AFileIsShownSelectedInItsFolderByExplorerFoundUnderTheWindowsFolder()
    {
        var file = Path.Combine(_root, "my result (2).png");
        File.WriteAllBytes(file, [1]);

        var result = await Revealer().RevealAsync(file, Ct);

        Assert.True(result.IsSuccess);
        var info = Assert.Single(_started);
        Assert.Equal(Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows), "explorer.exe"), info.FileName);
        Assert.Equal($"/select,\"{file}\"", info.Arguments);
        Assert.False(info.UseShellExecute);
    }

    [Fact]
    public async Task AFolderIsOpened()
    {
        var result = await Revealer().RevealAsync(_root, Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal($"\"{_root}\"", Assert.Single(_started).Arguments);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"relative\file.png")]
    [InlineData(@"\\server\share\file.png")]
    [InlineData("//server/share/file.png")]
    [InlineData(@"\\?\C:\file.png")]
    [InlineData(@"\\.\C:\file.png")]
    [InlineData("C:\\a\" /e,C:\\Windows")]
    [InlineData(@"C:\a\*.png")]
    [InlineData(@"C:\a\file?.png")]
    [InlineData(@"C:file.png")]
    public async Task APathThatIsNotAFullLocalPathIsRefusedAndNothingIsStarted(string path)
    {
        var result = await Revealer().RevealAsync(path, Ct);

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.Empty(_started);
        Assert.DoesNotContain("server", result.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APathThatIsGoneIsRefusedWithoutNamingIt()
    {
        var gone = Path.Combine(_root, "secret-name.png");

        var result = await Revealer().RevealAsync(gone, Ct);

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.DoesNotContain("secret-name", result.Error.SafeMessage, StringComparison.Ordinal);
        Assert.Empty(_started);
    }

    [Fact]
    public async Task AFailureToStartExplorerIsAFailedResultWithoutThePath()
    {
        var file = Path.Combine(_root, "secret-name.png");
        File.WriteAllBytes(file, [1]);

        var result = await Revealer(_ => throw new Win32Exception(2, file)).RevealAsync(file, Ct);

        Assert.Equal(ErrorCode.IoFailure, result.Error!.Code);
        Assert.True(result.Error.Retryable);
        Assert.DoesNotContain("secret-name", result.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACancelledRequestStartsNothing()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        var result = await Revealer().RevealAsync(_root, cancel.Token);

        Assert.True(result.IsCancelled);
        Assert.Empty(_started);
    }

    [Fact]
    public async Task TheFileItselfIsNeverOpenedOrChanged()
    {
        var file = Path.Combine(_root, "keep.png");
        File.WriteAllBytes(file, [1, 2, 3]);
        var stamp = File.GetLastWriteTimeUtc(file);

        await Revealer().RevealAsync(file, Ct);

        Assert.Equal([1, 2, 3], File.ReadAllBytes(file));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(file));
        Assert.DoesNotContain(_started, i => i.FileName.EndsWith("keep.png", StringComparison.OrdinalIgnoreCase));
    }
}
