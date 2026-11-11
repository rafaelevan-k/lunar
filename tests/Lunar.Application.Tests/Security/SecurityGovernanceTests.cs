using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Lunar.Architecture.Tests;
using Xunit;

namespace Lunar.Application.Tests.Security;

// BE-28: the security and dependency rules of docs/SECURITY.md that can be checked by reading the repository, so a later change that
// breaks one fails the build instead of being found in a review. Each test names the rule it guards. They read source text only.
public sealed partial class SecurityGovernanceTests
{
    private static string Root => LayerBoundaryTests.RepositoryRoot;

    private static string Rel(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');

    // Production source: the canonical projects, never the retained discovery code, build output or caches.
    private static IEnumerable<string> ProductionFiles(params string[] extensions) =>
        Directory.EnumerateFiles(Path.Combine(Root, "src"), "*", SearchOption.AllDirectories)
            .Where(path => extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Select(path => (Path: path, Relative: Rel(path)))
            .Where(file => !file.Relative.Contains("/obj/", StringComparison.Ordinal)
                && !file.Relative.Contains("/bin/", StringComparison.Ordinal)
                && !file.Relative.Contains("/Spike/", StringComparison.Ordinal)
                && !file.Relative.Contains(".Spike", StringComparison.Ordinal))
            .Select(file => file.Path);

    private static IEnumerable<string> FilesContaining(IEnumerable<string> files, params string[] needles) =>
        files.Where(path =>
            {
                var text = File.ReadAllText(path);
                return needles.Any(needle => text.Contains(needle, StringComparison.Ordinal));
            })
            .Select(Rel)
            .Order(StringComparer.Ordinal);

    // ---- no network except the disclosed addresses (Usage, ADR-0014; the update check, ADR-0025) ----------------------

    [Fact]
    public void OnlyTheUsageAndUpdateAdaptersTouchTheNetwork()
    {
        var offenders = FilesContaining(
            ProductionFiles(".cs"),
            "System.Net.Http", "HttpClient", "WebClient", "WebRequest", "System.Net.Sockets", "TcpClient", "UdpClient", "ClientWebSocket", "System.Net.Mail", "SmtpClient")
            .ToList();

        Assert.Equal(
            [
                "src/Lunar.Infrastructure/Updates/GitHubReleaseFeed.cs", // ADR-0025: the latest release, once a day, switch in Settings
                "src/Lunar.Infrastructure/Usage/AnthropicUsageSource.cs",
                "src/Lunar.Windows/Updates/MsiUpdateInstaller.cs"        // ADR-0025: the installer, only after Install now
            ],
            offenders);
    }

    [Fact]
    public void TheUpdateCodeOnlyKnowsGitHubOverHttps()
    {
        foreach (var file in new[] { Path.Combine("src", "Lunar.Infrastructure", "Updates", "GitHubReleaseFeed.cs"), Path.Combine("src", "Lunar.Windows", "Updates", "MsiUpdateInstaller.cs") })
        {
            var text = File.ReadAllText(Path.Combine(Root, file));
            var addresses = AbsoluteUrl().Matches(text).Select(m => m.Value.TrimEnd('"', ')', ';', ',')).Distinct().ToList();
            Assert.All(addresses, address => Assert.StartsWith("https://api.github.com/repos/rafaelevan-k/lunar/", address, StringComparison.Ordinal));
            Assert.DoesNotContain("http://", text, StringComparison.Ordinal);
        }

        var hosts = File.ReadAllText(Path.Combine(Root, "src", "Lunar.Windows", "Updates", "MsiUpdateInstaller.cs"));
        foreach (var host in new[] { "\"github.com\"", "\"objects.githubusercontent.com\"", "\"release-assets.githubusercontent.com\"" })
            Assert.Contains(host, hosts, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUsageAdapterOnlyKnowsOneAddressOverHttps()
    {
        var text = File.ReadAllText(Path.Combine(Root, "src", "Lunar.Infrastructure", "Usage", "AnthropicUsageSource.cs"));
        var addresses = AbsoluteUrl().Matches(text).Select(m => m.Value.TrimEnd('"', ')', ';', ',')).Distinct().Order().ToList();

        Assert.NotEmpty(addresses);
        Assert.All(addresses, address => Assert.StartsWith("https://api.anthropic.com/", address, StringComparison.Ordinal));
        Assert.DoesNotContain("http://", text, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheUpdateFeedChecksForUpdatesAndNothingElseDownloads()
    {
        var offenders = FilesContaining(
            ProductionFiles(".cs", ".xaml"),
            "DownloadFile", "DownloadString", "DownloadData", "GetByteArrayAsync", "releases/latest", "api.github.com", "ApplicationDeployment", "Squirrel", "AutoUpdater", "CheckForUpdate")
            .ToList();

        // ADR-0025: the one place that names GitHub's release address. The installer downloads through its own checked loop, not these helpers.
        Assert.Equal(["src/Lunar.Infrastructure/Updates/GitHubReleaseFeed.cs"], offenders);
    }

    // ---- other programs are started from exactly three places --------------------------------------------------------

    [Fact]
    public void OnlyKnownCodeStartsOtherPrograms()
    {
        var offenders = FilesContaining(ProductionFiles(".cs"), "Process.Start", "new ProcessStartInfo", "ProcessStartInfo(").ToList();

        Assert.Equal(
            [
                "src/Lunar.Conversion/Providers/LibreOfficeRendererProvider.cs", // the optional layout engine, argument list, no shell
                "src/Lunar.Windows/Environment/WindowsOutputRevealer.cs",        // Explorer, to show a result
                "src/Lunar.Windows/Environment/WindowsSettingsLauncher.cs",      // a fixed ms-settings address
                "src/Lunar.Windows/Updates/MsiUpdateInstaller.cs"                // ADR-0025: System32 msiexec on a checked MSI, argument list, no shell
            ],
            offenders);
    }

    [Fact]
    public void NoProgramIsStartedThroughAShellOrAStringOfArguments()
    {
        var text = string.Join('\n', ProductionFiles(".cs").Select(File.ReadAllText));

        Assert.DoesNotContain("cmd.exe", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("powershell", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".Arguments =", text, StringComparison.Ordinal); // only ArgumentList, so nothing is re-parsed
    }

    [Fact]
    public void TheLibreOfficeProviderNeverUsesAShellAndKillsTheWholeProcessTree()
    {
        var text = File.ReadAllText(Path.Combine(Root, "src", "Lunar.Conversion", "Providers", "LibreOfficeRendererProvider.cs"));

        Assert.Contains("UseShellExecute = false", text, StringComparison.Ordinal);
        Assert.Contains("ArgumentList.Add", text, StringComparison.Ordinal);
        Assert.Contains("Kill(entireProcessTree: true)", text, StringComparison.Ordinal);
        Assert.Contains("--headless", text, StringComparison.Ordinal);
    }

    // ---- never elevated ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheAppRunsAsTheUserAndNeverElevatedOrWithUiAccess()
    {
        var manifest = XDocument.Load(Path.Combine(Root, "src", "Lunar.Desktop", "app.manifest"));
        var level = manifest.Descendants().Single(e => e.Name.LocalName == "requestedExecutionLevel");

        Assert.Equal("asInvoker", (string?)level.Attribute("level"));
        Assert.Equal("false", (string?)level.Attribute("uiAccess"));
    }

    // ---- no secrets in the repository --------------------------------------------------------------------------------

    [Fact]
    public void NoTokenKeyOrPrivateKeyIsWrittenIntoSourceScriptsOrWorkflows()
    {
        var files = ProductionFiles(".cs", ".xaml", ".json", ".props", ".csproj", ".manifest")
            .Where(path => !path.EndsWith("packages.lock.json", StringComparison.OrdinalIgnoreCase))
            .Concat(Directory.EnumerateFiles(Path.Combine(Root, "scripts"), "*.ps1"))
            .Concat(Directory.EnumerateFiles(Path.Combine(Root, ".github"), "*", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(Root, "*.props"));

        var offenders = files
            .Where(path => SecretShapes().IsMatch(File.ReadAllText(path)))
            .Select(Rel)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(offenders);
    }

    // ---- CI is least privilege ---------------------------------------------------------------------------------------

    [Fact]
    public void TheWorkflowIsReadOnlyUsesNoSecretsAndNeverRunsPullRequestCodeWithWriteAccess()
    {
        var text = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "ci.yml"));

        Assert.Matches(@"(?m)^permissions:\s*\r?\n\s+contents: read\s*$", text);
        Assert.DoesNotContain("pull_request_target", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secrets.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("contents: write", text, StringComparison.Ordinal);
        Assert.DoesNotContain("workflow_run", text, StringComparison.Ordinal);
    }

    // ---- dependencies: nothing enters without a recorded review -------------------------------------------------------

    // Every package any project resolves, with the scope it is allowed in. A package, a version or a scope that is not here fails the
    // test below, so adding or updating a dependency means editing this table in the same change, with its license (checked from the
    // package's own metadata) and, for a product package, the review in THIRD_PARTY_NOTICES.md and docs/DEPENDENCY_REVIEW.md.
    // Scope: Product = ships with the app; Test = test projects only.
    private static readonly (string Id, string Version, string License, string Scope)[] Approved =
    [
        ("DocumentFormat.OpenXml", "3.5.1", "MIT", "Product"),
        ("DocumentFormat.OpenXml.Framework", "3.5.1", "MIT", "Product"),
        ("Microsoft.Extensions.DependencyInjection", "10.0.13", "MIT", "Product"),
        ("Microsoft.Extensions.DependencyInjection.Abstractions", "10.0.13", "MIT", "Product"),
        ("Serilog", "4.3.1", "Apache-2.0", "Product"),
        ("Serilog.Sinks.File", "7.0.0", "Apache-2.0", "Product"),
        ("SkiaSharp", "4.153.1", "MIT", "Product"),
        ("SkiaSharp.NativeAssets.Win32", "4.153.1", "MIT", "Product"), // the native aggregate has mixed terms: shipping it is on hold (ADR-0003)
        ("SkiaSharp.NativeAssets.macOS", "4.153.1", "MIT", "Product"), // restored with SkiaSharp, never part of a Windows payload
        ("System.IO.Packaging", "10.0.2", "MIT", "Product"),
        ("Microsoft.NET.Test.Sdk", "18.10.0", "MIT", "Test"),
        ("Microsoft.CodeCoverage", "18.10.0", "MIT", "Test"),
        ("Microsoft.TestPlatform.ObjectModel", "18.10.0", "MIT", "Test"),
        ("Microsoft.TestPlatform.TestHost", "18.10.0", "MIT", "Test"),
        ("Microsoft.Bcl.AsyncInterfaces", "6.0.0", "MIT", "Test"),
        ("Microsoft.Win32.Registry", "5.0.0", "MIT", "Test"),
        ("System.Security.AccessControl", "6.0.1", "MIT", "Test"),
        ("xunit.analyzers", "2.2.0", "Apache-2.0", "Test"),
        ("xunit.runner.visualstudio", "4.0.1", "Apache-2.0", "Test"),
        ("xunit.v3.assert", "4.0.2", "Apache-2.0", "Test"),
        ("xunit.v3.common", "4.0.2", "Apache-2.0", "Test"),
        ("xunit.v3.core.mtp-off", "4.0.2", "Apache-2.0", "Test"),
        ("xunit.v3.extensibility.core", "4.0.2", "Apache-2.0", "Test"),
        ("xunit.v3.mtp-off", "4.0.2", "Apache-2.0", "Test"),
        ("xunit.v3.runner.common", "4.0.2", "Apache-2.0", "Test"),
        ("xunit.v3.runner.inproc.console", "4.0.2", "Apache-2.0", "Test")
    ];

    private static readonly string[] AllowedLicenses = ["MIT", "Apache-2.0"];

    private static IEnumerable<(string Project, string Id, string Version)> Resolved(bool productOnly)
    {
        var lockFiles = Directory.EnumerateFiles(Root, "*packages.lock.json", SearchOption.AllDirectories)
            .Select(path => (Path: path, Relative: Rel(path)))
            .Where(file => !file.Relative.Contains("/obj/", StringComparison.Ordinal)
                && !file.Relative.Contains("/bin/", StringComparison.Ordinal)
                && !file.Relative.Contains("artifacts/", StringComparison.Ordinal)
                && !file.Relative.StartsWith(".tools/", StringComparison.Ordinal)
                && !file.Relative.Contains("Spike", StringComparison.Ordinal))
            .Where(file => !productOnly || file.Relative.StartsWith("src/", StringComparison.Ordinal));

        foreach (var file in lockFiles)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file.Path));
            foreach (var framework in document.RootElement.GetProperty("dependencies").EnumerateObject())
            {
                foreach (var package in framework.Value.EnumerateObject())
                {
                    var type = package.Value.GetProperty("type").GetString();
                    if (type == "Project")
                        continue;
                    yield return (file.Relative, package.Name, package.Value.GetProperty("resolved").GetString()!);
                }
            }
        }
    }

    [Fact]
    public void EveryResolvedPackageIsOnTheApprovedListWithItsExactVersion()
    {
        var approved = Approved.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        var unknown = Resolved(productOnly: false)
            .Where(r => !approved.TryGetValue(r.Id, out var a) || a.Version != r.Version)
            .Select(r => $"{r.Project}: {r.Id} {r.Version}")
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(unknown);
    }

    [Fact]
    public void ProductProjectsOnlyResolveProductPackagesAndTestOnlyPackagesNeverReachThem()
    {
        var approved = Approved.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);

        var leaked = Resolved(productOnly: true)
            .Where(r => approved.TryGetValue(r.Id, out var a) && a.Scope != "Product")
            .Select(r => $"{r.Project}: {r.Id}")
            .Distinct()
            .ToList();

        Assert.Empty(leaked);
    }

    [Fact]
    public void ApprovedPackagesCarryOnlyTheLicensesTheProjectAccepted()
    {
        Assert.All(Approved, a => Assert.Contains(a.License, AllowedLicenses));
        Assert.Equal(Approved.Length, Approved.Select(a => a.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void EveryApprovedPackageIsStillUsedSoTheListDoesNotGrowStale()
    {
        var used = Resolved(productOnly: false).Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var stale = Approved.Where(a => !used.Contains(a.Id)).Select(a => a.Id).ToList();

        Assert.Empty(stale);
    }

    [Fact]
    public void CentralVersionsAreExactPinsNeverRangesOrFloating()
    {
        var props = XDocument.Load(Path.Combine(Root, "Directory.Packages.props"));
        var versions = props.Descendants().Where(e => e.Name.LocalName == "PackageVersion").ToList();

        Assert.NotEmpty(versions);
        Assert.All(versions, e => Assert.Matches(@"^\d+\.\d+\.\d+$", (string?)e.Attribute("Version") ?? string.Empty));
        var approved = Approved.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        Assert.All(versions, e => Assert.Equal(approved[(string)e.Attribute("Include")!].Version, (string)e.Attribute("Version")!));
    }

    [Fact]
    public void RestoreIsLockedAuditedAndRunByTheBuildScriptThatCiUses()
    {
        var build = File.ReadAllText(Path.Combine(Root, "Directory.Build.props"));
        var script = File.ReadAllText(Path.Combine(Root, "scripts", "build.ps1"));
        var ci = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "ci.yml"));

        Assert.Contains("<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>", build, StringComparison.Ordinal);
        Assert.Contains("<NuGetAuditMode>all</NuGetAuditMode>", build, StringComparison.Ordinal);
        Assert.Contains("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", build, StringComparison.Ordinal); // an advisory warning fails the build
        Assert.Contains("--locked-mode", script, StringComparison.Ordinal);
        Assert.Contains("./scripts/build.ps1", ci, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSdkIsPinnedWithoutRollForward()
    {
        using var global = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "global.json")));
        var sdk = global.RootElement.GetProperty("sdk");

        Assert.Matches(@"^\d+\.\d+\.\d+$", sdk.GetProperty("version").GetString()!);
        Assert.True(sdk.TryGetProperty("rollForward", out var roll));
        Assert.Contains(roll.GetString(), new[] { "disable", "latestPatch" });
    }

    [GeneratedRegex(@"https?://[A-Za-z0-9._~:/?#\[\]@!$&'()*+,;=%-]+")]
    private static partial Regex AbsoluteUrl();

    // Shapes of real secrets: an Anthropic-style token, a PEM private key, a GitHub token, an AWS key id, a long hex or base64 value after a
    // password-like name. Prefixes alone ("sk-ant-" in the token rules) are fine; a whole token is not.
    [GeneratedRegex(@"sk-ant-[A-Za-z0-9_\-]{20,}|-----BEGIN [A-Z ]*PRIVATE KEY-----|gh[pousr]_[A-Za-z0-9]{30,}|AKIA[0-9A-Z]{16}|(?i)(password|secret|apikey|api_key)\s*[:=]\s*""[A-Za-z0-9+/=_\-]{16,}""")]
    private static partial Regex SecretShapes();
}
