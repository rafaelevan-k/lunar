namespace Lunar.Windows.Media;

/// <summary>What went wrong at the Windows media-session API, in terms the service can act on.</summary>
internal enum MediaFailure
{
    /// <summary>This Windows (or edition, for example one without the media feature pack) has no media-session API.</summary>
    NotSupported,

    /// <summary>Windows refused the call.</summary>
    AccessDenied,

    /// <summary>Anything else; carries the HRESULT for the log.</summary>
    Other
}

/// <summary>A failed media-session call. Carries only a kind and an HRESULT, never a title, artist or app name.</summary>
internal sealed class MediaException : Exception
{
    public MediaException(MediaFailure failure, int hresult, string message)
        : base(message)
    {
        Failure = failure;
        HResult = hresult;
    }

    public MediaFailure Failure { get; }
}

/// <summary>Native <c>GlobalSystemMediaTransportControlsSessionPlaybackStatus</c>.</summary>
internal enum GsmtcStatus
{
    Closed,
    Opened,
    Changing,
    Stopped,
    Playing,
    Paused
}

/// <summary>
/// Everything the service reads about one media session, as raw as Windows reports it (the position is the one Windows
/// last stamped, not yet moved forward to "now"). <see cref="Artwork"/> is empty when there is none or it is too large.
/// </summary>
internal sealed record MediaSessionReading(
    string Id,
    string? AppName,
    string? Title,
    string? Artist,
    GsmtcStatus Status,
    TimeSpan Start,
    TimeSpan End,
    TimeSpan Position,
    DateTimeOffset? PositionStamp,
    double Rate,
    bool CanPlay,
    bool CanPause,
    bool CanNext,
    bool CanPrevious,
    bool CanSeek,
    bool IsCurrent,
    byte[] Artwork)
{
    /// <summary>Content equality (an artwork buffer compares by its bytes), used to tell a real change from a repeat.</summary>
    public bool SameAs(MediaSessionReading? other) =>
        other is not null
        && Id == other.Id && AppName == other.AppName && Title == other.Title && Artist == other.Artist
        && Status == other.Status && Start == other.Start && End == other.End && Position == other.Position
        && PositionStamp == other.PositionStamp && Rate.Equals(other.Rate)
        && CanPlay == other.CanPlay && CanPause == other.CanPause && CanNext == other.CanNext
        && CanPrevious == other.CanPrevious && CanSeek == other.CanSeek && IsCurrent == other.IsCurrent
        && (ReferenceEquals(Artwork, other.Artwork) || Artwork.AsSpan().SequenceEqual(other.Artwork));
}

/// <summary>
/// The Windows media-session API (GSMTC) as the service needs it. The real implementation is <see cref="WinRtGsmtcBackend"/>;
/// tests replace it to simulate no session, several sessions, sessions that close under a read, a missing API and every
/// kind of change without touching the machine's real media.
/// </summary>
internal interface IGsmtcBackend : IDisposable
{
    /// <summary>Reads every media session (none is an empty list). Throws <see cref="MediaException"/> on failure.</summary>
    Task<IReadOnlyList<MediaSessionReading>> ReadAsync(CancellationToken ct);

    /// <summary>
    /// Raised on an arbitrary thread when the session list, the current session, or anything about a session (its media,
    /// playback or timeline) changed. It carries no data: the subscriber reads again.
    /// </summary>
    event Action? Changed;
}
