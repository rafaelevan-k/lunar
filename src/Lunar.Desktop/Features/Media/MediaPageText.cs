using System.Globalization;
using Lunar.Application.Media.Models;
using Lunar.Domain.Shared;

namespace Lunar.Desktop.Features.MediaControls;

/// <summary>
/// The words and formats of the Media page (FE-17), kept apart from the view model so each state's text is tested on its own.
/// Pure. Messages never contain a title, artist or app name: those are shown as data, never put into a sentence that could
/// be logged or announced as an error.
/// </summary>
internal static class MediaPageText
{
    public const string UnknownTitle = "Unknown title";
    public const string UnknownArtist = "Unknown artist";
    public const string UnknownSource = "Unknown app";

    public const string NoSessionTitle = "Nothing is playing";
    public const string NoSessionHint = "Play music or a video in any app and it shows up here.";

    public const string LiveNote = "Live or no timeline";
    public const string NotSeekableNote = "This app doesn't allow seeking";

    /// <summary>mm:ss, or hh:mm:ss from one hour on; never negative. A 100-hour value stays readable (the hours keep growing).</summary>
    public static string FormatTime(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
            value = TimeSpan.Zero;
        var hours = (long)value.TotalHours;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours:00}:{value.Minutes:00}:{value.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{value.Minutes:00}:{value.Seconds:00}");
    }

    public static string Title(string? title) => string.IsNullOrWhiteSpace(title) ? UnknownTitle : title;

    public static string Artist(string? artist) => string.IsNullOrWhiteSpace(artist) ? UnknownArtist : artist;

    public static string Source(string? appName) => string.IsNullOrWhiteSpace(appName) ? UnknownSource : appName;

    public static string Status(MediaPlaybackStatus status) => status switch
    {
        MediaPlaybackStatus.Playing => "Playing",
        MediaPlaybackStatus.Paused => "Paused",
        MediaPlaybackStatus.Stopped => "Stopped",
        _ => string.Empty
    };

    /// <summary>"Spotify · Playing", or just the source when there is no state to say.</summary>
    public static string SourceLine(string source, string status) => status.Length == 0 ? source : $"{source} · {status}";

    /// <summary>The one sentence for a finite timeline that cannot be seeked, or for no timeline at all; null when seeking works.</summary>
    public static string? TimelineNote(MediaSessionSnapshot session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Duration is not { } duration || duration <= TimeSpan.Zero)
            return LiveNote;
        return session.CanSeek ? null : NotSeekableNote;
    }

    /// <summary>Why the sessions could not be read, in words for the page.</summary>
    public static string Unavailable(ErrorCode code) => code switch
    {
        ErrorCode.Unsupported => "Media controls aren't available on this PC.",
        ErrorCode.AccessDenied => "Windows didn't allow access to what's playing.",
        _ => "Couldn't read what's playing."
    };

    /// <summary>Why a play, pause, next or previous request failed.</summary>
    public static string CommandFailed(ErrorCode code) => code switch
    {
        ErrorCode.Unsupported => "This app doesn't offer that.",
        ErrorCode.InvalidInput => "That player has closed.",
        ErrorCode.Timeout => "The player didn't answer.",
        _ => "The player didn't accept that."
    };

    /// <summary>The whole session in one sentence for assistive technology.</summary>
    public static string Summary(MediaSessionSnapshot session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var status = Status(session.Status);
        var text = $"{Title(session.Title)} by {Artist(session.Artist)}, from {Source(session.AppName)}";
        return status.Length == 0 ? text : $"{text}, {status}";
    }
}
