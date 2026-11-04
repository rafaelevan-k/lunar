namespace Lunar.Application.Media.Models;

/// <summary>
/// What the media page shows: every open session, and the one that is active. <see cref="Active"/> is null when no session is
/// open. <see cref="IsUserChoice"/> is true when the user picked it, false when the policy did.
/// </summary>
public sealed record ActiveMediaState(IReadOnlyList<MediaSessionSnapshot> Sessions, MediaSessionSnapshot? Active, bool IsUserChoice)
{
    public static ActiveMediaState Empty { get; } = new([], null, false);
}
