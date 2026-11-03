using Lunar.Application.Abstractions.System;
using Lunar.Application.Shell.Models;

namespace Lunar.Application.Shell.UseCases;

/// <summary>
/// The island state machine (BE-09): validates every transition, publishes accepted ones, and defines how rapid or
/// overlapping requests behave. It has no timers and no UI: time comes from <see cref="IClock"/>, and a delayed
/// collapse is a due time that the host checks with <see cref="Tick"/> (it can schedule one wake-up for
/// <see cref="AutoCollapseDueAt"/>). Not thread-safe: use it from one thread, normally the UI thread.
///
/// Rules:
/// - Idle, Expanding, Expanded, Collapsing, Busy, Closing. Busy is Expanded plus a running job; the job flag survives a
///   collapse, so an idle island can still be busy and the next expansion completes into Busy.
/// - Interruption: a Toggle during an animation reverses it at once (Expanding becomes Collapsing and the reverse),
///   so rapid clicking can never get stuck. Every new animation gets a fresh transition id; completions carry the id
///   and one that is not current is rejected as Stale, which makes completions idempotent.
/// - Explicit collapses (Toggle, Collapse, Escape) are stopped only by modal guards (dialog open, confirmation
///   pending). An outside click waits <see cref="IslandStateOptions.OutsideClickDelay"/> and is then re-checked
///   against every guard; if one holds, it is dropped.
/// - Reentrancy: a request made from inside a <see cref="Changed"/> handler is rejected as Reentrant, so listeners
///   always see changes in order and never a half-finished one.
/// - Exit moves to Closing, invalidates in-flight animations and rejects everything afterwards.
/// A rejected request changes nothing and is never an exception; only invalid arguments throw.
/// </summary>
public sealed class TransitionIsland
{
    private readonly IClock _clock;
    private readonly IslandStateOptions _options;
    private bool _notifying;
    private long _lastId;

    public TransitionIsland(IClock clock, IslandStateOptions? options = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? IslandStateOptions.Default;
        if (_options.OutsideClickDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "The outside-click delay must not be negative.");
    }

    /// <summary>Raised after every accepted state change, on the caller's thread.</summary>
    public event EventHandler<IslandTransition>? Changed;

    public IslandState State { get; private set; } = IslandState.Idle;

    /// <summary>A background job is active. It survives a collapse.</summary>
    public bool IsBusy { get; private set; }

    /// <summary>Id of the current animation; zero when none is running.</summary>
    public long TransitionId { get; private set; }

    public IslandGuards Guards { get; private set; } = IslandGuards.None;

    /// <summary>When a scheduled outside-click collapse becomes due, or null when none is pending.</summary>
    public DateTimeOffset? AutoCollapseDueAt { get; private set; }

    /// <summary>Click on the island: Idle expands, Expanded or Busy collapses, an animation is reversed.</summary>
    public IslandRequestResult Toggle()
    {
        if (Gate() is { } refused)
            return refused;

        return State switch
        {
            IslandState.Idle or IslandState.Collapsing => BeginExpand(IslandTrigger.Toggle),
            _ => TryCollapse(IslandTrigger.Toggle, Guards.BlocksExplicitCollapse)
        };
    }

    /// <summary>Expands from Idle, or reverses a running collapse.</summary>
    public IslandRequestResult Expand()
    {
        if (Gate() is { } refused)
            return refused;

        return State is IslandState.Idle or IslandState.Collapsing
            ? BeginExpand(IslandTrigger.Expand)
            : IslandRequestResult.Rejected(IslandRejection.AlreadyThere);
    }

    /// <summary>Collapses an expanded island, or reverses a running expansion.</summary>
    public IslandRequestResult Collapse() => Collapse(IslandTrigger.Collapse);

    /// <summary>Escape key: the same as <see cref="Collapse()"/>, recorded as Escape.</summary>
    public IslandRequestResult Escape() => Collapse(IslandTrigger.Escape);

    /// <summary>
    /// The user clicked outside the island. Starts (or restarts) the debounce; the collapse happens in
    /// <see cref="Tick"/> once the delay has passed and no guard holds.
    /// </summary>
    public IslandRequestResult OutsideClick()
    {
        if (Gate() is { } refused)
            return refused;
        if (State is not (IslandState.Expanded or IslandState.Busy))
            return IslandRequestResult.Rejected(IslandRejection.NotAllowedInState);

        AutoCollapseDueAt = _clock.LocalNow + _options.OutsideClickDelay;
        return IslandRequestResult.AcceptedNoChange;
    }

    /// <summary>Drops a scheduled outside-click collapse (for example the pointer came back inside).</summary>
    public bool CancelAutoCollapse()
    {
        var had = AutoCollapseDueAt is not null;
        AutoCollapseDueAt = null;
        return had;
    }

