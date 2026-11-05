using System.Net;
using System.Text;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Usage;
using Lunar.Application.Usage.Models;
using Lunar.Domain.Shared;
using Lunar.Infrastructure.Usage;
using Xunit;

namespace Lunar.Infrastructure.Tests.Usage;

// BE-31: the usage source against a stand-in network. The real service, a real login and the real reply format are NOT_RUN (no login is
// available to this project yet, and the endpoint is undocumented: ADR-0014).
public sealed class AnthropicUsageSourceTests
{
    private const string Secret = "sk-ant-oat01-SECRET-TOKEN-VALUE";

    private sealed class Credentials(Result<UsageCredential>? result = null) : IUsageCredentialSource
    {
        public int Calls { get; private set; }

        public Task<Result<UsageCredential>> GetAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(result ?? Result.Success(new UsageCredential(Secret)));
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string?> Authorization { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Authorization.Add(request.Headers.Authorization?.ToString());
            return await respond(request, cancellationToken);
        }
    }

    private sealed class RecordingLog : IDiagnosticLog
    {
        public List<DiagnosticEvent> Events { get; } = [];

        public void Write(DiagnosticEvent diagnosticEvent) => Events.Add(diagnosticEvent);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static AnthropicUsageSource Source(Handler handler, Credentials? credentials = null, RecordingLog? log = null, string address = AnthropicUsageSource.DefaultAddress, TimeSpan? timeout = null) =>
        new(credentials ?? new Credentials(), handler, address, log, TimeProvider.System, timeout ?? TimeSpan.FromSeconds(15));

    private const string Good = """{"five_hour":{"utilization":0.67,"resets_at":"2026-11-05T18:00:00Z"},"seven_day":{"utilization_pct":12.5,"resets_at":1762700000}}""";

    // ---- the request ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ItAsksOnceOverHttpsWithTheLoginInTheAuthorizationHeaderOnly()
    {
        var handler = new Handler((_, _) => Task.FromResult(Json(Good)));

        var result = await Source(handler).FetchAsync(Ct);

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(Uri.UriSchemeHttps, request.RequestUri!.Scheme);
        Assert.Equal(AnthropicUsageSource.DefaultAddress, request.RequestUri.ToString());
        Assert.Equal("Bearer " + Secret, Assert.Single(handler.Authorization));
        Assert.DoesNotContain(Secret, request.RequestUri.ToString(), StringComparison.Ordinal); // never in the address
        Assert.Equal(AnthropicUsageSource.BetaHeaderValue, Assert.Single(request.Headers.GetValues("anthropic-beta")));
        Assert.Null(request.Content);
    }

    [Theory]
    [InlineData("http://api.anthropic.com/api/oauth/usage")]
    [InlineData("ftp://api.anthropic.com/x")]
    [InlineData("api.anthropic.com/x")]
    [InlineData("https://user:pass@api.anthropic.com/x")]
    public void OnlyAPlainHttpsAddressIsAccepted(string address)
    {
        Assert.Throws<ArgumentException>(() => Source(new Handler((_, _) => Task.FromResult(Json(Good))), address: address));
    }

