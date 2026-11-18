using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Translate;
using Lunar.Application.Translate;
using Lunar.Domain.Shared;

namespace Lunar.Infrastructure.Translate;

/// <summary>
/// <see cref="ITextTranslator"/> through Microsoft Azure AI Translator, Text API v3.0 (ADR-0028). Requests go only to the documented global host over
/// HTTPS, with the user's key in <c>Ocp-Apim-Subscription-Key</c> and, for a regional resource, its region in <c>Ocp-Apim-Subscription-Region</c>.
/// Texts are sent in batches within the service's limits (1,000 elements and 50,000 characters per request; Lunar keeps 40,000 to leave room for
/// JSON). Replies are bounded and parsed defensively. Logs carry fixed events with counts only, never text, key or address parameters.
/// </summary>
public sealed class AzureTextTranslator : ITextTranslator, IDisposable
{
    public const string DefaultAddress = "https://api.cognitive.microsofttranslator.com/translate";

    internal const int MaxElements = 1000;
    internal const int MaxCharacters = 40_000;
    private const string Component = "Translate";
    private const long MaxReplyBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _client;
    private readonly Uri _address;
    private readonly IDiagnosticLog? _log;
    private readonly TimeProvider _time;
    private readonly TimeSpan _timeout;

    public AzureTextTranslator(IDiagnosticLog? log = null)
        : this(handler: null, DefaultAddress, log, TimeProvider.System, DefaultTimeout)
    {
    }

    internal AzureTextTranslator(HttpMessageHandler? handler, string address, IDiagnosticLog? log, TimeProvider time, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(time);
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query))
            throw new ArgumentException("The translator address must be an absolute https address without credentials or query.", nameof(address));

        _address = uri;
        _log = log;
        _time = time;
        _timeout = timeout;
        _client = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10)
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    /// <summary>The service's code for a language.</summary>
    internal static string Code(TranslationLanguage language) => language switch
    {
        TranslationLanguage.English => "en",
        TranslationLanguage.Indonesian => "id",
        TranslationLanguage.Japanese => "ja",
        _ => throw new ArgumentOutOfRangeException(nameof(language))
    };

    public async Task<Result<TranslationReply>> TranslateAsync(IReadOnlyList<string> texts, TranslationLanguage target, TranslatorCredential credential, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(credential);
        if (ct.IsCancellationRequested)
            return Result.Cancelled<TranslationReply>();
        if (texts.Count == 0)
            return Result.Success(TranslationReply.Of([]));
        if (TranslationKeyRules.ValidateKey(credential.Key).IsFailure || TranslationKeyRules.ValidateRegion(credential.Region) is not { IsSuccess: true } region)
            return Result.Success(TranslationReply.Failed(TranslationProblem.KeyRefused));

        var results = new List<TranslatedText>(texts.Count);
        foreach (var batch in Batches(texts))
        {
            var reply = await SendAsync(batch, Code(target), credential.Key, region.Value, ct).ConfigureAwait(false);
            if (reply.IsCancelled)
                return Result.Cancelled<TranslationReply>();
            if (!reply.Value.IsSuccess)
                return reply;
            results.AddRange(reply.Value.Translations!);
        }

        _log?.Write(new DiagnosticEvent(DiagnosticLevel.Information, "translate.done", Component, "translate"));
        return Result.Success(TranslationReply.Of(results));
    }

    public void Dispose() => _client.Dispose();

    /// <summary>Consecutive groups within the per-request limits. A single text longer than the limit is cut to it (screen blocks never are).</summary>
    internal static IEnumerable<IReadOnlyList<string>> Batches(IReadOnlyList<string> texts)
    {
        var batch = new List<string>();
        var characters = 0;
        foreach (var raw in texts)
        {
            var text = raw.Length > MaxCharacters ? raw[..MaxCharacters] : raw;
            if (batch.Count > 0 && (batch.Count == MaxElements || characters + text.Length > MaxCharacters))
            {
                yield return batch;
                batch = [];
                characters = 0;
            }

            batch.Add(text);
            characters += text.Length;
        }

        if (batch.Count > 0)
            yield return batch;
    }

    private async Task<Result<TranslationReply>> SendAsync(IReadOnlyList<string> batch, string to, string key, string? region, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(_timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            var body = new JsonArray([.. batch.Select(t => (JsonNode)new JsonObject { ["Text"] = t })]);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_address + "?api-version=3.0&to=" + to))
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", key);
            if (region is not null)
                request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Region", region);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Lunar", "1"));

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Result.Success(TranslationReply.Failed(Refused(response.StatusCode)));
            if (response.Content.Headers.ContentLength is > MaxReplyBytes)
                return Result.Success(TranslationReply.Failed(NotUnderstood()));

            var bytes = await ReadBoundedAsync(response, linked.Token).ConfigureAwait(false);
            var parsed = bytes is null ? null : Parse(bytes, batch.Count);
            return Result.Success(parsed is null ? TranslationReply.Failed(NotUnderstood()) : TranslationReply.Of(parsed));
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested)
                return Result.Cancelled<TranslationReply>();
            Log(DiagnosticLevel.Warning, "translate.request.timeout", ErrorCode.Timeout);
            return Result.Success(TranslationReply.Failed(TranslationProblem.NoNetwork));
        }
        catch (HttpRequestException ex)
        {
            Log(DiagnosticLevel.Warning, "translate.request.failed", ErrorCode.DeviceUnavailable, ex.GetType());
            return Result.Success(TranslationReply.Failed(TranslationProblem.NoNetwork));
        }
    }

    /// <summary>One translation per input, in order, or null for a reply that does not match.</summary>
    internal static IReadOnlyList<TranslatedText>? Parse(byte[] json, int expected)
    {
        try
        {
            if (JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 16 }) is not JsonArray items || items.Count != expected)
                return null;
            var list = new List<TranslatedText>(expected);
            foreach (var item in items)
            {
                if (item?["translations"] is not JsonArray { Count: > 0 } translations || translations[0]?["text"] is not JsonValue textValue
                    || !textValue.TryGetValue<string>(out var text))
                    return null;
                string? detected = null;
                if (item["detectedLanguage"]?["language"] is JsonValue languageValue && languageValue.TryGetValue<string>(out var language))
                    detected = language;
                list.Add(new TranslatedText(text, detected));
            }

            return list;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private TranslationProblem Refused(HttpStatusCode status)
    {
        var problem = (int)status switch
        {
            401 => TranslationProblem.KeyRefused,
            403 => TranslationProblem.FreeLimitReached, // Microsoft: "often indicates that you used all the free translations"
            429 => TranslationProblem.RateLimited,
            >= 500 => TranslationProblem.NoNetwork,
            _ => TranslationProblem.ProviderError
        };
        Log(DiagnosticLevel.Warning, "translate.request.refused", problem == TranslationProblem.KeyRefused ? ErrorCode.AccessDenied : ErrorCode.DeviceUnavailable);
        return problem;
    }

    private TranslationProblem NotUnderstood()
    {
        Log(DiagnosticLevel.Warning, "translate.reply.unusable", ErrorCode.InvalidInput);
        return TranslationProblem.ProviderError;
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxReplyBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private void Log(DiagnosticLevel level, string eventId, ErrorCode code, Type? exception = null) =>
        _log?.Write(new DiagnosticEvent(level, eventId, Component, "translate", code, ExceptionType: exception));
}
