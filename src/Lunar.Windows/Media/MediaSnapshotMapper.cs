using Lunar.Application.Media.Models;

namespace Lunar.Windows.Media;

/// <summary>
/// Turns what Windows reported about a media session into the port's <see cref="MediaSessionSnapshot"/>. Pure: no I/O and
/// no clock of its own (the moment is an argument), so every playback state, timeline shape and capability combination is
/// tested without a player.
/// </summary>
internal static class MediaSnapshotMapper
{
    /// <summary>Titles and artists longer than this are cut, so a hostile or broken app cannot make the island hold a huge string.</summary>
    internal const int MaxTextLength = 256;

    public static MediaSessionSnapshot ToSnapshot(MediaSessionReading reading, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(reading);

        var duration = DurationOf(reading);
        var capabilities = new MediaCapabilities(
            CanPlayPause: reading.CanPlay || reading.CanPause,
            CanNext: reading.CanNext,
            CanPrevious: reading.CanPrevious,
            CanSeek: reading.CanSeek);

        return new MediaSessionSnapshot(
            reading.Id,
            Clean(reading.AppName),
            Clean(reading.Title),
            Clean(reading.Artist),
            StatusOf(reading.Status),
            PositionOf(reading, duration, now),
            duration,
            capabilities,
            reading.IsCurrent,
            reading.Artwork.Length is > 0 and <= MediaSessionSnapshot.MaxArtworkBytes ? reading.Artwork : ReadOnlyMemory<byte>.Empty);
    }

    /// <summary>
    /// Playing and paused are what they say; stopped, closed and merely opened sessions are stopped; a session in the middle
    /// of changing track is unknown (it is neither), so the UI does not claim anything.
    /// </summary>
    internal static MediaPlaybackStatus StatusOf(GsmtcStatus status) => status switch
    {
        GsmtcStatus.Playing => MediaPlaybackStatus.Playing,
        GsmtcStatus.Paused => MediaPlaybackStatus.Paused,
        GsmtcStatus.Stopped or GsmtcStatus.Closed or GsmtcStatus.Opened => MediaPlaybackStatus.Stopped,
        _ => MediaPlaybackStatus.Unknown
    };

    /// <summary>
    /// The length of the media, or null when there is none to speak of: a live stream or an app that does not report a
    /// timeline has an end at or before its start, and an absurdly long end (a sentinel for "unbounded") counts as none too.
    /// </summary>
    internal static TimeSpan? DurationOf(MediaSessionReading reading)
    {
        var length = reading.End - reading.Start;
        return length > TimeSpan.Zero && length < TimeSpan.FromDays(7) ? length : null;
    }

    /// <summary>
    /// Where the media is now, counted from its start: the position Windows stamped, moved forward by the time since the
    /// stamp while it plays (at its rate), never before the start nor past the end. Null when there is no timeline.
    /// </summary>
    internal static TimeSpan? PositionOf(MediaSessionReading reading, TimeSpan? duration, DateTimeOffset now)
    {
        if (duration is not { } length)
            return null;

        var position = reading.Position - reading.Start;
        if (reading.Status == GsmtcStatus.Playing && reading.PositionStamp is { } stamp && now > stamp)
        {
            var rate = reading.Rate is > 0 and < 16 ? reading.Rate : 1.0;
            position += TimeSpan.FromTicks((long)((now - stamp).Ticks * rate));
        }

        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;
        return position > length ? length : position;
    }

    /// <summary>A friendlier name for an app's user-model id when Windows gave no display name: no ".exe" and no package suffix.</summary>
    internal static string? FriendlyAppName(string? appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId))
            return null;

        var name = appUserModelId.Trim();
        var bang = name.IndexOf('!', StringComparison.Ordinal);
        if (bang > 0)
            name = name[..bang]; // package family and app id: keep the package name
        var underscore = name.IndexOf('_', StringComparison.Ordinal);
        if (underscore > 0)
            name = name[..underscore]; // drop the publisher hash
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name.Length == 0 ? null : name;
    }

    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var trimmed = text.Trim();
        return trimmed.Length <= MaxTextLength ? trimmed : trimmed[..MaxTextLength];
    }
}