    [Fact]
    public async Task NoLoginMeansNoRequestAtAll()
    {
        var handler = new Handler((_, _) => Task.FromResult(Json(Good)));
        var missing = new Credentials(Result.Failure<UsageCredential>(new OperationError(ErrorCode.AccessDenied, "Sign in to Claude first.")));

        var result = await Source(handler, missing).FetchAsync(Ct);

        Assert.Equal(ErrorCode.AccessDenied, result.Error!.Code);
        Assert.Equal("Sign in to Claude first.", result.Error.SafeMessage);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AskedWhileAlreadyCancelledItDoesNothing()
    {
        var handler = new Handler((_, _) => Task.FromResult(Json(Good)));
        var credentials = new Credentials();
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        var result = await Source(handler, credentials).FetchAsync(cancel.Token);

        Assert.True(result.IsCancelled);
        Assert.Equal(0, credentials.Calls);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ACancelDuringTheRequestIsACancelNotAFailure()
    {
        using var cancel = new CancellationTokenSource();
        var handler = new Handler(async (_, token) =>
        {
            await cancel.CancelAsync();
            await Task.Delay(Timeout.Infinite, token);
            return Json(Good);
        });

        var result = await Source(handler).FetchAsync(cancel.Token);

        Assert.True(result.IsCancelled);
    }

    // ---- the reading ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task BothLimitsAreReadWithTheirResetTimes()
    {
        var result = await Source(new Handler((_, _) => Task.FromResult(Json(Good)))).FetchAsync(Ct);

        var snapshot = result.Value;
        Assert.Equal(UsageWindowKind.FiveHour, snapshot.FiveHour!.Kind);
        Assert.Equal(67, snapshot.FiveHour.UsedPercent);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 18, 0, 0, TimeSpan.Zero), snapshot.FiveHour.ResetsAt);
        Assert.Equal(UsageWindowKind.Weekly, snapshot.Weekly!.Kind);
        Assert.Equal(13, snapshot.Weekly.UsedPercent); // 12.5 rounds away from zero
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1762700000), snapshot.Weekly.ResetsAt);
    }

    [Theory]
    [InlineData("""{"five_hour":{"utilization":0.5,"resets_at":"2026-11-05T10:00:00+07:00"}}""", 50)]
    [InlineData("""{"five_hour":{"utilization":50,"resets_at":null}}""", 50)]
    [InlineData("""{"five_hour":{"utilization_pct":99.6}}""", 100)]
    [InlineData("""{"limits":{"five_hour":{"utilization":0.25},"seven_day":{"utilization":0.1}}}""", 25)]
    [InlineData("""{"five_hour":{"utilization":0}}""", 0)]
    [InlineData("""{"five_hour":{"utilization":250}}""", 100)]
    public async Task TheCommunityReportedShapesAreRead(string body, int expectedPercent)
    {
        var result = await Source(new Handler((_, _) => Task.FromResult(Json(body)))).FetchAsync(Ct);

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal(expectedPercent, result.Value.FiveHour!.UsedPercent);
    }

    [Theory]
    [InlineData("""{"five_hour":{"utilization":-1}}""")]
    [InlineData("""{"five_hour":{"utilization":"lots"}}""")]
    [InlineData("""{"five_hour":{}}""")]
    public async Task AFigureThatCannotBeReadIsNoFigureNeverAGuess(string body)
    {
        var result = await Source(new Handler((_, _) => Task.FromResult(Json(body)))).FetchAsync(Ct);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.FiveHour!.UsedPercent);
    }

    [Fact]
    public async Task AResetTimeThatCannotBeReadIsNoTime()
    {
        var result = await Source(new Handler((_, _) => Task.FromResult(Json("""{"five_hour":{"utilization":0.1,"resets_at":"soon"},"seven_day":{"utilization":0.1,"resets_at":99999999999999999999}}""")))).FetchAsync(Ct);

        Assert.Null(result.Value.FiveHour!.ResetsAt);
        Assert.Null(result.Value.Weekly!.ResetsAt);
    }

    [Fact]
    public async Task AWindowTheServiceDoesNotReportIsAbsent()
    {
        var result = await Source(new Handler((_, _) => Task.FromResult(Json("""{"five_hour":{"utilization":0.3},"seven_day":null}""")))).FetchAsync(Ct);

        Assert.NotNull(result.Value.FiveHour);
        Assert.Null(result.Value.Weekly);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"other":1}""")]
    [InlineData("""{"five_hour":"x","seven_day":3}""")]
    [InlineData("""{"a":{"b":{"c":{"d":{"e":{"f":{"g":{"h":{"i":1}}}}}}}}}""")]
    public async Task AReplyItDoesNotUnderstandShowsNothingAndSaysSo(string body)
    {
        var result = await Source(new Handler((_, _) => Task.FromResult(Json(body)))).FetchAsync(Ct);

        Assert.Equal(ErrorCode.Unexpected, result.Error!.Code);
        Assert.Contains("not in a form Lunar understands", result.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOversizeReplyIsRefusedWithoutBeingRead()
    {
        var handler = new Handler((_, _) => Task.FromResult(Json("{\"five_hour\":{\"utilization\":0.1}," + new string(' ', 70_000) + "}")));

        var result = await Source(handler).FetchAsync(Ct);

        Assert.Equal(ErrorCode.Unexpected, result.Error!.Code);
    }

    // ---- failures are typed, fixed and free of secrets -----------------------------------------------------------------

    [Theory]
    [InlineData(401, ErrorCode.AccessDenied, false)]
    [InlineData(403, ErrorCode.AccessDenied, false)]
    [InlineData(429, ErrorCode.DeviceUnavailable, true)]
    [InlineData(500, ErrorCode.DeviceUnavailable, true)]
    [InlineData(503, ErrorCode.DeviceUnavailable, true)]
    [InlineData(404, ErrorCode.Unexpected, false)]
    [InlineData(400, ErrorCode.Unexpected, false)]
    public async Task EachKindOfRefusalHasItsOwnTypedAnswer(int status, ErrorCode code, bool retryable)
    {
        var handler = new Handler((_, _) => Task.FromResult(Json("""{"error":"details with sk-ant-oat01-SECRET-TOKEN-VALUE"}""", (HttpStatusCode)status)));

        var result = await Source(handler).FetchAsync(Ct);

        Assert.Equal(code, result.Error!.Code);
        Assert.Equal(retryable, result.Error.Retryable);
        Assert.DoesNotContain("sk-ant", result.Error.SafeMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("details", result.Error.SafeMessage, StringComparison.Ordinal); // the reply text is never shown
    }

    [Fact]
    public async Task NoNetworkIsRetryableAndSaysNothingAboutWhy()
    {
        var handler = new Handler((_, _) => throw new HttpRequestException("Name or service not known: api.anthropic.com " + Secret));

        var result = await Source(handler).FetchAsync(Ct);

        Assert.Equal(ErrorCode.DeviceUnavailable, result.Error!.Code);
        Assert.True(result.Error.Retryable);
        Assert.DoesNotContain("anthropic.com", result.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASlowReplyIsATimeoutThatCanBeRetried()
    {
        var handler = new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Json(Good);
        });

        var result = await Source(handler, timeout: TimeSpan.FromMilliseconds(150)).FetchAsync(Ct);

        Assert.Equal(ErrorCode.Timeout, result.Error!.Code);
        Assert.True(result.Error.Retryable);
    }

    [Fact]
    public async Task ARedirectIsNeverFollowed()
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://evil.example/steal") } }));

        var result = await Source(handler).FetchAsync(Ct);

        Assert.Equal(ErrorCode.Unexpected, result.Error!.Code);
        Assert.Single(handler.Requests); // the default handler is built with redirects off; here the answer is simply refused
    }

    [Fact]
    public async Task TheLoginNeverReachesALogAMessageOrAnObjectsText()
    {
        var log = new RecordingLog();
        var credential = new UsageCredential(Secret);
        foreach (var status in new[] { 401, 429, 500, 404 })
        {
            var handler = new Handler((_, _) => Task.FromResult(Json("{}", (HttpStatusCode)status)));
            var result = await Source(handler, log: log).FetchAsync(Ct);
            Assert.DoesNotContain(Secret, result.Error!.SafeMessage, StringComparison.Ordinal);
        }

        var failing = new Handler((_, _) => throw new HttpRequestException(Secret));
        await Source(failing, log: log).FetchAsync(Ct);

        Assert.NotEmpty(log.Events);
        Assert.All(log.Events, e => Assert.DoesNotContain(Secret, e.ToString(), StringComparison.Ordinal));
        Assert.All(log.Events, e => Assert.DoesNotContain("anthropic.com", e.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Secret, credential.ToString(), StringComparison.Ordinal);
        Assert.Equal("UsageCredential", credential.ToString());
    }

    [Fact]
    public async Task EveryCallAsksForTheLoginAgainAndKeepsNoCopy()
    {
        var handler = new Handler((_, _) => Task.FromResult(Json(Good)));
        var credentials = new Credentials();
        var source = Source(handler, credentials);

        await source.FetchAsync(Ct);
        await source.FetchAsync(Ct);

        Assert.Equal(2, credentials.Calls);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public void ABlankTokenCannotBeMadeIntoALogin()
    {
        Assert.Throws<ArgumentException>(() => new UsageCredential("  "));
    }

    [Fact]
    public void ThePercentIsHeldInRangeAndRoundedAwayFromZero()
    {
        Assert.Equal(0, new UsageWindow(UsageWindowKind.Weekly, -0.2, null).UsedPercent);
        Assert.Equal(100, new UsageWindow(UsageWindowKind.Weekly, 3, null).UsedPercent);
        Assert.Equal(13, new UsageWindow(UsageWindowKind.Weekly, 0.125, null).UsedPercent);
        Assert.Null(new UsageWindow(UsageWindowKind.Weekly, null, null).UsedPercent);
    }
}
