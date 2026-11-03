using Lunar.Application.Shell.Models;
using Lunar.Application.Shell.UseCases;
using Lunar.Application.Tests.Fakes;
using Xunit;

namespace Lunar.Application.Tests.Shell;

public sealed class TransitionIslandTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 3, 9, 0, 0, TimeSpan.FromHours(7));

    private sealed record Rig(TransitionIsland Island, FakeClock Clock, List<IslandTransition> Seen);

    private static Rig Create(IslandStateOptions? options = null)
    {
        var clock = new FakeClock(Start);
        var island = new TransitionIsland(clock, options);
        var seen = new List<IslandTransition>();
        island.Changed += (_, t) => seen.Add(t);
        return new Rig(island, clock, seen);
    }

    // Brings the island to Expanded (or Busy) the way the view would.
    private static Rig Expanded(bool busy = false)
    {
        var rig = Create();
        if (busy)
            rig.Island.SetBusy(true);
        var id = rig.Island.Toggle().Transition!.TransitionId;
        rig.Island.CompleteExpansion(id);
        rig.Seen.Clear();
        return rig;
    }

    // ---- basic transitions -------------------------------------------------------------------------------------

    [Fact]
    public void StartsIdleAndTheFirstToggleStartsExpanding()
    {
        var rig = Create();
        Assert.Equal(IslandState.Idle, rig.Island.State);

        var result = rig.Island.Toggle();

        Assert.True(result.Accepted);
        Assert.Equal(new IslandTransition(IslandState.Idle, IslandState.Expanding, IslandTrigger.Toggle, 1), result.Transition);
        Assert.Equal(IslandState.Expanding, rig.Island.State);
        Assert.Equal(1, rig.Island.TransitionId);
    }

    [Fact]
    public void ACompletionWithTheCurrentIdFinishesTheAnimation()
    {
        var rig = Create();
        var id = rig.Island.Toggle().Transition!.TransitionId;

        var done = rig.Island.CompleteExpansion(id);

        Assert.Equal(IslandState.Expanded, rig.Island.State);
        Assert.Equal(IslandState.Expanded, done.Transition!.To);
        Assert.Equal(0, rig.Island.TransitionId);

        var collapse = rig.Island.Toggle();
        Assert.Equal(IslandState.Collapsing, rig.Island.State);
        rig.Island.CompleteCollapse(collapse.Transition!.TransitionId);
        Assert.Equal(IslandState.Idle, rig.Island.State);
    }

    [Fact]
    public void ExplicitExpandAndCollapseRespectTheCurrentState()
    {
        var rig = Create();
        Assert.Equal(IslandRejection.AlreadyThere, rig.Island.Collapse().Rejection);
        Assert.True(rig.Island.Expand().Accepted);
        Assert.Equal(IslandRejection.AlreadyThere, rig.Island.Expand().Rejection);

        rig.Island.CompleteExpansion(rig.Island.TransitionId);
        Assert.Equal(IslandRejection.AlreadyThere, rig.Island.Expand().Rejection);
        Assert.True(rig.Island.Escape().Accepted);
        Assert.Equal(IslandTrigger.Escape, rig.Seen[^1].Trigger);
        Assert.Equal(IslandRejection.AlreadyThere, rig.Island.Collapse().Rejection); // already collapsing
    }

    // ---- rapid expand / collapse and interruption --------------------------------------------------------------

    [Fact]
    public void ToggleDuringExpansionReversesItAtOnce()
    {
        var rig = Create();
        var first = rig.Island.Toggle().Transition!;

        var second = rig.Island.Toggle().Transition!;

        Assert.Equal(IslandState.Collapsing, rig.Island.State);
        Assert.Equal(IslandState.Expanding, second.From);
        Assert.NotEqual(first.TransitionId, second.TransitionId);
    }

    [Fact]
    public void ToggleDuringCollapseReversesItAtOnce()
    {
        var rig = Expanded();
        rig.Island.Toggle();
        Assert.Equal(IslandState.Collapsing, rig.Island.State);

        rig.Island.Toggle();

        Assert.Equal(IslandState.Expanding, rig.Island.State);
    }

    [Fact]
    public void ACompletionFromAnInterruptedAnimationIsStaleAndChangesNothing()
    {
        var rig = Create();
        var expandId = rig.Island.Toggle().Transition!.TransitionId;
        var collapseId = rig.Island.Toggle().Transition!.TransitionId; // interrupts the expansion
        rig.Seen.Clear();

        var stale = rig.Island.CompleteExpansion(expandId);

        Assert.False(stale.Accepted);
        Assert.Equal(IslandRejection.Stale, stale.Rejection);
        Assert.Equal(IslandState.Collapsing, rig.Island.State);
        Assert.Empty(rig.Seen);

        Assert.Equal(IslandRejection.Stale, rig.Island.CompleteExpansion(collapseId).Rejection); // wrong kind of completion
        Assert.True(rig.Island.CompleteCollapse(collapseId).Accepted);
        Assert.Equal(IslandState.Idle, rig.Island.State);
    }

    [Fact]
    public void CompletionsAreIdempotent()
    {
        var rig = Create();
        var id = rig.Island.Toggle().Transition!.TransitionId;

        Assert.True(rig.Island.CompleteExpansion(id).Accepted);
        var again = rig.Island.CompleteExpansion(id);

        Assert.Equal(IslandRejection.Stale, again.Rejection);
        Assert.Equal(IslandState.Expanded, rig.Island.State);
    }

    [Fact]
    public void ACompletionWhenNothingIsAnimatingIsStale()
    {
        var rig = Create();
        Assert.Equal(IslandRejection.Stale, rig.Island.CompleteExpansion(0).Rejection);
        Assert.Equal(IslandRejection.Stale, rig.Island.CompleteCollapse(1).Rejection);
        Assert.Equal(IslandState.Idle, rig.Island.State);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(100)]
    [InlineData(1001)]
    public void RapidTogglesAlwaysLandOnTheRightStateAndEveryChangeIsValid(int clicks)
    {
        var rig = Create();

        for (var i = 0; i < clicks; i++)
            Assert.True(rig.Island.Toggle().Accepted);

        // An odd number of clicks ends heading to expanded, an even number ends heading to idle.
        Assert.Equal(clicks % 2 == 1 ? IslandState.Expanding : IslandState.Collapsing, rig.Island.State);
        Assert.Equal(clicks, rig.Seen.Count);
        var expected = IslandState.Idle;
        foreach (var t in rig.Seen)
        {
            Assert.Equal(expected, t.From); // the chain is unbroken
            expected = t.To;
        }

        // Only the last animation's completion counts.
        var last = rig.Seen[^1];
        Assert.False(rig.Island.CompleteExpansion(last.TransitionId - 1).Accepted);
        var complete = clicks % 2 == 1 ? rig.Island.CompleteExpansion(last.TransitionId) : rig.Island.CompleteCollapse(last.TransitionId);
        Assert.True(complete.Accepted);
        Assert.Equal(clicks % 2 == 1 ? IslandState.Expanded : IslandState.Idle, rig.Island.State);
    }

    [Fact]
    public void TransitionIdsNeverRepeat()
    {
        var rig = Create();
        for (var i = 0; i < 50; i++)
            rig.Island.Toggle();

        var ids = rig.Seen.Select(t => t.TransitionId).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.True(id > 0));
    }

    // ---- invalid transitions -----------------------------------------------------------------------------------

    [Fact]
    public void OutsideClickWhileIdleOrAnimatingIsNotAllowed()
    {
        var rig = Create();
        Assert.Equal(IslandRejection.NotAllowedInState, rig.Island.OutsideClick().Rejection);

        rig.Island.Toggle(); // Expanding
        Assert.Equal(IslandRejection.NotAllowedInState, rig.Island.OutsideClick().Rejection);
        Assert.Null(rig.Island.AutoCollapseDueAt);
    }

    [Fact]
    public void TickWithNothingScheduledIsNotAllowed()
    {
        var rig = Expanded();
        Assert.Equal(IslandRejection.NotAllowedInState, rig.Island.Tick().Rejection);
        Assert.Empty(rig.Seen);
    }

    [Fact]
    public void RejectedRequestsNeverChangeStateOrRaiseEvents()
    {
        var rig = Create();

        rig.Island.Collapse();
        rig.Island.Escape();
        rig.Island.CompleteCollapse(5);
        rig.Island.OutsideClick();
        rig.Island.Tick();

        Assert.Equal(IslandState.Idle, rig.Island.State);
        Assert.Empty(rig.Seen);
    }

    // ---- busy ---------------------------------------------------------------------------------------------------

    [Fact]
    public void BusyWhileExpandedSwitchesBetweenExpandedAndBusy()
    {
        var rig = Expanded();

        Assert.Equal(IslandState.Busy, rig.Island.SetBusy(true).Transition!.To);
        Assert.Equal(IslandRejection.AlreadyThere, rig.Island.SetBusy(true).Rejection);
        Assert.Equal(IslandState.Expanded, rig.Island.SetBusy(false).Transition!.To);
    }

    [Fact]
    public void BusyFlagSurvivesCollapseAndTheNextExpansionCompletesIntoBusy()
    {
        var rig = Expanded(busy: true);
        Assert.Equal(IslandState.Busy, rig.Island.State);

        var collapse = rig.Island.Toggle().Transition!;
        rig.Island.CompleteCollapse(collapse.TransitionId);
        Assert.Equal(IslandState.Idle, rig.Island.State);
        Assert.True(rig.Island.IsBusy);

        var expand = rig.Island.Toggle().Transition!;
        Assert.Equal(IslandState.Busy, rig.Island.CompleteExpansion(expand.TransitionId).Transition!.To);
    }

    [Fact]
    public void BusyChangeDuringAnAnimationOnlyChangesTheFlag()
    {
        var rig = Create();
        var id = rig.Island.Toggle().Transition!.TransitionId;

        var result = rig.Island.SetBusy(true);

        Assert.True(result.Accepted);
        Assert.Null(result.Transition);
        Assert.Equal(IslandState.Expanding, rig.Island.State);
        Assert.Equal(IslandState.Busy, rig.Island.CompleteExpansion(id).Transition!.To);
    }

    // ---- guards and the outside-click debounce -----------------------------------------------------------------

    [Fact]
    public void OutsideClickCollapsesOnlyAfterTheDebounceDelay()
    {
        var rig = Expanded();

        var scheduled = rig.Island.OutsideClick();
        Assert.True(scheduled.Accepted);
        Assert.Null(scheduled.Transition);
        Assert.Equal(Start + TimeSpan.FromMilliseconds(300), rig.Island.AutoCollapseDueAt);

        rig.Clock.LocalNow = Start + TimeSpan.FromMilliseconds(299);
        Assert.Equal(IslandRejection.NotDue, rig.Island.Tick().Rejection);
        Assert.Equal(IslandState.Expanded, rig.Island.State);

        rig.Clock.LocalNow = Start + TimeSpan.FromMilliseconds(300);
        var due = rig.Island.Tick();
        Assert.Equal(IslandState.Collapsing, due.Transition!.To);
        Assert.Equal(IslandTrigger.OutsideClick, due.Transition.Trigger);
        Assert.Null(rig.Island.AutoCollapseDueAt);
    }

    [Fact]
    public void AnotherOutsideClickRestartsTheDebounce()
    {
        var rig = Expanded();
        rig.Island.OutsideClick();
        rig.Clock.LocalNow = Start + TimeSpan.FromMilliseconds(200);
        rig.Island.OutsideClick();

        Assert.Equal(Start + TimeSpan.FromMilliseconds(500), rig.Island.AutoCollapseDueAt);
    }

    [Fact]
    public void CancelDropsAScheduledCollapse()
    {
        var rig = Expanded();
        rig.Island.OutsideClick();

        Assert.True(rig.Island.CancelAutoCollapse());
        Assert.False(rig.Island.CancelAutoCollapse());
        rig.Clock.LocalNow = Start + TimeSpan.FromSeconds(5);
        Assert.Equal(IslandRejection.NotAllowedInState, rig.Island.Tick().Rejection);
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void EveryGuardStopsADueOutsideClickAndTheRequestIsDropped(bool dialog, bool drag, bool focus, bool confirm)
    {
        var rig = Expanded();
        rig.Island.OutsideClick();
        rig.Island.SetGuards(new IslandGuards { DialogOpen = dialog, DragActive = drag, FocusInside = focus, ConfirmationPending = confirm });
        rig.Clock.LocalNow = Start + TimeSpan.FromSeconds(1);

        var result = rig.Island.Tick();

        Assert.Equal(IslandRejection.BlockedByGuard, result.Rejection);
        Assert.Equal(IslandState.Expanded, rig.Island.State);
        Assert.Null(rig.Island.AutoCollapseDueAt); // dropped, not retried on its own
    }

    [Fact]
    public void GuardsAreCheckedWhenTheCollapseIsDueNotWhenItWasRequested()
    {
        var rig = Expanded();
        rig.Island.SetGuards(new IslandGuards { FocusInside = true });
        rig.Island.OutsideClick(); // accepted: the guard is not read yet
        rig.Island.SetGuards(IslandGuards.None); // focus left before the delay passed
        rig.Clock.LocalNow = Start + TimeSpan.FromSeconds(1);

        Assert.Equal(IslandState.Collapsing, rig.Island.Tick().Transition!.To);
    }

    [Fact]
    public void OnlyModalGuardsStopAnExplicitCollapse()
    {
        var rig = Expanded();

        rig.Island.SetGuards(new IslandGuards { FocusInside = true, DragActive = true }); // not modal
        Assert.True(rig.Island.Escape().Accepted);

        var again = Expanded();
        again.Island.SetGuards(new IslandGuards { DialogOpen = true });
        Assert.Equal(IslandRejection.BlockedByGuard, again.Island.Escape().Rejection);
        Assert.Equal(IslandRejection.BlockedByGuard, again.Island.Toggle().Rejection);
        Assert.Equal(IslandRejection.BlockedByGuard, again.Island.Collapse().Rejection);
        Assert.Equal(IslandState.Expanded, again.Island.State);

        again.Island.SetGuards(new IslandGuards { ConfirmationPending = true });
        Assert.Equal(IslandRejection.BlockedByGuard, again.Island.Escape().Rejection);

        again.Island.SetGuards(IslandGuards.None);
        Assert.True(again.Island.Escape().Accepted);
    }

    [Fact]
    public void AnyStateChangeClearsAScheduledCollapse()
    {
        var rig = Expanded();
        rig.Island.OutsideClick();

        rig.Island.Toggle();

        Assert.Null(rig.Island.AutoCollapseDueAt);
    }

    [Fact]
    public void GuardsCannotBeNullAndOptionsAreValidated()
    {
        var rig = Create();
        Assert.Throws<ArgumentNullException>(() => rig.Island.SetGuards(null!));
        Assert.Throws<ArgumentNullException>(() => new TransitionIsland(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TransitionIsland(new FakeClock(Start), new IslandStateOptions { OutsideClickDelay = TimeSpan.FromMilliseconds(-1) }));
    }

    // ---- reentrancy ----------------------------------------------------------------------------------------------

    [Fact]
    public void ARequestMadeFromInsideAChangeHandlerIsRejectedAndOrderIsKept()
    {
        var clock = new FakeClock(Start);
        var island = new TransitionIsland(clock);
        var inner = new List<IslandRequestResult>();
        var seen = new List<IslandTransition>();
        island.Changed += (_, t) =>
        {
            seen.Add(t);
            inner.Add(island.Toggle()); // a handler that reacts by toggling again
            inner.Add(island.CompleteExpansion(t.TransitionId));
            inner.Add(island.Exit());
        };

        island.Toggle();

        Assert.All(inner, r => Assert.Equal(IslandRejection.Reentrant, r.Rejection));
        Assert.Single(seen);
        Assert.Equal(IslandState.Expanding, island.State);

        // The guard is released afterwards.
        Assert.True(island.CompleteExpansion(island.TransitionId).Accepted);
    }

    [Fact]
    public void AThrowingHandlerDoesNotLeaveTheMachineLocked()
    {
        var island = new TransitionIsland(new FakeClock(Start));
        island.Changed += (_, _) => throw new InvalidOperationException("listener bug");

        Assert.Throws<InvalidOperationException>(() => island.Toggle());

        // The state change itself was applied, and the guard was released: the next request is processed (and its
        // listener throws again) instead of being refused as Reentrant.
        Assert.Equal(IslandState.Expanding, island.State);
        Assert.Throws<InvalidOperationException>(() => island.Collapse());
        Assert.Equal(IslandState.Collapsing, island.State);
    }

    // ---- exit ----------------------------------------------------------------------------------------------------

    [Fact]
    public void ExitMovesToClosingInvalidatesAnimationsAndRejectsEverythingAfter()
    {
        var rig = Create();
        var id = rig.Island.Toggle().Transition!.TransitionId;
        rig.Island.SetBusy(true);

        var exit = rig.Island.Exit();

        Assert.Equal(IslandState.Closing, exit.Transition!.To);
        Assert.Equal(IslandTrigger.Exit, exit.Transition.Trigger);
        Assert.Null(rig.Island.AutoCollapseDueAt);
        Assert.Equal(IslandRejection.Closing, rig.Island.CompleteExpansion(id).Rejection);
        Assert.Equal(IslandRejection.Closing, rig.Island.Toggle().Rejection);
        Assert.Equal(IslandRejection.Closing, rig.Island.Expand().Rejection);
        Assert.Equal(IslandRejection.Closing, rig.Island.Collapse().Rejection);
        Assert.Equal(IslandRejection.Closing, rig.Island.Escape().Rejection);
        Assert.Equal(IslandRejection.Closing, rig.Island.OutsideClick().Rejection);
        Assert.Equal(IslandRejection.Closing, rig.Island.Tick().Rejection);
        Assert.Equal(IslandRejection.Closing, rig.Island.SetBusy(false).Rejection);
        Assert.Equal(IslandRejection.Closing, rig.Island.Exit().Rejection); // idempotent
        Assert.Equal(IslandState.Closing, rig.Island.State);
    }

    [Fact]
    public void ExitClearsAScheduledCollapse()
    {
        var rig = Expanded();
        rig.Island.OutsideClick();

        rig.Island.Exit();

        Assert.Null(rig.Island.AutoCollapseDueAt);
    }

    // ---- randomized safety -----------------------------------------------------------------------------------------

    [Fact]
    public void RandomRequestSequencesNeverBreakTheInvariants()
    {
        var random = new Random(12345);
        var rig = Create();

        for (var step = 0; step < 5000; step++)
        {
            switch (random.Next(11))
            {
                case 0: rig.Island.Toggle(); break;
                case 1: rig.Island.Expand(); break;
                case 2: rig.Island.Collapse(); break;
                case 3: rig.Island.Escape(); break;
                case 4: rig.Island.OutsideClick(); break;
                case 5: rig.Clock.LocalNow += TimeSpan.FromMilliseconds(random.Next(0, 500)); rig.Island.Tick(); break;
                case 6: rig.Island.CompleteExpansion(random.Next(0, 3) == 0 ? rig.Island.TransitionId : random.Next(0, 50)); break;
                case 7: rig.Island.CompleteCollapse(random.Next(0, 3) == 0 ? rig.Island.TransitionId : random.Next(0, 50)); break;
                case 8: rig.Island.SetBusy(random.Next(2) == 0); break;
                case 9:
                    rig.Island.SetGuards(new IslandGuards { DialogOpen = random.Next(4) == 0, FocusInside = random.Next(4) == 0 });
                    break;
                default: rig.Island.CancelAutoCollapse(); break;
            }

            var state = rig.Island.State;
            Assert.NotEqual(IslandState.Closing, state); // Exit is never issued here
            Assert.Equal(state is IslandState.Expanding or IslandState.Collapsing, rig.Island.TransitionId != 0);
            Assert.Equal(state == IslandState.Busy, state is IslandState.Busy && rig.Island.IsBusy);
            if (rig.Island.AutoCollapseDueAt is not null)
                Assert.True(state is IslandState.Expanded or IslandState.Busy);
        }

        // Every accepted change followed on from the previous one.
        var expected = IslandState.Idle;
        foreach (var t in rig.Seen)
        {
            Assert.Equal(expected, t.From);
            Assert.NotEqual(t.From, t.To);
            expected = t.To;
        }
    }
}
