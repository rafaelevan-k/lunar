using System.Net;
using System.Net.Http.Headers;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Usage;
using Lunar.Application.Usage.Models;
using Lunar.Domain.Shared;

namespace Lunar.Infrastructure.Usage;

/// <summary>
/// Reads the user's own Claude usage limits from Anthropic over HTTPS (ADR-0014, BE-31). The endpoint is not documented by Anthropic and is
/// what community tools use, so its address and the beta header are named constants in one place, and a reply that does not look right is
/// reported as such instead of shown.
///
/// What it guarantees:
/// - Only HTTPS, only the one address it was given, and redirects are never followed (a redirect would carry the login to another host).
/// - The login comes from <see cref="IUsageCredentialSource"/> for this one request, goes only into the Authorization header and is dropped
///   afterwards. It is never logged, put in a message, cached or written anywhere. No login means no request at all.
/// - One request, with a time limit (15 s), a size limit on the reply (64 KB) and no cookies. It sends nothing about the user or the PC
///   beyond the login and a fixed product name.
/// - Logging carries only an event id and the kind of failure, never the address, the status text, the reply or the token.
/// - Failures are typed with fixed messages: 401/403 mean sign in again, 429 and 5xx and no network mean try later (retryable), a slow reply is
///   a timeout, an unreadable reply is "not understood" and shows nothing.
/// It keeps no state: polling and reset detection belong to the monitor (BE-32). Thread-safe.
/// </summary>
public sealed class AnthropicUsageSource : IUsageSource, IDisposable
{
    /// <summary>The usage address. Undocumented by Anthropic: community-reported, may change (ADR-0014).</summary>
    public const string DefaultAddress = "https://api.anthropic.com/api/oauth/usage";

    /// <summary>The beta header the community tools send with it. Undocumented as well.</summary>
    public const string BetaHeaderValue = "oauth-2025-04-20";

    private const long MaxReplyBytes = 64 * 1024;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly IUsageCredentialSource _credentials;
    private readonly HttpClient _client;
    private readonly Uri _address;
    private readonly IDiagnosticLog? _log;
    private readonly TimeProvider _time;
    private readonly TimeSpan _timeout;

    public AnthropicUsageSource(IUsageCredentialSource credentials, IDiagnosticLog? log = null)
        : this(credentials, handler: null, DefaultAddress, log, TimeProvider.System, DefaultTimeout)
    {
    }

    internal AnthropicUsageSource(IUsageCredentialSource credentials, HttpMessageHandler? handler, string address, IDiagnosticLog? log, TimeProvider time, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(time);
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("The usage address must be an absolute https address without credentials.", nameof(address));

        _credentials = credentials;
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
            Timeout = Timeout.InfiniteTimeSpan // the limit is applied per call below, so it can tell a timeout from a cancel
        };
    }

    public async Task<Result<UsageSnapshot>> FetchAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Result.Cancelled<UsageSnapshot>();

        var credential = await _credentials.GetAsync(ct).ConfigureAwait(false);
        if (credential.IsCancelled)
            return Result.Cancelled<UsageSnapshot>();
        if (credential.IsFailure)
            return Result.Failure<UsageSnapshot>(credential.Error!); // no login: no request

        using var timeout = new CancellationTokenSource(_timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _address);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Value.Token);
            request.Headers.TryAddWithoutValidation("anthropic-beta", BetaHeaderValue);
            request.Headers.UserAgent.ParseAdd("Lunar-UsageMonitor");
            request.Headers.Accept.ParseAdd("application/json");

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Failed(response.StatusCode);
            if (response.Content.Headers.ContentLength is > MaxReplyBytes)
                return NotUnderstood("too_large");

            var bytes = await ReadBoundedAsync(response, linked.Token).ConfigureAwait(false);
            if (bytes is null)
                return NotUnderstood("too_large");

            var parsed = UsageResponseParser.Parse(bytes, _time.GetLocalNow());
            if (parsed.IsFailure)
                Log(DiagnosticLevel.Warning, "usage.reply.not_understood", ErrorCode.Unexpected);
            return parsed;
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested)
                return Result.Cancelled<UsageSnapshot>();
            Log(DiagnosticLevel.Warning, "usage.request.timeout", ErrorCode.Timeout);
            return Result.Failure<UsageSnapshot>(new OperationError(ErrorCode.Timeout, "Claude did not answer in time. Lunar will try again later.", retryable: true));
        }
        catch (HttpRequestException ex)
        {
            Log(DiagnosticLevel.Warning, "usage.request.failed", ErrorCode.DeviceUnavailable, ex.GetType());
            return Result.Failure<UsageSnapshot>(Unavailable);
        }
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxReplyBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private Result<UsageSnapshot> Failed(HttpStatusCode status)
    {
        switch ((int)status)
        {
            case 401:
            case 403:
                Log(DiagnosticLevel.Warning, "usage.request.refused", ErrorCode.AccessDenied);
                return Result.Failure<UsageSnapshot>(new OperationError(ErrorCode.AccessDenied, "Claude did not accept your login. Sign in to Claude again, then Lunar will retry."));
            case 429:
                Log(DiagnosticLevel.Warning, "usage.request.rate_limited", ErrorCode.DeviceUnavailable);
                return Result.Failure<UsageSnapshot>(new OperationError(ErrorCode.DeviceUnavailable, "Claude asked Lunar to slow down. It will try again later.", retryable: true));
            case >= 500:
                Log(DiagnosticLevel.Warning, "usage.request.server_error", ErrorCode.DeviceUnavailable);
                return Result.Failure<UsageSnapshot>(Unavailable);
            default:
                Log(DiagnosticLevel.Warning, "usage.request.unexpected_status", ErrorCode.Unexpected);
                return NotUnderstood(null);
        }
    }

    private static OperationError Unavailable { get; } =
        new(ErrorCode.DeviceUnavailable, "Lunar can't reach Claude right now. It will try again later.", retryable: true);

    private Result<UsageSnapshot> NotUnderstood(string? reason)
    {
        if (reason is not null)
            Log(DiagnosticLevel.Warning, "usage.reply." + reason, ErrorCode.Unexpected);
        return Result.Failure<UsageSnapshot>(new OperationError(ErrorCode.Unexpected, "Claude's usage reply was not in a form Lunar understands, so nothing is shown."));
    }

    private void Log(DiagnosticLevel level, string id, ErrorCode code, Type? exception = null) =>
        _log?.Write(new DiagnosticEvent(level, id, "Usage", "Fetch", code, ExceptionType: exception));

    public void Dispose() => _client.Dispose();
}
