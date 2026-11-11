using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Updates;
using Lunar.Domain.Shared;
using Lunar.Domain.Updates;

namespace Lunar.Windows.Updates;

/// <summary>
/// Downloads a release's MSI, proves it is the file the release names and hands it to Windows Installer (ADR-0025, BE-44).
///
/// What it guarantees:
/// - Only https, and only GitHub's own hosts: the release address on github.com and the storage hosts GitHub redirects to. Redirects are followed by
///   hand, at most five, each one checked; any other host or plain http stops the download.
/// - The file goes to a new folder under <c>%LocalAppData%\Lunar\Updates</c>. It is never larger than the release says (and never above 300 MB),
///   and its SHA-256 must equal the release's digest. Any failure, mismatch or cancellation deletes it, so nothing unchecked is left to run.
/// - The installer is started as <c>%SystemRoot%\System32\msiexec.exe /i "file" /passive /norestart LUNAR_RELAUNCH=1</c>, without a shell, and
///   only for a file inside that folder. The per-user MSI needs no administrator rights and starts Lunar again when it ends.
/// - Logs carry fixed event ids only, never an address, a path or a digest.
/// </summary>
public sealed class MsiUpdateInstaller : IUpdateInstaller, IDisposable
{
    private const string Component = "UpdateInstaller";
    private const int MaxRedirects = 5;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(15);

