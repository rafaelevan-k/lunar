using System.Text.Json;
using Lunar.Application.Abstractions.Updates;
using Lunar.Domain.Updates;

namespace Lunar.Infrastructure.Updates;

/// <summary>Why a release reply gave no installable release; logged as a fixed word, never with the reply itself.</summary>
internal enum ReleaseRejection
{
    None,
    NotJson,
    Draft,
    Prerelease,
    BadTag,
    NoInstaller,
    BadAddress,
    NoDigest,
    BadSize
}

/// <summary>
/// Reads the GitHub "latest release" reply (ADR-0025). It keeps only what an install needs and refuses anything that is not a plain published
/// release with a <c>Lunar.msi</c> on an https GitHub address, a SHA-256 digest and a size within the limit.
/// </summary>
internal static class GitHubReleaseParser
{
    /// <summary>The host a release asset address may name. GitHub redirects the download to its own storage hosts, which the installer checks.</summary>
    internal static readonly IReadOnlySet<string> AssetHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "github.com" };

    public static ReleaseInfo? Parse(ReadOnlySpan<byte> json, out ReleaseRejection rejection)
    {
        rejection = ReleaseRejection.None;
        JsonDocument document;
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 16 });
            document = JsonDocument.ParseValue(ref reader);
        }
        catch (JsonException)
        {
            rejection = ReleaseRejection.NotJson;
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Reject(ReleaseRejection.NotJson, out rejection);
            if (Bool(root, "draft"))
                return Reject(ReleaseRejection.Draft, out rejection);
            if (Bool(root, "prerelease"))
                return Reject(ReleaseRejection.Prerelease, out rejection);
            if (!AppVersion.TryParse(Text(root, "tag_name"), out var version))
                return Reject(ReleaseRejection.BadTag, out rejection);

            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return Reject(ReleaseRejection.NoInstaller, out rejection);

            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object
                    || !string.Equals(Text(asset, "name"), UpdateRules.InstallerAssetName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!Uri.TryCreate(Text(asset, "browser_download_url"), UriKind.Absolute, out var address)
                    || address.Scheme != Uri.UriSchemeHttps
                    || !string.IsNullOrEmpty(address.UserInfo)
                    || !address.IsDefaultPort
                    || !AssetHosts.Contains(address.Host))
                    return Reject(ReleaseRejection.BadAddress, out rejection);

                var digest = Text(asset, "digest");
                if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || !UpdateRules.IsSha256Hex(digest[7..]))
                    return Reject(ReleaseRejection.NoDigest, out rejection);

                if (!asset.TryGetProperty("size", out var sizeElement) || !sizeElement.TryGetInt64(out var size) || size <= 0 || size > UpdateRules.MaxInstallerBytes)
                    return Reject(ReleaseRejection.BadSize, out rejection);

                return new ReleaseInfo(version, address, digest[7..].ToLowerInvariant(), size);
            }

            return Reject(ReleaseRejection.NoInstaller, out rejection);
        }
    }

    private static ReleaseInfo? Reject(ReleaseRejection why, out ReleaseRejection rejection)
    {
        rejection = why;
        return null;
    }

    private static bool Bool(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
