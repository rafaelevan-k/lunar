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
    byte[] Artwork,
    bool CanToggle = false)
{
    /// <summary>Content equality (an artwork buffer compares by its bytes), used to tell a real change from a repeat.</summary>
    public bool SameAs(MediaSessionReading? other) =>
        other is not null
        && Id == other.Id && AppName == other.AppName && Title == other.Title && Artist == other.Artist
        && Status == other.Status && Start == other.Start && End == other.End && Position == other.Position
        && PositionStamp == other.PositionStamp && Rate.Equals(other.Rate)
        && CanPlay == other.CanPlay && CanPause == other.CanPause && CanNext == other.CanNext
        && CanPrevious == other.CanPrevious && CanSeek == other.CanSeek && CanToggle == other.CanToggle && IsCurrent == other.IsCurrent
        && (ReferenceEquals(Artwork, other.Artwork) || Artwork.AsSpan().SequenceEqual(other.Artwork));
}

/// <summary>What a transport command asks Windows to do. Chosen by <see cref="MediaCommandPlanner"/> from what the session advertises.</summary>
internal enum GsmtcAction
{
    Play,
    Pause,
    Toggle,
    Next,
    Previous,

    /// <summary>Move to a position on the timeline of the player (its start plus the position).</summary>
    Seek
}

/// <summary>How a command ended at the player.</summary>
internal enum GsmtcCommandResult
{
    /// <summary>The player accepted the command.</summary>
    Accepted,

    /// <summary>Windows delivered it but the player did not accept it.</summary>
    Refused,

    /// <summary>The session is not there any more.</summary>
    SessionGone
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
    /// Sends one transport command to the session with this id (as listed by the last read) and reports how it ended.
    /// <paramref name="target"/> is the timeline position of a <see cref="GsmtcAction.Seek"/> and ignored otherwise.
    /// Throws <see cref="MediaException"/> on failure.
    /// </summary>
    Task<GsmtcCommandResult> SendAsync(string sessionId, GsmtcAction action, TimeSpan target, CancellationToken ct);

    /// <summary>
    /// Raised on an arbitrary thread when the session list, the current session, or anything about a session (its media,
    /// playback or timeline) changed. It carries no data: the subscriber reads again.
    /// </summary>
    event Action? Changed;
}
