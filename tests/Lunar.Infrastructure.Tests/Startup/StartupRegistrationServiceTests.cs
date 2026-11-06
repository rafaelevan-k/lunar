using Lunar.Domain.Shared;
using Lunar.Infrastructure.Startup;
using Microsoft.Win32;
using Xunit;

namespace Lunar.Infrastructure.Tests.Startup;

public sealed class StartupRegistrationServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "Lunar.Tests." + Guid.NewGuid().ToString("N"));
    private readonly string _exe;

    public StartupRegistrationServiceTests()
    {
        Directory.CreateDirectory(_folder);
        _exe = Path.Combine(_folder, "Lunar App.exe");
        File.WriteAllText(_exe, "x");
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private sealed class FakeStore : IRunKeyStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Exception? Throw { get; set; }

        public string? Read(string name) => Throw is null ? Values.GetValueOrDefault(name) : throw Throw;

        public void Write(string name, string command)
        {
            if (Throw is not null) throw Throw;
            Values[name] = command;
        }

        public void Delete(string name)
        {
            if (Throw is not null) throw Throw;
            Values.Remove(name);
        }
    }

    [Fact]
    public void NothingIsRegisteredUntilEnabled()
    {
        var store = new FakeStore();
        var service = new StartupRegistrationService(_exe, store);

        Assert.False(service.IsEnabled().Value);
        Assert.Empty(store.Values);
    }

    [Fact]
    public void EnableWritesOneQuotedPathAndIsIdempotent()
    {
        var store = new FakeStore();
        var service = new StartupRegistrationService(_exe, store);

        Assert.True(service.Enable().IsSuccess);
        Assert.True(service.Enable().IsSuccess);

        Assert.Equal("\"" + _exe + "\"", Assert.Single(store.Values).Value);
        Assert.True(service.IsEnabled().Value);
    }

    [Fact]
    public void StaleRegistrationToAnotherPathIsNotEnabledAndEnableRepairsIt()
    {
        var store = new FakeStore();
        store.Values[StartupRegistrationService.ValueName] = "\"C:\\Gone\\Lunar.exe\"";
        var service = new StartupRegistrationService(_exe, store);

        Assert.False(service.IsEnabled().Value);
        service.Enable();
        Assert.True(service.IsEnabled().Value);
    }

    [Fact]
    public void DisableRemovesOnlyLunarAndSucceedsWhenAbsent()
    {
        var store = new FakeStore();
        store.Values["Other"] = "\"C:\\Other.exe\"";
        var service = new StartupRegistrationService(_exe, store);

        Assert.True(service.Disable().IsSuccess);
        service.Enable();
        Assert.True(service.Disable().IsSuccess);

        Assert.Equal(["Other"], store.Values.Keys);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Lunar.exe")]
    [InlineData(@"\\server\share\Lunar.exe")]
    [InlineData("C:\\Missing\\Lunar.exe")]
    [InlineData("C:\\Dir\\\"quoted\".exe")]
    public void UnsafeOrMissingPathsAreRejectedAndNothingIsWritten(string path)
    {
        var store = new FakeStore();
        var service = new StartupRegistrationService(path, store);

        var result = service.Enable();

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.Empty(store.Values);
    }

    [Fact]
    public void NonExecutableFileIsRejected()
    {
        var text = Path.Combine(_folder, "note.txt");
        File.WriteAllText(text, "x");

        Assert.Equal(ErrorCode.InvalidInput,
            new StartupRegistrationService(text, new FakeStore()).Enable().Error!.Code);
    }

    [Theory]
    [InlineData(typeof(UnauthorizedAccessException), ErrorCode.AccessDenied)]
    [InlineData(typeof(System.Security.SecurityException), ErrorCode.AccessDenied)]
    [InlineData(typeof(IOException), ErrorCode.IoFailure)]
    [InlineData(typeof(PlatformNotSupportedException), ErrorCode.Unsupported)]
    public void StoreFailuresBecomeTypedResultsWithoutPathsInMessages(Type exception, ErrorCode expected)
    {
        var store = new FakeStore { Throw = (Exception)Activator.CreateInstance(exception, _exe)! };
        var service = new StartupRegistrationService(_exe, store);

        foreach (var error in new[] { service.Enable().Error!, service.Disable().Error!, service.IsEnabled().Error! })
        {
            Assert.Equal(expected, error.Code);
            Assert.DoesNotContain(_folder, error.SafeMessage);
        }
    }

    // Real registry, but under a throwaway HKCU key so the user's actual Run list is never touched.
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void RealRegistryRoundTripUsesHkcuAndCleansUp()
    {
        Assert.True(OperatingSystem.IsWindows());
        var subKey = @"Software\Lunar.Tests\" + Guid.NewGuid().ToString("N") + @"\Run";
        try
        {
            var service = new StartupRegistrationService(_exe, new RegistryRunKeyStore(subKey));

            Assert.False(service.IsEnabled().Value);
            Assert.True(service.Enable().IsSuccess);
            Assert.True(service.IsEnabled().Value);

            using (var key = Registry.CurrentUser.OpenSubKey(subKey))
            {
                Assert.Equal(RegistryValueKind.String, key!.GetValueKind(StartupRegistrationService.ValueName));
                Assert.Equal("\"" + _exe + "\"", key.GetValue(StartupRegistrationService.ValueName));
            }

            Assert.True(service.Disable().IsSuccess);
            Assert.False(service.IsEnabled().Value);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Lunar.Tests", throwOnMissingSubKey: false);
        }
    }
}
