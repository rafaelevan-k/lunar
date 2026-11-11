using Lunar.Domain.Focus;
using Lunar.Domain.Shared;

namespace Lunar.Application.Focus;

/// <summary>What is known about Do Not Disturb during a session (ADR-0023). Only <see cref="On"/> and <see cref="AlreadyOn"/> may be told as being on.</summary>
public enum FocusModeStatus
{
    NotRequested,
    TurningOn,

    /// <summary>Lunar turned it on and Windows confirmed it.</summary>
    On,

    /// <summary>It was on before the session; Lunar leaves it alone.</summary>
    AlreadyOn,

    /// <summary>It could not be turned on; see <see cref="PomodoroSnapshot.FocusError"/>. The timer still runs.</summary>
    Unavailable
}

/// <summary>A focus session as the page and the island show it. The time left is read from <see cref="PomodoroService.Remaining"/>.</summary>
public sealed record PomodoroSnapshot(PomodoroPhase Phase, FocusModeStatus FocusStatus = FocusModeStatus.NotRequested, OperationError? FocusError = null)
{
    public static PomodoroSnapshot Initial { get; } = new(PomodoroPhase.Idle);
}