    /// <summary>Where a release download may come from.</summary>
    internal static readonly IReadOnlySet<string> AllowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com"
    };

    private readonly HttpClient _client;
    private readonly string _root;
    private readonly Action<ProcessStartInfo> _start;
    private readonly IDiagnosticLog? _log;
    private readonly TimeProvider _time;
    private readonly TimeSpan _timeout;

    public MsiUpdateInstaller(IDiagnosticLog? log = null)
        : this(handler: null, DefaultRoot, StartProcess, log, TimeProvider.System, DefaultTimeout)
    {
    }

    internal MsiUpdateInstaller(HttpMessageHandler? handler, string root, Action<ProcessStartInfo> start, IDiagnosticLog? log, TimeProvider time, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(time);
        _root = Path.GetFullPath(root);
        _start = start;
        _log = log;
        _time = time;
        _timeout = timeout;
        _client = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public static string DefaultRoot { get; } = Path.Combine(
        global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.LocalApplicationData), "Lunar", "Updates");

    private static OperationError DownloadFailed => new(ErrorCode.DeviceUnavailable, "The new version could not be downloaded. Check the connection and try again.", retryable: true);

    private static OperationError NotGenuine => new(ErrorCode.InvalidInput, "The downloaded file did not match the release, so it was deleted and nothing was installed.", retryable: true);

    public async Task<Result<VerifiedInstaller>> DownloadAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (ct.IsCancellationRequested)
            return Result.Cancelled<VerifiedInstaller>();
        if (release.Size is <= 0 or > UpdateRules.MaxInstallerBytes || !UpdateRules.IsSha256Hex(release.Sha256) || !IsAllowed(release.InstallerAddress))
        {
            Log(DiagnosticLevel.Warning, "update.download.refused", ErrorCode.InvalidInput);
            return Result.Failure<VerifiedInstaller>(NotGenuine);
        }

        string folder;
        try
        {
            folder = Path.Combine(_root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log(DiagnosticLevel.Warning, "update.download.folder_failed", ErrorCode.IoFailure, ex.GetType());
            return Result.Failure<VerifiedInstaller>(new OperationError(ErrorCode.IoFailure, "Lunar could not save the new version.", retryable: true));
        }

        var file = Path.Combine(folder, "Lunar-" + release.Version + ".msi");
        using var timeout = new CancellationTokenSource(_timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var keep = false;
        try
        {
            using var response = await SendFollowingAsync(release.InstallerAddress, linked.Token).ConfigureAwait(false);
            if (response is null || !response.IsSuccessStatusCode)
            {
                Log(DiagnosticLevel.Warning, response is null ? "update.download.redirect_refused" : "update.download.refused_by_server", ErrorCode.DeviceUnavailable);
                return Result.Failure<VerifiedInstaller>(response is null ? NotGenuine : DownloadFailed);
            }

            if (response.Content.Headers.ContentLength is { } length && length != release.Size)
            {
                Log(DiagnosticLevel.Warning, "update.download.size_mismatch", ErrorCode.InvalidInput);
                return Result.Failure<VerifiedInstaller>(NotGenuine);
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long written = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false))
            await using (var target = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, linked.Token).ConfigureAwait(false)) > 0)
                {
                    written += read;
                    if (written > release.Size)
                    {
                        Log(DiagnosticLevel.Warning, "update.download.too_large", ErrorCode.InvalidInput);
                        return Result.Failure<VerifiedInstaller>(NotGenuine);
                    }

                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), linked.Token).ConfigureAwait(false);
                    progress?.Report((double)written / release.Size);
                }

                await target.FlushAsync(linked.Token).ConfigureAwait(false);
            }

            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (written != release.Size || !string.Equals(actual, release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                Log(DiagnosticLevel.Warning, "update.download.digest_mismatch", ErrorCode.InvalidInput);
                return Result.Failure<VerifiedInstaller>(NotGenuine);
            }

            keep = true;
            Log(DiagnosticLevel.Information, "update.download.verified", null);
            return Result.Success(new VerifiedInstaller(file, release.Version));
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested)
                return Result.Cancelled<VerifiedInstaller>();
            Log(DiagnosticLevel.Warning, "update.download.timeout", ErrorCode.Timeout);
            return Result.Failure<VerifiedInstaller>(new OperationError(ErrorCode.Timeout, "The download took too long. Try again.", retryable: true));
        }
        catch (HttpRequestException ex)
        {
            Log(DiagnosticLevel.Warning, "update.download.failed", ErrorCode.DeviceUnavailable, ex.GetType());
            return Result.Failure<VerifiedInstaller>(DownloadFailed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log(DiagnosticLevel.Warning, "update.download.write_failed", ErrorCode.IoFailure, ex.GetType());
            return Result.Failure<VerifiedInstaller>(new OperationError(ErrorCode.IoFailure, "Lunar could not save the new version.", retryable: true));
        }
        finally
        {
            if (!keep)
                TryDelete(folder);
        }
    }

    public Result StartInstall(VerifiedInstaller installer)
    {
        ArgumentNullException.ThrowIfNull(installer);
        var file = Path.GetFullPath(installer.Path);
        if (!IsInside(file, _root) || !file.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
        {
            Log(DiagnosticLevel.Warning, "update.install.refused", ErrorCode.InvalidInput);
            return Result.Failure(new OperationError(ErrorCode.InvalidInput, "The new version is no longer there. Try again.", retryable: true));
        }

        var msiexec = Path.Combine(global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.System), "msiexec.exe");
        var info = new ProcessStartInfo(msiexec) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(file)! };
        foreach (var argument in new[] { "/i", file, "/passive", "/norestart", "LUNAR_RELAUNCH=1" })
            info.ArgumentList.Add(argument);
        try
        {
            _start(info);
            return Result.Success();
        }
        catch (Exception ex) when (ex is global::System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Log(DiagnosticLevel.Warning, "update.install.start_failed", ErrorCode.DependencyMissing, ex.GetType());
            return Result.Failure(new OperationError(ErrorCode.DependencyMissing, "Windows Installer could not be started.", retryable: true));
        }
    }

    public void RemoveLeftovers()
    {
        try
        {
            if (!Directory.Exists(_root))
                return;
            foreach (var folder in Directory.EnumerateDirectories(_root))
                TryDelete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log(DiagnosticLevel.Information, "update.cleanup.skipped", ErrorCode.IoFailure, ex.GetType());
        }
    }

    public void Dispose() => _client.Dispose();

    internal static bool IsAllowed(Uri address) =>
        address.IsAbsoluteUri && address.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(address.UserInfo) && address.IsDefaultPort && AllowedHosts.Contains(address.Host);

    private async Task<HttpResponseMessage?> SendFollowingAsync(Uri address, CancellationToken ct)
    {
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!IsAllowed(address))
                return null;

            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.UserAgent.ParseAdd("Lunar-Updater");
            request.Headers.Accept.ParseAdd("application/octet-stream");
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode is not (HttpStatusCode.Moved or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                return response;

            var next = response.Headers.Location;
            response.Dispose();
            if (next is null)
                return null;
            address = next.IsAbsoluteUri ? next : new Uri(address, next);
        }

        return null;
    }

    private static bool IsInside(string file, string root)
    {
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log(DiagnosticLevel.Information, "update.cleanup.skipped", ErrorCode.IoFailure, ex.GetType());
        }
    }

    private void Log(DiagnosticLevel level, string eventId, ErrorCode? code, Type? exceptionType = null) =>
        _log?.Write(new DiagnosticEvent(level, eventId, Component, "Update", code, ExceptionType: exceptionType));

    private static void StartProcess(ProcessStartInfo info)
    {
        using var process = Process.Start(info);
    }
}
