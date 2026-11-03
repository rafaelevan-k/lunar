namespace Lunar.Application.Shell.Models;

/// <summary>What the island is doing. Platform independent; the Desktop layer maps it to visuals.</summary>
public enum IslandState
{
    /// <summary>The compact pill.</summary>
    Idle,

    /// <summary>The expansion animation is running.</summary>
    Expanding,

    /// <summary>Expanded and showing its pages.</summary>
    Expanded,

    /// <summary>The collapse animation is running.</summary>
    Collapsing,

    /// <summary>Expanded while a background job (for example a conversion) is active.</summary>
    Busy,

    /// <summary>The application is exiting. Terminal: every later request is rejected.</summary>
    Closing
}

/// <summary>What asked for a transition, for the <see cref="IslandTransition"/> record and for diagnostics.</summary>
public enum IslandTrigger
{
    Toggle,
    Expand,
    Collapse,
    Escape,
    OutsideClick,
    ExpansionCompleted,
    CollapseCompleted,
    BusyChanged,
    Exit
}

/// <summary>Why a request changed nothing. Rejections are expected outcomes, never exceptions.</summary>
public enum IslandRejection
{
    None,

    /// <summary>The island is already where the request would take it.</summary>
    AlreadyThere,

    /// <summary>The request does not apply in the current state (for example an outside click while idle).</summary>
    NotAllowedInState,

    /// <summary>A guard (dialog, drag, focus, confirmation) forbids the collapse right now.</summary>
    BlockedByGuard,

    /// <summary>A completion for a transition that is no longer current (it was interrupted or already completed).</summary>
    Stale,

    /// <summary>The request was made from inside a change notification; it is refused to keep ordering deterministic.</summary>
    Reentrant,

    /// <summary>The island is closing.</summary>
    Closing,

    /// <summary>A debounced collapse was checked before its due time.</summary>
    NotDue
}

/// <summary>Conditions that must stop the island from collapsing on its own (PRD §3.1).</summary>
public sealed record IslandGuards
{
    public static IslandGuards None { get; } = new();

    /// <summary>A dialog owned by the island is open.</summary>
    public bool DialogOpen { get; init; }

    /// <summary>A drag or file drop is in progress.</summary>
    public bool DragActive { get; init; }

    /// <summary>Keyboard or assistive-technology focus is inside the island.</summary>
    public bool FocusInside { get; init; }

    /// <summary>An action is waiting for the user's confirmation (for example a conversion).</summary>
    public bool ConfirmationPending { get; init; }

    /// <summary>Modal conditions stop every collapse, including an explicit one.</summary>
    public bool BlocksExplicitCollapse => DialogOpen || ConfirmationPending;

    /// <summary>An outside click is the weakest request, so every guard stops it.</summary>
    public bool BlocksOutsideClick => DialogOpen || DragActive || FocusInside || ConfirmationPending;
}

/// <summary>An accepted change of <see cref="IslandState"/>.</summary>
/// <param name="TransitionId">
/// Identifies the animation the new state starts (zero when none). The view reports completion with this id, so a
/// completion that belongs to an interrupted animation is recognised and ignored.
/// </param>
public sealed record IslandTransition(IslandState From, IslandState To, IslandTrigger Trigger, long TransitionId);

/// <summary>Outcome of one request: accepted (possibly with no state change) or rejected with a reason.</summary>
public readonly record struct IslandRequestResult(bool Accepted, IslandRejection Rejection, IslandTransition? Transition)
{
    public static IslandRequestResult Changed(IslandTransition transition) => new(true, IslandRejection.None, transition);

    /// <summary>Accepted but no state change (for example a collapse that was scheduled, or a busy flag change).</summary>
    public static IslandRequestResult AcceptedNoChange { get; } = new(true, IslandRejection.None, null);

    public static IslandRequestResult Rejected(IslandRejection reason) => new(false, reason, null);
}

/// <summary>Tunable timing of the island state machine.</summary>
public sealed record IslandStateOptions
{
    public static IslandStateOptions Default { get; } = new();

    /// <summary>Wait after an outside click before the island collapses (PRD §3.1).</summary>
    public TimeSpan OutsideClickDelay { get; init; } = TimeSpan.FromMilliseconds(300);
}
