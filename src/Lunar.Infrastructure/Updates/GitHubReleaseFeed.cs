using System.Net;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Updates;
using Lunar.Domain.Shared;

namespace Lunar.Infrastructure.Updates;

/// <summary>
/// Asks GitHub for the latest release of Lunar's repository (ADR-0025, BE-44).
///
/// What it guarantees:
/// - One https GET to the one fixed address, no redirects, no cookies, no login. It sends a fixed product name and nothing about the user or the PC.
/// - A time limit (15 s) and a size limit on the reply (256 KB).
/// - No release (404) is "nothing new", not an error. A rate limit, a server error, no network or a slow reply are retryable failures.
/// - Logs carry only an event id and the kind of failure: never the address, the reply or a version.
/// Thread-safe.
/// </summary>
public sealed class GitHubReleaseFeed : IUpdateFeed, IDisposable
{
    public const string DefaultAddress = "https://api.github.com/repos/rafaelevan-k/lunar/releases/latest";

    private const string Component = "UpdateFeed";
    private const long MaxReplyBytes = 256 * 1024;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _client;
    private readonly Uri _address;
    private readonly IDiagnosticLog? _log;
    private readonly TimeProvider _time;
    private readonly TimeSpan _timeout;

    public GitHubReleaseFeed(IDiagnosticLog? log = null)
        : this(handler: null, DefaultAddress, log, TimeProvider.System, DefaultTimeout)
    {
    }

    internal GitHubReleaseFeed(HttpMessageHandler? handler, string address, IDiagnosticLog? log, TimeProvider time, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(time);
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("The release address must be an absolute https address without credentials.", nameof(address));

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

    public async Task<Result<ReleaseInfo?>> GetLatestAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Result.Cancelled<ReleaseInfo?>();

        using var timeout = new CancellationTokenSource(_timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _address);
            request.Headers.UserAgent.ParseAdd("Lunar-Updater");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return Result.Success<ReleaseInfo?>(null); // no release yet
            if (!response.IsSuccessStatusCode)
                return Failed(response.StatusCode);
            if (response.Content.Headers.ContentLength is > MaxReplyBytes)
                return NotUnderstood();

            var bytes = await ReadBoundedAsync(response, linked.Token).ConfigureAwait(false);
            if (bytes is null)
                return NotUnderstood();

            var release = GitHubReleaseParser.Parse(bytes, out var rejection);
            if (release is null)
            {
                // A draft or prerelease is a normal "nothing to offer"; anything else is worth a line in the log.
                if (rejection is not (ReleaseRejection.Draft or ReleaseRejection.Prerelease))
                    Log(DiagnosticLevel.Warning, "update.release.unusable", ErrorCode.InvalidInput);
                return Result.Success<ReleaseInfo?>(null);
            }

            return Result.Success<ReleaseInfo?>(release);
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested)
                return Result.Cancelled<ReleaseInfo?>();
            Log(DiagnosticLevel.Warning, "update.request.timeout", ErrorCode.Timeout);
            return Result.Failure<ReleaseInfo?>(new OperationError(ErrorCode.Timeout, "GitHub did not answer in time.", retryable: true));
        }
        catch (HttpRequestException ex)
        {
            Log(DiagnosticLevel.Warning, "update.request.failed", ErrorCode.DeviceUnavailable, ex.GetType());
            return Result.Failure<ReleaseInfo?>(new OperationError(ErrorCode.DeviceUnavailable, "GitHub could not be reached.", retryable: true));
        }
    }

    public void Dispose() => _client.Dispose();

    private Result<ReleaseInfo?> Failed(HttpStatusCode status)
    {
        var code = (int)status;
        Log(DiagnosticLevel.Warning, code is 403 or 429 ? "update.request.limited" : "update.request.refused", ErrorCode.DeviceUnavailable);
        // 403/429 are GitHub's rate limits and 5xx its own trouble: both pass. Anything else is retried the next day as well.
        return Result.Failure<ReleaseInfo?>(new OperationError(ErrorCode.DeviceUnavailable, "GitHub refused the request.", retryable: true));
    }

    private Result<ReleaseInfo?> NotUnderstood()
    {
        Log(DiagnosticLevel.Warning, "update.reply.too_large", ErrorCode.InvalidInput);
        return Result.Failure<ReleaseInfo?>(new OperationError(ErrorCode.InvalidInput, "GitHub's reply was not understood.", retryable: true));
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

    private void Log(DiagnosticLevel level, string eventId, ErrorCode code, Type? exceptionType = null) =>
        _log?.Write(new DiagnosticEvent(level, eventId, Component, "Latest", code, ExceptionType: exceptionType));
}
