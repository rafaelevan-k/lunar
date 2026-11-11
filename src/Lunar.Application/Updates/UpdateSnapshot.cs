using Lunar.Application.Abstractions.Updates;
using Lunar.Domain.Shared;

namespace Lunar.Application.Updates;

/// <summary>Where the update check stands (ADR-0025).</summary>
public enum UpdatePhase
{
    /// <summary>Nothing newer is known (or the check is off).</summary>
    Idle,

    /// <summary>A newer release is known and nothing is being done with it.</summary>
    Available,

    /// <summary>Its installer is being downloaded and checked.</summary>
    Downloading,

    /// <summary>Windows Installer was started; Lunar is about to exit.</summary>
    Installing,

    /// <summary>The download, the check or the start of the installer failed. <see cref="UpdateSnapshot.Error"/> says why; Try again is possible.</summary>
    Failed
}

/// <summary>
/// One immutable state of the update check. <see cref="ShowPrompt"/> is false after Later, until the next check finds the release again.
/// A failed check is never shown: it is logged and the next daily check tries again.
/// </summary>
public sealed record UpdateSnapshot(UpdatePhase Phase, ReleaseInfo? Release, double Progress, OperationError? Error, bool Postponed)
{
    public static UpdateSnapshot Initial { get; } = new(UpdatePhase.Idle, null, 0, null, false);

    /// <summary>The user should see the new-version prompt now.</summary>
    public bool ShowPrompt => Release is not null && Phase is not UpdatePhase.Idle && !(Phase == UpdatePhase.Available && Postponed);
}
