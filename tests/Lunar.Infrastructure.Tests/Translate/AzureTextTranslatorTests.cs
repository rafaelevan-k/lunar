using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Translate;
using Lunar.Infrastructure.Translate;
using Xunit;

namespace Lunar.Infrastructure.Tests.Translate;

// BE-48 (ADR-0028): the Azure AI Translator adapter against recorded replies. A real request needs the owner's own key and is NOT_RUN here.
public sealed class AzureTextTranslatorTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheRequestHasTheDocumentedShapeAndTheReplyIsReadInOrder()
    {
        string? body = null;
        var handler = new Handler(async (request, ct) =>
        {
            body = await request.Content!.ReadAsStringAsync(ct);
            return Json("""
                [ { "detectedLanguage": { "language": "en", "score": 1.0 }, "translations": [ { "text": "Simpan", "to": "id" } ] },
                  { "detectedLanguage": { "language": "ja", "score": 0.9 }, "translations": [ { "text": "Tutup", "to": "id" } ] } ]
                """);
        });
        using var translator = Create(handler);

        var result = await translator.TranslateAsync(["Save", "閉じる"], TranslationLanguage.Indonesian, new TranslatorCredential(Key, "southeastasia"), Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal([new TranslatedText("Simpan", "en"), new TranslatedText("Tutup", "ja")], result.Value.Translations);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&to=id", request.RequestUri!.ToString());
        Assert.Equal(Key, Assert.Single(request.Headers.GetValues("Ocp-Apim-Subscription-Key")));
        Assert.Equal("southeastasia", Assert.Single(request.Headers.GetValues("Ocp-Apim-Subscription-Region")));
        var items = (JsonArray)JsonNode.Parse(body!)!;
        Assert.Equal(["Save", "閉じる"], items.Select(i => (string)i!["Text"]!));
    }

    [Theory]
    [InlineData(TranslationLanguage.English, "en")]
    [InlineData(TranslationLanguage.Indonesian, "id")]
    [InlineData(TranslationLanguage.Japanese, "ja")]
    public async Task EachTargetHasItsCodeAndAGlobalResourceSendsNoRegion(TranslationLanguage target, string code)
    {
        var handler = new Handler((_, _) => Task.FromResult(Json("""[ { "translations": [ { "text": "x", "to": "en" } ] } ]""")));
        using var translator = Create(handler);
        await translator.TranslateAsync(["x"], target, new TranslatorCredential(Key, null), Ct);
        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("&to=" + code, request.RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.False(request.Headers.Contains("Ocp-Apim-Subscription-Region"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, TranslationProblem.KeyRefused)]
    [InlineData(HttpStatusCode.Forbidden, TranslationProblem.FreeLimitReached)]
    [InlineData(HttpStatusCode.TooManyRequests, TranslationProblem.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, TranslationProblem.NoNetwork)]
    [InlineData(HttpStatusCode.BadRequest, TranslationProblem.ProviderError)]
    public async Task RefusalsAreSaidAsWhatTheyMean(HttpStatusCode status, TranslationProblem expected)
    {
        var log = new RecordingLog();
        using var translator = Create(new Handler((_, _) => Task.FromResult(Json("""{ "error": { "code": 401000, "message": "Access denied for key 0123" } }""", status))), log);
        var result = await translator.TranslateAsync(["Private text"], TranslationLanguage.English, new TranslatorCredential(Key, null), Ct);
        Assert.Equal(expected, result.Value.Problem);
        Assert.DoesNotContain(log.Events, e => (e.EventId + e.Component + e.Operation).Contains("Private", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "translations": [] }""")]
    [InlineData("""[ { "translations": [ { "text": "a" } ] }, { "translations": [ { "text": "b" } ] } ]""")] // two for one
    [InlineData("""[ { "translations": [] } ]""")]
    [InlineData("""[ { "translations": [ { "text": 5 } ] } ]""")]
    public async Task AReplyThatDoesNotMatchIsAProviderError(string reply)
    {
        using var translator = Create(new Handler((_, _) => Task.FromResult(Json(reply))));
        var result = await translator.TranslateAsync(["one"], TranslationLanguage.English, new TranslatorCredential(Key, null), Ct);
        Assert.Equal(TranslationProblem.ProviderError, result.Value.Problem);
    }

    [Fact]
    public async Task NoNetworkTimeoutsAndCancellationAreHandled()
    {
        using var offline = Create(new Handler((_, _) => throw new HttpRequestException("no route")));
        Assert.Equal(TranslationProblem.NoNetwork, (await offline.TranslateAsync(["a"], TranslationLanguage.English, new TranslatorCredential(Key, null), Ct)).Value.Problem);

        using var slow = Create(new Handler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return Json("[]");
        }), timeout: TimeSpan.FromMilliseconds(100));
        Assert.Equal(TranslationProblem.NoNetwork, (await slow.TranslateAsync(["a"], TranslationLanguage.English, new TranslatorCredential(Key, null), Ct)).Value.Problem);

        using var cancelled = new CancellationTokenSource();
        var running = slow.TranslateAsync(["a"], TranslationLanguage.English, new TranslatorCredential(Key, null), cancelled.Token);
        cancelled.Cancel();
        Assert.True((await running).IsCancelled);
    }

    [Fact]
    public async Task ABadKeyOrRegionIsNeverSent()
    {
        var handler = new Handler((_, _) => Task.FromResult(Json("[]")));
        using var translator = Create(handler);
        Assert.Equal(TranslationProblem.KeyRefused, (await translator.TranslateAsync(["a"], TranslationLanguage.English, new TranslatorCredential("short", null), Ct)).Value.Problem);
        Assert.Equal(TranslationProblem.KeyRefused, (await translator.TranslateAsync(["a"], TranslationLanguage.English, new TranslatorCredential(Key, "evil\r\nX: 1"), Ct)).Value.Problem);
        Assert.Empty(handler.Requests);
        Assert.Equal("TranslatorCredential(redacted)", new TranslatorCredential(Key, null).ToString());
    }

    [Fact]
    public void BatchesStayWithinTheServicesLimits()
    {
        var many = Enumerable.Repeat("word", 2500).ToList();
        Assert.Equal([1000, 1000, 500], AzureTextTranslator.Batches(many).Select(b => b.Count));

        var long_ = Enumerable.Repeat(new string('a', 15_000), 5).ToList();
        var batches = AzureTextTranslator.Batches(long_).ToList();
        Assert.All(batches, b => Assert.True(b.Sum(t => t.Length) <= AzureTextTranslator.MaxCharacters));
        Assert.Equal(5, batches.Sum(b => b.Count));
    }

    [Fact]
    public async Task ManyBlocksAreSentInSeveralRequestsAndJoinedInOrder()
    {
        var handler = new Handler(async (request, ct) =>
        {
            var items = (JsonArray)JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var reply = new JsonArray([.. items.Select(i => (JsonNode)new JsonObject { ["translations"] = new JsonArray(new JsonObject { ["text"] = "T" + (string)i!["Text"]! }) })]);
            return Json(reply.ToJsonString());
        });
        using var translator = Create(handler);
        var texts = Enumerable.Range(0, 1500).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        var result = await translator.TranslateAsync(texts, TranslationLanguage.English, new TranslatorCredential(Key, null), Ct);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(texts.Select(t => "T" + t), result.Value.Translations!.Select(t => t.Text));
    }

    [Fact]
    public void OnlyAnHttpsAddressWithoutCredentialsIsAccepted()
    {
        Assert.Throws<ArgumentException>(() => new AzureTextTranslator(null, "http://api.cognitive.microsofttranslator.com/translate", null, TimeProvider.System, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(() => new AzureTextTranslator(null, "https://user:pw@example.com/translate", null, TimeProvider.System, TimeSpan.FromSeconds(1)));
    }

    private static AzureTextTranslator Create(Handler handler, RecordingLog? log = null, TimeSpan? timeout = null) =>
        new(handler, AzureTextTranslator.DefaultAddress, log, TimeProvider.System, timeout ?? TimeSpan.FromSeconds(10));

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
                Requests.Add(request);
            return await respond(request, cancellationToken);
        }
    }

    private sealed class RecordingLog : IDiagnosticLog
    {
        public List<DiagnosticEvent> Events { get; } = [];

        public void Write(DiagnosticEvent diagnosticEvent) => Events.Add(diagnosticEvent);
    }
}
