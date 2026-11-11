using System.Net;
using System.Text;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Domain.Shared;
using Lunar.Domain.Updates;
using Lunar.Infrastructure.Updates;
using Xunit;

namespace Lunar.Infrastructure.Tests.Updates;

// BE-44, ADR-0025: the GitHub release feed against recorded replies. The reply shape follows the real "latest release" reply of
// rafaelevan-k/lunar seen on 2026-11-12 (v1.0.5), trimmed to the fields Lunar reads plus some it must ignore. The real request is
// RealCheckAgainstTheLunarRepository, skipped unless LUNAR_REAL_UPDATE_CHECK=1.
public sealed class GitHubReleaseFeedTests
{
    private const string Digest = "87a42ae27d683731bec08fb9f8118616dc3492eeefee2cffa487f366c835a2a4";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Reply(
        string tag = "v1.0.6",
        bool draft = false,
        bool prerelease = false,
        string name = "Lunar.msi",
        string url = "https://github.com/rafaelevan-k/lunar/releases/download/v1.0.6/Lunar.msi",
        string? digest = "sha256:" + Digest,
        long size = 71_000_000) =>
        $$"""
        {
          "url": "https://api.github.com/repos/rafaelevan-k/lunar/releases/1",
          "tag_name": "{{tag}}",
          "name": "Lunar {{tag}}",
          "draft": {{(draft ? "true" : "false")}},
          "prerelease": {{(prerelease ? "true" : "false")}},
          "body": "Release notes are not read.",
          "assets": [
            { "name": "Lunar.msi.sha256", "size": 75, "digest": "sha256:{{new string('b', 64)}}", "browser_download_url": "https://github.com/rafaelevan-k/lunar/releases/download/{{tag}}/Lunar.msi.sha256" },
            { "name": "{{name}}", "size": {{size}}, "digest": {{(digest is null ? "null" : "\"" + digest + "\"")}}, "browser_download_url": "{{url}}", "uploader": { "login": "someone" } }
          ]
        }
        """;

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return await respond(request, cancellationToken);
        }
    }

    private sealed class RecordingLog : IDiagnosticLog
    {
        public List<DiagnosticEvent> Events { get; } = [];

        public void Write(DiagnosticEvent diagnosticEvent) => Events.Add(diagnosticEvent);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static GitHubReleaseFeed Feed(Handler handler, RecordingLog? log = null, TimeSpan? timeout = null) =>
        new(handler, GitHubReleaseFeed.DefaultAddress, log, TimeProvider.System, timeout ?? TimeSpan.FromSeconds(15));

    // ---- the reply ------------------------------------------------------------------------------------------------------

    [Fact]
    public void APublishedReleaseGivesItsVersionInstallerDigestAndSize()
    {
        var release = GitHubReleaseParser.Parse(Encoding.UTF8.GetBytes(Reply()), out var rejection);

        Assert.Equal(ReleaseRejection.None, rejection);
        Assert.NotNull(release);
        Assert.Equal(new AppVersion(1, 0, 6), release.Version);
        Assert.Equal("https://github.com/rafaelevan-k/lunar/releases/download/v1.0.6/Lunar.msi", release.InstallerAddress.AbsoluteUri);
        Assert.Equal(Digest, release.Sha256);
        Assert.Equal(71_000_000, release.Size);
    }

    [Theory]
    [InlineData(true, false, "Draft")]
    [InlineData(false, true, "Prerelease")]
    public void DraftsAndPrereleasesAreNotOffered(bool draft, bool prerelease, string expected)
    {
        Assert.Null(GitHubReleaseParser.Parse(Encoding.UTF8.GetBytes(Reply(draft: draft, prerelease: prerelease)), out var rejection));
        Assert.Equal(expected, rejection.ToString());
    }

    public static TheoryData<string, string> Unusable => new()
    {
        { Reply(tag: "nightly"), "BadTag" },
        { Reply(tag: "v1.0.6-rc1"), "BadTag" },
        { Reply(name: "Lunar-setup.exe"), "NoInstaller" },
        { Reply(url: "http://github.com/rafaelevan-k/lunar/releases/download/v1.0.6/Lunar.msi"), "BadAddress" },
        { Reply(url: "https://evil.example/Lunar.msi"), "BadAddress" },
        { Reply(url: "https://github.com.evil.example/Lunar.msi"), "BadAddress" },
        { Reply(url: "https://user:pass@github.com/Lunar.msi"), "BadAddress" },
        { Reply(url: "https://github.com:8443/Lunar.msi"), "BadAddress" },
        { Reply(digest: null), "NoDigest" },
        { Reply(digest: "sha1:" + new string('a', 40)), "NoDigest" },
        { Reply(digest: "sha256:abc"), "NoDigest" },
        { Reply(size: 0), "BadSize" },
        { Reply(size: 400L * 1024 * 1024), "BadSize" },
        { "not json", "NotJson" },
        { "[1, 2]", "NotJson" },
        { """{"tag_name":"v1.0.6","assets":"none"}""", "NoInstaller" }
    };

    [Theory]
    [MemberData(nameof(Unusable))]
    public void AnythingNotPlainlyInstallableIsRefused(string body, string expected)
    {
        Assert.Null(GitHubReleaseParser.Parse(Encoding.UTF8.GetBytes(body), out var rejection));
        Assert.Equal(expected, rejection.ToString());
    }

    // ---- the request ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OneHttpsGetToTheFixedAddressWithNothingAboutTheUser()
    {
        var handler = new Handler((_, _) => Task.FromResult(Json(Reply())));
        using var feed = Feed(handler);

        var result = await feed.GetLatestAsync(Ct);

        Assert.True(result.IsSuccess);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.github.com/repos/rafaelevan-k/lunar/releases/latest", request.RequestUri!.AbsoluteUri);
        Assert.Null(request.Headers.Authorization);
        Assert.Equal("Lunar-Updater", request.Headers.UserAgent.ToString());
        Assert.Null(request.Content);
    }

    [Fact]
    public async Task NoReleaseYetIsNothingNew()
    {
        using var feed = Feed(new Handler((_, _) => Task.FromResult(Json("""{"message":"Not Found"}""", HttpStatusCode.NotFound))));
        var result = await feed.GetLatestAsync(Ct);
        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "update.request.limited")]
    [InlineData((HttpStatusCode)429, "update.request.limited")]
    [InlineData(HttpStatusCode.BadGateway, "update.request.refused")]
    [InlineData(HttpStatusCode.MovedPermanently, "update.request.refused")] // a redirect is never followed
    public async Task RefusalsAreRetryableAndLoggedWithoutDetails(HttpStatusCode status, string eventId)
    {
        var log = new RecordingLog();
        using var feed = Feed(new Handler((_, _) => Task.FromResult(Json("""{"message":"API rate limit exceeded for 203.0.113.9"}""", status))), log);

        var result = await feed.GetLatestAsync(Ct);

        Assert.True(result.IsFailure);
        Assert.True(result.Error!.Retryable);
        var logged = Assert.Single(log.Events);
        Assert.Equal(eventId, logged.EventId);
    }

    [Fact]
    public async Task NoNetworkIsRetryable()
    {
        using var feed = Feed(new Handler((_, _) => throw new HttpRequestException("No such host is known. (api.github.com:443)")));
        var result = await feed.GetLatestAsync(Ct);
        Assert.Equal(ErrorCode.DeviceUnavailable, result.Error!.Code);
        Assert.True(result.Error.Retryable);
        Assert.DoesNotContain("api.github.com", result.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASlowReplyIsATimeoutAndACancelIsACancel()
    {
        using (var slow = Feed(new Handler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Json(Reply());
        }), timeout: TimeSpan.FromMilliseconds(50)))
        {
            Assert.Equal(ErrorCode.Timeout, (await slow.GetLatestAsync(Ct)).Error!.Code);
        }

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        using var feed = Feed(new Handler((_, _) => Task.FromResult(Json(Reply()))));
        Assert.True((await feed.GetLatestAsync(cancelled.Token)).IsCancelled);
    }

    [Fact]
    public async Task AnOversizedReplyIsNotRead()
    {
        var big = "{\"pad\":\"" + new string('x', 300 * 1024) + "\"}";
        using var feed = Feed(new Handler((_, _) => Task.FromResult(Json(big))));
        Assert.Equal(ErrorCode.InvalidInput, (await feed.GetLatestAsync(Ct)).Error!.Code);
    }

    [Fact]
    public async Task AnUnusableReleaseIsLoggedAsSuchAndOffersNothing()
    {
        var log = new RecordingLog();
        using var feed = Feed(new Handler((_, _) => Task.FromResult(Json(Reply(digest: null)))), log);

        var result = await feed.GetLatestAsync(Ct);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal("update.release.unusable", Assert.Single(log.Events).EventId);
    }

    [Fact]
    public void OnlyAnHttpsAddressIsAccepted() =>
        Assert.Throws<ArgumentException>(() => new GitHubReleaseFeed(null, "http://api.github.com/repos/rafaelevan-k/lunar/releases/latest", null, TimeProvider.System, TimeSpan.FromSeconds(1)));

    // ---- the real repository --------------------------------------------------------------------------------------------

    // One real GET to GitHub. Skipped unless LUNAR_REAL_UPDATE_CHECK=1; it downloads nothing.
    [Fact]
    public async Task RealCheckAgainstTheLunarRepository()
    {
        if (Environment.GetEnvironmentVariable("LUNAR_REAL_UPDATE_CHECK") != "1")
        {
            Assert.Skip("Set LUNAR_REAL_UPDATE_CHECK=1 to ask GitHub for the latest Lunar release once.");
            return;
        }

        using var feed = new GitHubReleaseFeed();
        var result = await feed.GetLatestAsync(Ct);

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.NotNull(result.Value);
        Assert.True(result.Value.Version >= new AppVersion(1, 0, 5));
        Assert.True(UpdateRules.IsSha256Hex(result.Value.Sha256));
        TestContext.Current.SendDiagnosticMessage("Latest release: {0}, {1} bytes", result.Value.Version, result.Value.Size);
    }
}
