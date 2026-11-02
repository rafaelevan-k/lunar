namespace Lunar.Application.Media.Models;

/// <summary>
/// Immutable view of one media session. Artwork is a bounded byte buffer (empty when absent) so no stream
/// ownership crosses the port. Seeking is only meaningful with a finite <see cref="Duration"/> and
/// <see cref="MediaCapabilities.CanSeek"/>.
/// </summary>
public sealed record MediaSessionSnapshot
{
    public const int MaxArtworkBytes = 2 * 1024 * 1024;

    public MediaSessionSnapshot(
        string id,
        string? appName,
        string? title,
        string? artist,
        MediaPlaybackStatus status,
        TimeSpan? position,
        TimeSpan? duration,
        MediaCapabilities capabilities,
        bool isCurrent,
        ReadOnlyMemory<byte> artwork)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown playback status.");
        if (position is { } p && p < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(position), position, "Position cannot be negative.");
        if (duration is { } d && d < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "Duration cannot be negative.");
        if (artwork.Length > MaxArtworkBytes)
            throw new ArgumentException($"Artwork exceeds {MaxArtworkBytes} bytes.", nameof(artwork));

        Id = id;
        AppName = appName;
        Title = title;
        Artist = artist;
        Status = status;
        Position = position;
        Duration = duration;
        Capabilities = capabilities;
        IsCurrent = isCurrent;
        Artwork = artwork;
    }

    public string Id { get; }

    public string? AppName { get; }

    public string? Title { get; }

    public string? Artist { get; }

    public MediaPlaybackStatus Status { get; }

    public TimeSpan? Position { get; }

    public TimeSpan? Duration { get; }

    public MediaCapabilities Capabilities { get; }

    public bool IsCurrent { get; }

    public ReadOnlyMemory<byte> Artwork { get; }

    /// <summary>True when the timeline is finite and the session reports seek support.</summary>
    public bool CanSeek => Capabilities.CanSeek && Duration is { } d && d > TimeSpan.Zero;
}
