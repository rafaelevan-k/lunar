using Lunar.Application.Abstractions.System;
using Lunar.Domain.Shared;
using Lunar.Infrastructure.Startup;
using Xunit;

namespace Lunar.Infrastructure.Tests.Startup;

// ADR-0018. Runs in a scratch folder named Lunar; the user's real %LocalAppData%\Lunar is never touched.
public sealed class UninstallCleanupTests : IDisposable
{
    private readonly string _parent = Path.Combine(Path.GetTempPath(), "Lunar.Tests." + Guid.NewGuid().ToString("N"));
    private readonly string _data;

    public UninstallCleanupTests()
    {
        _data = Path.Combine(_parent, "Lunar");
        Directory.CreateDirectory(Path.Combine(_data, "Logs"));
        File.WriteAllText(Path.Combine(_data, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(_data, "Logs", "lunar.log"), "x");
    }

    public void Dispose()
    {
        if (Directory.Exists(_parent))
            Directory.Delete(_parent, recursive: true);
    }

    private sealed class FakeStartup : IStartupRegistration
    {
        public int Disabled { get; private set; }

        public Result? DisableResult { get; init; }

        public Result<bool> IsEnabled() => Result.Success(false);

        public Result Enable() => Result.Success();

        public Result Disable()
        {
            Disabled++;
            return DisableResult ?? Result.Success();
        }
    }

    [Fact]
    public void KeepingDataRemovesOnlyTheStartupEntry()
    {
        var startup = new FakeStartup();

        Assert.True(new UninstallCleanup(startup, _data).Run(deleteUserData: false).IsSuccess);

        Assert.Equal(1, startup.Disabled);
        Assert.True(File.Exists(Path.Combine(_data, "settings.json")));
    }

    [Fact]
    public void DeletingDataRemovesTheWholeLunarFolderAndNothingBesideIt()
    {
        var neighbour = Path.Combine(_parent, "Other");
        Directory.CreateDirectory(neighbour);
        var startup = new FakeStartup();

        Assert.True(new UninstallCleanup(startup, _data).Run(deleteUserData: true).IsSuccess);

        Assert.Equal(1, startup.Disabled);
        Assert.False(Directory.Exists(_data));
        Assert.True(Directory.Exists(neighbour));
    }

    [Fact]
    public void AMissingFolderIsFine()
    {
        Directory.Delete(_data, recursive: true);

        Assert.True(new UninstallCleanup(new FakeStartup(), _data).Run(deleteUserData: true).IsSuccess);
    }

    [Fact]
    public void AStartupFailureIsReportedButTheDataIsStillDeleted()
    {
        var startup = new FakeStartup { DisableResult = Result.Failure(new OperationError(ErrorCode.AccessDenied, "no")) };

        var result = new UninstallCleanup(startup, _data).Run(deleteUserData: true);

        Assert.Equal(ErrorCode.AccessDenied, result.Error!.Code);
        Assert.False(Directory.Exists(_data));
    }

    [Fact]
    public void ALockedFileIsATypedFailureWithoutAPath()
    {
        using var locked = new FileStream(Path.Combine(_data, "Logs", "lunar.log"), FileMode.Open, FileAccess.Read, FileShare.None);

        var result = new UninstallCleanup(new FakeStartup(), _data).Run(deleteUserData: true);

        Assert.Equal(ErrorCode.IoFailure, result.Error!.Code);
        Assert.DoesNotContain(_parent, result.Error.SafeMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Lunar")]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Users\Someone\AppData\Local")]
    [InlineData(@"C:\Users\Someone\AppData\Local\Programs")]
    public void OnlyAnAbsoluteFolderNamedLunarIsAccepted(string folder) =>
        Assert.Throws<ArgumentException>(() => new UninstallCleanup(new FakeStartup(), folder));
}
