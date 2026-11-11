using Lunar.Domain.Shared;
using Lunar.Domain.Updates;

namespace Lunar.Application.Abstractions.Updates;

/// <summary>A published release that Lunar could install (ADR-0025). The digest is the SHA-256 the release itself reports for the installer.</summary>
public sealed record ReleaseInfo(AppVersion Version, Uri InstallerAddress, string Sha256, long Size);

/// <summary>Where Lunar learns of new versions (ADR-0025): the latest release of its GitHub repository.</summary>
public interface IUpdateFeed
{
    /// <summary>
    /// The latest published release, or null when there is none Lunar can use (no release, a draft or prerelease, no installer, no digest).
    /// No network or a refusal is a retryable failure.
    /// </summary>
    Task<Result<ReleaseInfo?>> GetLatestAsync(CancellationToken ct);
}

/// <summary>Downloads a release's installer, proves it is the one the release names and hands it to Windows Installer (ADR-0025).</summary>
public interface IUpdateInstaller
{
    /// <summary>
    /// Downloads the installer to Lunar's own folder and checks its size and SHA-256 against <paramref name="release"/>. Returns a handle to the
    /// checked file; on any failure or cancellation the file is gone. <paramref name="progress"/> gets 0 to 1.
    /// </summary>
    Task<Result<VerifiedInstaller>> DownloadAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken ct);

    /// <summary>Starts Windows Installer on a checked file. Lunar must exit right after a success, so its files can be replaced.</summary>
    Result StartInstall(VerifiedInstaller installer);

    /// <summary>Deletes installers left from an earlier update. Never throws; a file still in use is left for next time.</summary>
    void RemoveLeftovers();
}

/// <summary>An installer whose size and SHA-256 matched its release. Only <see cref="IUpdateInstaller"/> creates one. The path is never logged.</summary>
public sealed class VerifiedInstaller
{
    public VerifiedInstaller(string path, AppVersion version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
        Version = version;
    }

    public string Path { get; }

    public AppVersion Version { get; }
}
