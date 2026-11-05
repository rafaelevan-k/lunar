namespace Lunar.Application.Usage.Models;

/// <summary>Where the usage monitor stands.</summary>
public enum UsageStatus
{
    /// <summary>Not running: the user has not turned the Usage page on, or turned it off. Nothing is requested.</summary>
    Off,

    /// <summary>Running, and the first reading has not arrived yet.</summary>
    Loading,

    /// <summary>The last reading worked.</summary>
    Ready,

    /// <summary>There is no token, or the service refused it. Polling is paused until the user pastes a token again or presses refresh.</summary>
    SignedOut,

    /// <summary>No network, the service is down or asked to slow down. The last reading, if any, is kept and marked by its time; Lunar retries with a growing wait.</summary>
    Unavailable,

    /// <summary>The service answered in a shape this version does not understand, so nothing is shown. Lunar retries with a growing wait.</summary>
    Unreadable
}

/// <summary>
/// What a screen shows about usage. <see cref="Snapshot"/> is the last good reading (null before the first one and after a refusal);
/// when <see cref="Status"/> is not <see cref="UsageStatus.Ready"/> it is old, and its <see cref="UsageSnapshot.FetchedAt"/> says how old.
/// <see cref="Message"/> is a plain sentence for the user and never holds a token, address or response text.
/// </summary>
public sealed record UsageState(UsageStatus Status, UsageSnapshot? Snapshot = null, string? Message = null)
{
    public static UsageState Off { get; } = new(UsageStatus.Off);
}

/// <summary>
/// A limit that had been in use has started a new period. <see cref="ResetAt"/> is when the earlier reading said it would reset (or, for an
/// early reset, when the new reading was taken); <see cref="DetectedAt"/> is when Lunar noticed, which can be later when Lunar was busy or
/// offline, so a screen can choose not to announce a very old reset.
/// </summary>
public sealed record UsageLimitReset(UsageWindowKind Kind, DateTimeOffset ResetAt, DateTimeOffset DetectedAt, int? PreviousUsedPercent);
