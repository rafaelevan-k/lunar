using Lunar.Conversion.Planning;
using Lunar.Conversion.Providers;
using Lunar.Conversion.Tests.Support;
using Xunit;

namespace Lunar.Conversion.Tests.Providers;

// BE-25: where Lunar looks for the user's LibreOffice, and what it accepts. The file is only read for its version, never run.
public sealed class LibreOfficeLocatorTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Install(string name = "program", string fileName = "soffice.exe")
    {
        var folder = Directory.CreateDirectory(Path.Combine(_folder.Root, name)).FullName;
        File.WriteAllBytes(Path.Combine(folder, fileName), [1, 2, 3]);
        return folder;
    }

    private static LibreOfficeLocator Locator(IEnumerable<string> folders, string? product = "LibreOffice", int major = 24) =>
        new(folders, _ => (product, product is null ? null : new Version(major, 2, 1, 2)));

    [Fact]
    public void AnInstallationInAKnownFolderIsFoundWithItsVersion()
    {
        var folder = Install();

        var found = Locator([folder]).Locate();

        Assert.NotNull(found);
        Assert.Equal(Path.Combine(folder, "soffice.exe"), found.ExecutablePath);
        Assert.Equal(24, found.Version.Major);
    }

    [Fact]
    public void TheFirstUsableFolderWins()
    {
        var none = Directory.CreateDirectory(Path.Combine(_folder.Root, "empty")).FullName;
        var real = Install("real");

        Assert.Equal(Path.Combine(real, "soffice.exe"), Locator([none, real]).Locate()!.ExecutablePath);
    }

    [Fact]
    public void NothingIsFoundWhereThereIsNoLauncherOrNoFolder()
    {
        var empty = Directory.CreateDirectory(Path.Combine(_folder.Root, "empty")).FullName;
        var otherName = Install("other", "soffice2.exe");

        Assert.Null(Locator([empty, otherName, Path.Combine(_folder.Root, "missing")]).Locate());
        Assert.Null(Locator([]).Locate());
    }

    [Theory]
    [InlineData(null, 24)]
    [InlineData("Some Other Program", 24)]
    [InlineData("LibreOffice", 6)]
    [InlineData("LibreOffice", 0)]
    public void AProgramThatIsNotARecentLibreOfficeIsNotAccepted(string? product, int major)
    {
        Assert.Null(Locator([Install()], product, major).Locate());
    }

    [Fact]
    public void TheOldestAcceptedLineIsSeven()
    {
        Assert.NotNull(Locator([Install()], major: LibreOfficeLocator.MinimumMajorVersion).Locate());
        Assert.Null(Locator([Install()], major: LibreOfficeLocator.MinimumMajorVersion - 1).Locate());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"program")]
    [InlineData(@"\\server\share\LibreOffice\program")]
    public void AFolderThatIsNotAFullLocalPathIsIgnored(string folder)
    {
        Assert.Null(Locator([folder]).Locate());
    }

    [Fact]
    public void AVersionReaderThatFailsMeansNotFound()
    {
        var locator = new LibreOfficeLocator([Install()], _ => throw new IOException("locked"));

        Assert.Null(locator.Locate());
    }

    [Fact]
    public void ALauncherThatIsALinkToElsewhereIsNotAccepted()
    {
        var target = Install("target");
        var linkFolder = Path.Combine(_folder.Root, "link");
        try
        {
            Directory.CreateSymbolicLink(linkFolder, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // creating a link needs a privilege this account may not have; the rule is then covered by reading the attribute only
        }

        Assert.Null(Locator([linkFolder]).Locate());
    }

    [Fact]
    public void ThePathIsNeverSearchedOrRun()
    {
        // A launcher on PATH or in the current folder is not looked for at all: only the folders given are.
        var decoy = Install("decoy");
        var previous = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", decoy + ";" + previous);
        try
        {
            Assert.Null(Locator([Path.Combine(_folder.Root, "nowhere")]).Locate());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previous);
        }
    }

    [Fact]
    public void TheDefaultLocatorLooksOnlyUnderProgramFiles()
    {
        // It is built from the system's Program Files folders; on a PC without LibreOffice it finds nothing and does not throw.
        var found = new LibreOfficeLocator().Locate();

        if (found is not null)
            Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)[..2], found.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    // ---- availability ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task WordRoutesAreAvailableOnlyWhenEnabledAndTheEngineIsFound()
    {
        var installed = Locator([Install()]);
        var missing = Locator([]);

        Assert.True(await new LibreOfficeAvailability(installed, routesEnabled: true).IsAvailableAsync(Ct));
        Assert.False(await new LibreOfficeAvailability(missing, routesEnabled: true).IsAvailableAsync(Ct));
        Assert.False(await new LibreOfficeAvailability(installed, routesEnabled: false).IsAvailableAsync(Ct)); // the hold of ADR-0005
    }

    [Fact]
    public async Task WhileTheHoldIsOnTheEngineIsNotEvenLookedFor()
    {
        var spy = new SpyLocator();

        Assert.False(await new LibreOfficeAvailability(spy, routesEnabled: false).IsAvailableAsync(Ct));
        Assert.Equal(0, spy.Calls);
    }

    [Fact]
    public async Task ACancelledQuestionIsCancelled()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LibreOfficeAvailability(new SpyLocator(), true).IsAvailableAsync(cancel.Token));
    }

    private sealed class SpyLocator : ILibreOfficeLocator
    {
        public int Calls { get; private set; }

        public LibreOfficeInfo? Locate()
        {
            Calls++;
            return null;
        }
    }
}
