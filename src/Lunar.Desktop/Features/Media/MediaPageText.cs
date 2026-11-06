using System.Globalization;
using Lunar.Application.Media.Models;
using Lunar.Desktop.Resources;
using Lunar.Domain.Shared;

namespace Lunar.Desktop.Features.MediaControls;

/// <summary>
/// The words and formats of the Media page (FE-17), kept apart from the view model so each state's text is tested on its own.
/// Pure. Messages never contain a title, artist or app name: those are shown as data, never put into a sentence that could
/// be logged or announced as an error.
/// </summary>
internal static class MediaPageText
{
    public static string UnknownTitle => Strings.Get("Media.UnknownTitle");
    public static string UnknownArtist => Strings.Get("Media.UnknownArtist");
    public static string UnknownSource => Strings.Get("Media.UnknownSource");

    public static string NoSessionTitle => Strings.Get("Media.NoSessionTitle");
    public static string NoSessionHint => Strings.Get("Media.NoSessionHint");

    public static string LiveNote => Strings.Get("Media.LiveNote");
    public static string NotSeekableNote => Strings.Get("Media.NotSeekableNote");

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
        MediaPlaybackStatus.Playing => Strings.Get("Media.Playing"),
        MediaPlaybackStatus.Paused => Strings.Get("Media.Paused"),
        MediaPlaybackStatus.Stopped => Strings.Get("Media.Stopped"),
        _ => string.Empty
    };

    /// <summary>"Spotify · Playing", or just the source when there is no state to say.</summary>
    public static string SourceLine(string source, string status) => status.Length == 0 ? source : Strings.Format("Media.SourceLine", source, status);

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
        ErrorCode.Unsupported => Strings.Get("Media.UnavailableUnsupported"),
        ErrorCode.AccessDenied => Strings.Get("Media.UnavailableDenied"),
        _ => Strings.Get("Media.UnavailableOther")
    };

    /// <summary>Why a play, pause, next or previous request failed.</summary>
    public static string CommandFailed(ErrorCode code) => code switch
    {
        ErrorCode.Unsupported => Strings.Get("Media.CommandUnsupported"),
        ErrorCode.InvalidInput => Strings.Get("Media.CommandClosed"),
        ErrorCode.Timeout => Strings.Get("Media.CommandTimeout"),
        _ => Strings.Get("Media.CommandOther")
    };

    /// <summary>Why a seek (moving the position) failed. The page has already gone back to the real position.</summary>
    public static string SeekFailed(ErrorCode code) => code switch
    {
        ErrorCode.Unsupported => Strings.Get("Media.SeekUnsupported"),
        ErrorCode.InvalidInput => Strings.Get("Media.SeekChanged"),
        ErrorCode.Timeout => Strings.Get("Media.CommandTimeout"),
        _ => Strings.Get("Media.SeekOther")
    };

    /// <summary>The whole session in one sentence for assistive technology.</summary>
    public static string Summary(MediaSessionSnapshot session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var status = Status(session.Status);
        var title = Title(session.Title);
        var artist = Artist(session.Artist);
        var source = Source(session.AppName);
        return status.Length == 0
            ? Strings.Format("Media.Summary", title, artist, source)
            : Strings.Format("Media.SummaryWithStatus", title, artist, source, status);
    }
}
