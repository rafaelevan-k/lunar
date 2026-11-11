using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Updates;
using Lunar.Domain.Shared;
using Lunar.Domain.Updates;
using Lunar.Windows.Updates;
using Xunit;

namespace Lunar.Windows.IntegrationTests.Updates;

// BE-44, ADR-0025: the download, the SHA-256 check and the hand-off to Windows Installer, with a stand-in network and a stand-in process start.
// A real download and a real install of a test release are manual checks (BE-44 evidence).
public sealed class MsiUpdateInstallerTests : IDisposable
{
    private const string Start = "https://github.com/rafaelevan-k/lunar/releases/download/v1.0.6/Lunar.msi";
    private const string Storage = "https://release-assets.githubusercontent.com/github-production-release-asset/1/abc?sig=x";

    private static readonly byte[] Payload = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 31)).ToArray();
    private static readonly string PayloadDigest = Convert.ToHexStringLower(SHA256.HashData(Payload));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "LunarUpdateTests", Guid.NewGuid().ToString("N"));
    private readonly List<ProcessStartInfo> _started = [];
    private readonly RecordingLog _log = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static ReleaseInfo Release(string? digest = null, long? size = null, string address = Start) =>
        new(new AppVersion(1, 0, 6), new Uri(address), digest ?? PayloadDigest, size ?? Payload.Length);

    private MsiUpdateInstaller Installer(Handler handler, TimeSpan? timeout = null) =>
        new(handler, _root, _started.Add, _log, TimeProvider.System, timeout ?? TimeSpan.FromSeconds(30));

    private static HttpResponseMessage Redirect(string to) => new(HttpStatusCode.Found) { Headers = { Location = new Uri(to) } };

    private static HttpResponseMessage File(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    /// <summary>GitHub's real shape: the release address redirects once to its storage host, which serves the file.</summary>
    private static Handler GitHub(byte[]? bytes = null, string storage = Storage) => new((request, _) => Task.FromResult(
        request.RequestUri!.Host == "github.com" ? Redirect(storage) : File(bytes ?? Payload)));

    private int FilesLeft() => Directory.Exists(_root) ? Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Count() : 0;

    [Fact]
    public async Task AMatchingDownloadIsKeptAndReportsProgress()
    {
        var progress = new List<double>();
        using var installer = Installer(GitHub());

        var result = await installer.DownloadAsync(Release(), new SyncProgress(progress.Add), Ct);

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal(Payload, await System.IO.File.ReadAllBytesAsync(result.Value.Path, Ct));
        Assert.StartsWith(_root, result.Value.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new AppVersion(1, 0, 6), result.Value.Version);
        Assert.Equal(1.0, progress[^1]);
        Assert.True(progress.SequenceEqual(progress.Order()));
    }

    [Fact]
    public async Task AWrongDigestDeletesTheFileAndNothingCanRunIt()
    {
        using var installer = Installer(GitHub());

        var result = await installer.DownloadAsync(Release(digest: new string('0', 64)), null, Ct);

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.Equal(0, FilesLeft());
        Assert.Empty(_started);
        Assert.Contains(_log.Events, e => e.EventId == "update.download.digest_mismatch");
    }

    [Theory]
    [InlineData(-1)] // the server sends more than the release says
    [InlineData(+1)] // or less
    public async Task ASizeOtherThanTheReleaseSaysIsRefused(int difference)
    {
        using var installer = Installer(GitHub());

        var result = await installer.DownloadAsync(Release(size: Payload.Length + difference), null, Ct);

        Assert.True(result.IsFailure);
        Assert.Equal(0, FilesLeft());
    }

    [Fact]
    public async Task AStreamLongerThanTheReleaseIsCutOffWithoutALengthHeader()
    {
        var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.Host == "github.com"
            ? Redirect(Storage)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new UnknownLengthStream(Payload)) }));
        using var installer = Installer(handler);

        var result = await installer.DownloadAsync(Release(size: 1000), null, Ct);

        Assert.Contains(_log.Events, e => e.EventId == "update.download.too_large");
        Assert.True(result.IsFailure);
        Assert.Equal(0, FilesLeft());
    }

    [Theory]
    [InlineData("https://evil.example/Lunar.msi")]
    [InlineData("http://release-assets.githubusercontent.com/Lunar.msi")]
    [InlineData("https://release-assets.githubusercontent.com.evil.example/Lunar.msi")]
    [InlineData("https://raw.githubusercontent.com/Lunar.msi")]
    public async Task ARedirectToAnotherHostOrToHttpIsNotFollowed(string target)
    {
        var handler = GitHub(storage: target);
        using var installer = Installer(handler);

        var result = await installer.DownloadAsync(Release(), null, Ct);

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.Single(handler.Requests); // the other host is never asked
        Assert.Equal(0, FilesLeft());
    }

    [Fact]
    public async Task EndlessRedirectsStop()
    {
        var handler = new Handler((_, _) => Task.FromResult(Redirect(Start)));
        using var installer = Installer(handler);

        Assert.True((await installer.DownloadAsync(Release(), null, Ct)).IsFailure);
        Assert.Equal(6, handler.Requests.Count);
    }

    [Fact]
    public async Task AReleaseAddressOffGitHubIsRefusedBeforeAnyRequest()
    {
        var handler = GitHub();
        using var installer = Installer(handler);

        Assert.True((await installer.DownloadAsync(Release(address: "https://evil.example/Lunar.msi"), null, Ct)).IsFailure);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ATooLargeReleaseIsRefusedBeforeAnyRequest()
    {
        var handler = GitHub();
        using var installer = Installer(handler);

        Assert.True((await installer.DownloadAsync(Release(size: UpdateRules.MaxInstallerBytes + 1), null, Ct)).IsFailure);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CancellingDeletesWhatWasWritten()
    {
        using var cancel = new CancellationTokenSource();
        var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.Host == "github.com"
            ? Redirect(Storage)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new CancellingStream(Payload, cancel)) }));
        using var installer = Installer(handler);

        var result = await installer.DownloadAsync(Release(), null, cancel.Token);

        Assert.True(result.IsCancelled);
        Assert.Equal(0, FilesLeft());
    }

    [Fact]
    public async Task AServerErrorIsAPlainRetryableFailure()
    {
        using var installer = Installer(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        var result = await installer.DownloadAsync(Release(), null, Ct);
        Assert.Equal(ErrorCode.DeviceUnavailable, result.Error!.Code);
        Assert.True(result.Error.Retryable);
        Assert.Equal(0, FilesLeft());
    }

    [Fact]
    public async Task TheInstallerIsStartedWithFixedArgumentsAndNoShell()
    {
        using var installer = Installer(GitHub());
        var file = (await installer.DownloadAsync(Release(), null, Ct)).Value;

        Assert.True(installer.StartInstall(file).IsSuccess);

        var info = Assert.Single(_started);
        Assert.Equal(Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.System), "msiexec.exe"), info.FileName);
        Assert.False(info.UseShellExecute);
        Assert.Equal(["/i", file.Path, "/passive", "/norestart", "LUNAR_RELAUNCH=1"], info.ArgumentList);
    }

    [Fact]
    public void AFileOutsideTheUpdatesFolderIsNeverStarted()
    {
        var outside = Path.Combine(Path.GetTempPath(), "LunarUpdateTests", "outside-" + Guid.NewGuid().ToString("N") + ".msi");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        System.IO.File.WriteAllBytes(outside, [1]);
        try
        {
            using var installer = Installer(GitHub());
            Assert.True(installer.StartInstall(new VerifiedInstaller(outside, new AppVersion(1, 0, 6))).IsFailure);
            Assert.True(installer.StartInstall(new VerifiedInstaller(Path.Combine(_root, "..", Path.GetFileName(outside)), new AppVersion(1, 0, 6))).IsFailure);
            Assert.Empty(_started);
        }
        finally
        {
            System.IO.File.Delete(outside);
        }
    }

    [Fact]
    public async Task LeftoversAreRemovedAndAFileInUseIsLeftForNextTime()
    {
        using var installer = Installer(GitHub());
        var kept = (await installer.DownloadAsync(Release(), null, Ct)).Value;
        var other = Path.Combine(_root, "older", "Lunar-1.0.5.msi");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        await System.IO.File.WriteAllBytesAsync(other, [1, 2, 3], Ct);

        using (new FileStream(kept.Path, FileMode.Open, FileAccess.Read, FileShare.None))
            installer.RemoveLeftovers();

        Assert.False(System.IO.File.Exists(other));
        Assert.True(System.IO.File.Exists(kept.Path)); // still in use: no throw, next start tries again

        installer.RemoveLeftovers();
        Assert.Equal(0, FilesLeft());
    }

    [Fact]
    public async Task LogsNameNoAddressPathOrDigest()
    {
        using var installer = Installer(GitHub());
        await installer.DownloadAsync(Release(digest: new string('0', 64)), null, Ct);
        var file = (await installer.DownloadAsync(Release(), null, Ct)).Value;
        installer.StartInstall(file);

        Assert.NotEmpty(_log.Events);
        Assert.All(_log.Events, e => Assert.Matches("^update\\.[a-z_.]+$", e.EventId));
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return await respond(request, cancellationToken);
        }
    }

    private sealed class RecordingLog : IDiagnosticLog
    {
        public List<DiagnosticEvent> Events { get; } = [];

        public void Write(DiagnosticEvent diagnosticEvent) => Events.Add(diagnosticEvent);
    }

    /// <summary>A stream whose length is not known, so no Content-Length header is sent.</summary>
    private sealed class UnknownLengthStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    /// <summary>Cancels the download after the first chunk has been read.</summary>
    private sealed class CancellingStream(byte[] bytes, CancellationTokenSource cancel) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer[..Math.Min(buffer.Length, 1000)], cancellationToken);
            await cancel.CancelAsync();
            return read;
        }
    }
}
