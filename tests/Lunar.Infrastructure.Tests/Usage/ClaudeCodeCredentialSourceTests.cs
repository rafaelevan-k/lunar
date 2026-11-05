using System.Text;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Domain.Shared;
using Lunar.Infrastructure.Usage;
using Xunit;

namespace Lunar.Infrastructure.Tests.Usage;

// ADR-0015: the reader of the user's own Claude Code login. Synthetic files only; the user's real login file is never touched by a test.
public sealed class ClaudeCodeCredentialSourceTests : IDisposable
{
    private const string Token = "sk-ant-oat01-THIS-IS-A-FAKE-ACCESS-TOKEN-0123456789";
    private const string RefreshToken = "sk-ant-ort01-THIS-IS-A-FAKE-REFRESH-TOKEN-9876543210";
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTime : TimeProvider
    {
        public DateTimeOffset Value { get; set; } = Now;

        public override DateTimeOffset GetUtcNow() => Value;
    }

    private sealed class RecordingLog : IDiagnosticLog
    {
        public List<DiagnosticEvent> Events { get; } = [];

        public void Write(DiagnosticEvent diagnosticEvent) => Events.Add(diagnosticEvent);
    }

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "lunar-be34-" + Guid.NewGuid().ToString("N"));
    private readonly FixedTime _time = new();
    private readonly RecordingLog _log = new();

    public ClaudeCodeCredentialSourceTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string PathOf => Path.Combine(_folder, ".credentials.json");

    private ClaudeCodeCredentialSource Source() => new(PathOf, _log, _time);

    private static string Json(string accessToken = Token, long? expiresAt = null, string scopes = "\"user:inference\",\"user:profile\"", string extra = "") =>
        "{\"claudeAiOauth\":{\"accessToken\":\"" + accessToken + "\",\"refreshToken\":\"" + RefreshToken + "\",\"expiresAt\":"
        + (expiresAt ?? Now.AddHours(8).ToUnixTimeMilliseconds()) + ",\"refreshTokenExpiresAt\":1999999999999,\"scopes\":[" + scopes
        + "],\"subscriptionType\":\"max\"" + extra + "},\"other\":{\"keep\":[1,2,{\"x\":null}]}}";

    private void Write(string text) => File.WriteAllText(PathOf, text, new UTF8Encoding(false));

    private static void AssertRefusal(Result<Lunar.Application.Usage.Models.UsageCredential> result, string messagePart)
    {
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCode.AccessDenied, result.Error!.Code);
        Assert.False(result.Error.Retryable);
        Assert.Contains(messagePart, result.Error.SafeMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-ant", result.Error.SafeMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(_folderMarker, result.Error.SafeMessage, StringComparison.Ordinal);
    }

    private const string _folderMarker = "lunar-be34-";

    [Fact]
    public async Task AValidLoginGivesItsAccessTokenAndOnlyThat()
    {
        Write(Json());

        var result = await Source().GetAsync(Ct);

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal(Token, result.Value.Token);
        Assert.Equal("UsageCredential", result.Value.ToString()); // prints as nothing but its type
        Assert.DoesNotContain(RefreshToken, result.Value.Token, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFileIsNeverChangedByReadingIt()
    {
        Write(Json());
        var bytes = File.ReadAllBytes(PathOf);
        var written = File.GetLastWriteTimeUtc(PathOf);

        for (var i = 0; i < 3; i++)
            await Source().GetAsync(Ct);

        Assert.Equal(bytes, File.ReadAllBytes(PathOf));
        Assert.Equal(written, File.GetLastWriteTimeUtc(PathOf));
        Assert.Equal(["\\.credentials.json"], Directory.GetFileSystemEntries(_folder).Select(p => p[_folder.Length..]).ToArray()); // no copy, no lock file
    }

    [Fact]
    public async Task ClaudeCodeCanWriteTheFileWhileItIsBeingRead()
    {
        Write(Json());
        // A writer that holds the file open for writing (as Claude Code does while renewing) must not stop the read.
        await using var writer = new FileStream(PathOf, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

        var result = await Source().GetAsync(Ct);

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
    }

    [Fact]
    public async Task ABomAtTheStartIsTolerated()
    {
        File.WriteAllText(PathOf, Json(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.True((await Source().GetAsync(Ct)).IsSuccess);
    }

    [Fact]
    public async Task OtherFieldsOfAnyShapeAreSkippedIncludingNestedOnes()
    {
        Write(Json(extra: ",\"nested\":{\"a\":[1,{\"b\":\"c\"}],\"d\":true},\"list\":[[],[1,2]]"));

        Assert.True((await Source().GetAsync(Ct)).IsSuccess);
    }

    [Fact]
    public async Task NoFileMeansNotSignedInAndNothingIsLoggedAboveDebug()
    {
        var result = await Source().GetAsync(Ct);

        AssertRefusal(result, "not signed in");
        Assert.Equal("usage.login.missing", Assert.Single(_log.Events).EventId);
        Assert.All(_log.Events, e => Assert.Equal(DiagnosticLevel.Debug, e.Level));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"claudeAiOauth\":[]}")]
    [InlineData("{\"claudeAiOauth\":{}}")]
    [InlineData("{\"claudeAiOauth\":{\"accessToken\":5,\"expiresAt\":1,\"scopes\":[]}}")]
    [InlineData("{\"claudeAiOauth\":{\"accessToken\":\"\",\"expiresAt\":4102444800000,\"scopes\":[\"user:profile\"]}}")]
    [InlineData("{\"claudeAiOauth\":{\"accessToken\":\"abc def\",\"expiresAt\":4102444800000,\"scopes\":[\"user:profile\"]}}")]
    [InlineData("{\"claudeAiOauth\":{\"accessToken\":\"tok\",\"scopes\":[\"user:profile\"]}}")] // no expiry
    [InlineData("{\"claudeAiOauth\":{\"accessToken\":\"tok\",\"expiresAt\":\"later\",\"scopes\":[\"user:profile\"]}}")]
    [InlineData("{\"claudeAiOauth\":{\"accessToken\":\"tok\",\"expiresAt\":4102444800000,\"scopes\":\"user:profile\"}}")]
    [InlineData("{\"claudeAiOauth\":{\"accessToken\":\"tok\",\"expiresAt\":-5,\"scopes\":[\"user:profile\"]}}")]
    [InlineData("{\"claudeAiOauth\":{\"accessToken\":\"tok\"")] // cut short
    public async Task AFileThatIsNotAWholeLoginIsUnreadableNotACrash(string content)
    {
        Write(content);

        var result = await Source().GetAsync(Ct);

        AssertRefusal(result, "could not read the Claude Code sign-in");
    }

    [Fact]
    public async Task ATokenWithNonAsciiCharactersIsRefused()
    {
        Write(Json(accessToken: "sk-ant-oat01-héllo-wörld-0123456789"));

        AssertRefusal(await Source().GetAsync(Ct), "could not read the Claude Code sign-in");
    }

    [Fact]
    public async Task ATokenOverTheLengthLimitIsRefused()
    {
        Write(Json(accessToken: new string('a', ClaudeCodeCredentialSource.MaxTokenChars + 1)));

        AssertRefusal(await Source().GetAsync(Ct), "could not read the Claude Code sign-in");
    }

    [Fact]
    public async Task AFileOverTheSizeLimitIsRefusedWithoutBeingRead()
    {
        Write(Json(extra: ",\"pad\":\"" + new string('x', (int)ClaudeCodeCredentialSource.MaxFileBytes) + "\""));

        AssertRefusal(await Source().GetAsync(Ct), "could not read the Claude Code sign-in");
    }

    [Fact]
    public async Task ALoginWithoutTheProfilePermissionIsToldApartFromOtherProblems()
    {
        // What a token from "claude setup-token" looks like: it can run Claude and nothing else.
        Write(Json(scopes: "\"user:inference\""));

        var result = await Source().GetAsync(Ct);

        AssertRefusal(result, "does not allow reading usage");
        Assert.Contains("not \\\"claude setup-token\\\"".Replace("\\\"", "\""), result.Error!.SafeMessage, StringComparison.Ordinal);
        Assert.Equal("usage.login.no_permission", Assert.Single(_log.Events).EventId);
    }

    [Fact]
    public async Task AnExpiredLoginSaysSoAndNeedsNoRequest()
    {
        Write(Json(expiresAt: Now.AddMinutes(-5).ToUnixTimeMilliseconds()));

        AssertRefusal(await Source().GetAsync(Ct), "has expired");
    }

    [Fact]
    public async Task ALoginThatEndsWithinAMinuteIsTreatedAsExpiredAndOneThatEndsLaterIsNot()
    {
        Write(Json(expiresAt: Now.AddSeconds(59).ToUnixTimeMilliseconds()));
        AssertRefusal(await Source().GetAsync(Ct), "has expired");

        Write(Json(expiresAt: Now.AddSeconds(61).ToUnixTimeMilliseconds()));
        Assert.True((await Source().GetAsync(Ct)).IsSuccess);
    }

    [Fact]
    public async Task TheClockDecidesAndAFileThatWasValidBecomesExpired()
    {
        Write(Json(expiresAt: Now.AddHours(1).ToUnixTimeMilliseconds()));
        Assert.True((await Source().GetAsync(Ct)).IsSuccess);

        _time.Value = Now.AddHours(2);

        AssertRefusal(await Source().GetAsync(Ct), "has expired");
    }

    [Fact]
    public async Task ARenewedLoginIsPickedUpOnTheNextReadWithNothingKept()
    {
        Write(Json(accessToken: "sk-ant-oat01-FIRST-TOKEN-AAAAAAAAAAAAAAAAAAAA"));
        var first = await Source().GetAsync(Ct);
        Write(Json(accessToken: "sk-ant-oat01-SECOND-TOKEN-BBBBBBBBBBBBBBBBBBB"));

        var second = await Source().GetAsync(Ct);

        Assert.Equal("sk-ant-oat01-FIRST-TOKEN-AAAAAAAAAAAAAAAAAAAA", first.Value.Token);
        Assert.Equal("sk-ant-oat01-SECOND-TOKEN-BBBBBBBBBBBBBBBBBBB", second.Value.Token);
    }

    [Fact]
    public async Task AFolderWhereTheFileShouldBeIsNotAFile()
    {
        Directory.CreateDirectory(PathOf);

        AssertRefusal(await Source().GetAsync(Ct), "not signed in");
    }

    [Fact]
    public async Task ASymbolicLinkInPlaceOfTheFileIsRefused()
    {
        var target = Path.Combine(_folder, "elsewhere.json");
        Write(Json());
        File.Move(PathOf, target);
        try
        {
            File.CreateSymbolicLink(PathOf, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This account may not create symbolic links; NOT_RUN here.");
            return;
        }

        AssertRefusal(await Source().GetAsync(Ct), "could not read the Claude Code sign-in");
    }

    [Fact]
    public async Task ACancelledCallReadsNothing()
    {
        Write(Json());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var result = await Source().GetAsync(cancelled.Token);

        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
    }

    [Fact]
    public async Task NeitherTheTokensNorThePathReachTheLogOrAnyMessage()
    {
        Write(Json(scopes: "\"user:inference\""));
        await Source().GetAsync(Ct);
        Write(Json(expiresAt: Now.AddDays(-1).ToUnixTimeMilliseconds()));
        await Source().GetAsync(Ct);
        Write("garbage");
        await Source().GetAsync(Ct);

        Assert.NotEmpty(_log.Events);
        var all = string.Join('|', _log.Events.Select(e => $"{e.EventId} {e.Component} {e.Operation} {e.Error}"));
        Assert.DoesNotContain("sk-ant", all, StringComparison.Ordinal);
        Assert.DoesNotContain(_folderMarker, all, StringComparison.Ordinal);
        foreach (var error in new[] { ClaudeCodeCredentialSource.NotSignedIn, ClaudeCodeCredentialSource.Unreadable, ClaudeCodeCredentialSource.NoPermission, ClaudeCodeCredentialSource.Expired })
        {
            Assert.DoesNotContain("credentials.json", error.SafeMessage, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\\", error.SafeMessage, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheDefaultPathIsClaudeCodesLoginFileInTheUsersProfile() =>
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json"), ClaudeCodeCredentialSource.DefaultPath);

    // The promises of ADR-0015 that can be read from the source: it opens the file only for reading, never writes, and never names the refresh token.
    [Fact]
    public void TheReaderNeverWritesTheFileAndNeverMentionsTheRefreshToken()
    {
        var root = Lunar.Architecture.Tests.LayerBoundaryTests.RepositoryRoot;
        var text = File.ReadAllText(Path.Combine(root, "src", "Lunar.Infrastructure", "Usage", "ClaudeCodeCredentialSource.cs"));
        var code = string.Join('\n', text.Split('\n').Where(l => !l.TrimStart().StartsWith("///", StringComparison.Ordinal) && !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        Assert.Contains("FileAccess.Read", code, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "FileAccess.Write", "FileAccess.ReadWrite", "FileMode.Create", "FileMode.Append", "File.Write", "File.Delete", "File.Move", "File.Copy", "File.Replace", "refreshToken", "HttpClient" })
            Assert.DoesNotContain(forbidden, code, StringComparison.Ordinal);
    }
}