    /// <summary>Runs a scheduled collapse if it is due. Call it when the host's wake-up fires.</summary>
    public IslandRequestResult Tick()
    {
        if (Gate() is { } refused)
            return refused;
        if (AutoCollapseDueAt is not { } due)
            return IslandRequestResult.Rejected(IslandRejection.NotAllowedInState);
        if (_clock.LocalNow < due)
            return IslandRequestResult.Rejected(IslandRejection.NotDue);

        AutoCollapseDueAt = null;
        if (State is not (IslandState.Expanded or IslandState.Busy))
            return IslandRequestResult.Rejected(IslandRejection.NotAllowedInState);

        return Guards.BlocksOutsideClick
            ? IslandRequestResult.Rejected(IslandRejection.BlockedByGuard)
            : BeginCollapse(IslandTrigger.OutsideClick);
    }

    /// <summary>The view finished the expansion animation <paramref name="transitionId"/>.</summary>
    public IslandRequestResult CompleteExpansion(long transitionId)
    {
        if (Gate() is { } refused)
            return refused;
        if (State != IslandState.Expanding || transitionId != TransitionId)
            return IslandRequestResult.Rejected(IslandRejection.Stale);

        TransitionId = 0;
        return Move(IsBusy ? IslandState.Busy : IslandState.Expanded, IslandTrigger.ExpansionCompleted, 0);
    }

    /// <summary>The view finished the collapse animation <paramref name="transitionId"/>.</summary>
    public IslandRequestResult CompleteCollapse(long transitionId)
    {
        if (Gate() is { } refused)
            return refused;
        if (State != IslandState.Collapsing || transitionId != TransitionId)
            return IslandRequestResult.Rejected(IslandRejection.Stale);

        TransitionId = 0;
        return Move(IslandState.Idle, IslandTrigger.CollapseCompleted, 0);
    }

    /// <summary>
    /// Sets whether a background job is active. While expanded this switches between Expanded and Busy; in every other
    /// state only the flag changes and the state follows when the expansion completes.
    /// </summary>
    public IslandRequestResult SetBusy(bool busy)
    {
        if (Gate() is { } refused)
            return refused;
        if (IsBusy == busy)
            return IslandRequestResult.Rejected(IslandRejection.AlreadyThere);

        IsBusy = busy;
        return State switch
        {
            IslandState.Expanded when busy => Move(IslandState.Busy, IslandTrigger.BusyChanged, 0),
            IslandState.Busy when !busy => Move(IslandState.Expanded, IslandTrigger.BusyChanged, 0),
            _ => IslandRequestResult.AcceptedNoChange
        };
    }

    /// <summary>Replaces the guards. They are read when a collapse is requested or becomes due.</summary>
    public void SetGuards(IslandGuards guards)
    {
        ArgumentNullException.ThrowIfNull(guards);
        Guards = guards;
    }

    /// <summary>The application is exiting. Idempotent: the second call is rejected.</summary>
    public IslandRequestResult Exit()
    {
        if (_notifying)
            return IslandRequestResult.Rejected(IslandRejection.Reentrant);
        if (State == IslandState.Closing)
            return IslandRequestResult.Rejected(IslandRejection.Closing);

        AutoCollapseDueAt = null;
        TransitionId = 0; // an in-flight animation's completion is now stale
        return Move(IslandState.Closing, IslandTrigger.Exit, 0);
    }

    private IslandRequestResult Collapse(IslandTrigger trigger)
    {
        if (Gate() is { } refused)
            return refused;

        return State is IslandState.Idle or IslandState.Collapsing
            ? IslandRequestResult.Rejected(IslandRejection.AlreadyThere)
            : TryCollapse(trigger, Guards.BlocksExplicitCollapse);
    }

    private IslandRequestResult TryCollapse(IslandTrigger trigger, bool blocked) =>
        blocked ? IslandRequestResult.Rejected(IslandRejection.BlockedByGuard) : BeginCollapse(trigger);

    private IslandRequestResult BeginExpand(IslandTrigger trigger)
    {
        AutoCollapseDueAt = null;
        TransitionId = ++_lastId;
        return Move(IslandState.Expanding, trigger, TransitionId);
    }

    private IslandRequestResult BeginCollapse(IslandTrigger trigger)
    {
        AutoCollapseDueAt = null;
        TransitionId = ++_lastId;
        return Move(IslandState.Collapsing, trigger, TransitionId);
    }

    // Common refusals: a request from inside a notification, and anything after Exit.
    private IslandRequestResult? Gate()
    {
        if (_notifying)
            return IslandRequestResult.Rejected(IslandRejection.Reentrant);
        if (State == IslandState.Closing)
            return IslandRequestResult.Rejected(IslandRejection.Closing);
        return null;
    }

    private IslandRequestResult Move(IslandState next, IslandTrigger trigger, long transitionId)
    {
        var transition = new IslandTransition(State, next, trigger, transitionId);
        State = next;

        _notifying = true;
        try
        {
            Changed?.Invoke(this, transition);
        }
        finally
        {
            _notifying = false;
        }

        return IslandRequestResult.Changed(transition);
    }
}
