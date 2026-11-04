using Lunar.Application.Media.Models;

namespace Lunar.Windows.Media;

/// <summary>
/// Decides, from what a session advertises right now, what a command should ask Windows to do, or that it cannot be done.
/// Pure: no I/O, so every status and capability combination is tested without a player.
/// </summary>
internal static class MediaCommandPlanner
{
    /// <summary>
    /// Play/pause pauses a playing session and plays any stopped or paused one, using the session's own play or pause
    /// control, or its toggle when that is all it offers. A session in the middle of changing track is not asked anything:
    /// whether it is playing is not known. Returns null when the session does not allow the command.
    /// </summary>
    public static GsmtcAction? Plan(MediaSessionReading session, MediaCommand command)
    {
        ArgumentNullException.ThrowIfNull(session);
        return command switch
        {
            MediaCommand.PlayPause => PlanPlayPause(session),
            MediaCommand.Next => session.CanNext ? GsmtcAction.Next : null,
            MediaCommand.Previous => session.CanPrevious ? GsmtcAction.Previous : null,
            _ => null
        };
    }

    private static GsmtcAction? PlanPlayPause(MediaSessionReading session) => session.Status switch
    {
        GsmtcStatus.Playing => session.CanPause ? GsmtcAction.Pause : session.CanToggle ? GsmtcAction.Toggle : null,
        GsmtcStatus.Paused or GsmtcStatus.Stopped or GsmtcStatus.Closed or GsmtcStatus.Opened =>
            session.CanPlay ? GsmtcAction.Play : session.CanToggle ? GsmtcAction.Toggle : null,
        _ => null
    };
}
