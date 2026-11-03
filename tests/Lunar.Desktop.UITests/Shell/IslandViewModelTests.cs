using System.Windows.Automation;
using Lunar.Desktop.Shell;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Desktop.UITests.Shell;

// The view model has no OS dependency, so these run without a window; only the binding tests need WPF.
public sealed class IslandViewModelTests
{
    private static IslandViewModel InState(IslandVisualState state)
    {
        var vm = new IslandViewModel();
        switch (state)
        {
            case IslandVisualState.Idle:
                break;
            case IslandVisualState.Expanding:
                vm.Toggle();
                break;
            case IslandVisualState.Expanded:
                vm.Toggle();
                vm.CompleteExpansion();
                break;
            case IslandVisualState.Collapsing:
                vm.Toggle();
                vm.CompleteExpansion();
                vm.Toggle();
                break;
            case IslandVisualState.Busy:
                vm.Toggle();
                vm.CompleteExpansion();
                vm.SetBusy(true);
                break;
        }

        Assert.Equal(state, vm.State);
        return vm;
    }

    private static List<string> Changes(IslandViewModel vm)
    {
        var names = new List<string>();
        vm.PropertyChanged += (_, e) => names.Add(e.PropertyName!);
        return names;
    }

    [Fact]
    public void StartsCollapsedWithoutBusyOrError()
    {
        var vm = new IslandViewModel();

        Assert.Equal(IslandVisualState.Idle, vm.State);
        Assert.False(vm.IsExpanded);
        Assert.False(vm.IsBusy);
        Assert.False(vm.HasError);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal("Collapsed", vm.StatusText);
        Assert.True(vm.CanToggle);
        Assert.False(vm.CanCollapse);
    }

    [Theory]
    [InlineData(IslandVisualState.Idle, "Toggle", true, IslandVisualState.Expanding)]
    [InlineData(IslandVisualState.Idle, "Collapse", false, IslandVisualState.Idle)]
    [InlineData(IslandVisualState.Idle, "CompleteExpansion", false, IslandVisualState.Idle)]
    [InlineData(IslandVisualState.Idle, "CompleteCollapse", false, IslandVisualState.Idle)]
    [InlineData(IslandVisualState.Expanding, "Toggle", true, IslandVisualState.Collapsing)] // a click reverses a running animation
    [InlineData(IslandVisualState.Expanding, "Collapse", true, IslandVisualState.Collapsing)]
    [InlineData(IslandVisualState.Expanding, "CompleteExpansion", true, IslandVisualState.Expanded)]
    [InlineData(IslandVisualState.Expanding, "CompleteCollapse", false, IslandVisualState.Expanding)]
    [InlineData(IslandVisualState.Expanded, "Toggle", true, IslandVisualState.Collapsing)]
    [InlineData(IslandVisualState.Expanded, "Collapse", true, IslandVisualState.Collapsing)]
    [InlineData(IslandVisualState.Expanded, "CompleteExpansion", false, IslandVisualState.Expanded)]
    [InlineData(IslandVisualState.Expanded, "CompleteCollapse", false, IslandVisualState.Expanded)]
    [InlineData(IslandVisualState.Collapsing, "Toggle", true, IslandVisualState.Expanding)]
    [InlineData(IslandVisualState.Collapsing, "Collapse", false, IslandVisualState.Collapsing)]
    [InlineData(IslandVisualState.Collapsing, "CompleteExpansion", false, IslandVisualState.Collapsing)]
    [InlineData(IslandVisualState.Collapsing, "CompleteCollapse", true, IslandVisualState.Idle)]
    [InlineData(IslandVisualState.Busy, "Toggle", true, IslandVisualState.Collapsing)]
    [InlineData(IslandVisualState.Busy, "Collapse", true, IslandVisualState.Collapsing)]
    [InlineData(IslandVisualState.Busy, "CompleteExpansion", false, IslandVisualState.Busy)]
    [InlineData(IslandVisualState.Busy, "CompleteCollapse", false, IslandVisualState.Busy)]
    public void EveryStateAndRequestHasOneDefinedOutcome(IslandVisualState from, string request, bool accepted, IslandVisualState expected)
    {
        var vm = InState(from);
        var changes = Changes(vm);

        var result = request switch
        {
            "Toggle" => vm.Toggle(),
            "Collapse" => vm.Collapse(),
            "CompleteExpansion" => vm.CompleteExpansion(),
            "CompleteCollapse" => vm.CompleteCollapse(),
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };

        Assert.Equal(accepted, result);
        Assert.Equal(expected, vm.State);
        Assert.Equal(accepted, changes.Contains(nameof(IslandViewModel.State)));
        if (!accepted)
            Assert.Empty(changes); // a rejected request changes nothing and notifies nothing
    }

    [Fact]
    public void AcceptedTransitionNotifiesEachChangedPropertyOnce()
    {
        var vm = new IslandViewModel();
        var changes = Changes(vm);

        vm.Toggle();

        Assert.Equal(
            [nameof(IslandViewModel.State), nameof(IslandViewModel.IsExpanded), nameof(IslandViewModel.IsExpandedLayout),
                nameof(IslandViewModel.StatusText),
                nameof(IslandViewModel.CanToggle), nameof(IslandViewModel.CanCollapse), nameof(IslandViewModel.TransitionId)],
            changes);
    }

    [Fact]
    public void FullCycleReturnsToIdle()
    {
        var vm = new IslandViewModel();

        Assert.True(vm.Toggle());
        Assert.Equal("Expanding", vm.StatusText);
        Assert.True(vm.CompleteExpansion());
        Assert.True(vm.IsExpanded);
        Assert.Equal("Expanded", vm.StatusText);
        Assert.True(vm.Toggle());
        Assert.Equal("Collapsing", vm.StatusText);
        Assert.False(vm.IsExpanded);
        Assert.True(vm.CompleteCollapse());

        Assert.Equal(IslandVisualState.Idle, vm.State);
        Assert.Equal("Collapsed", vm.StatusText);
    }

    [Fact]
    public void BusyFollowsTheJobWhileExpanded()
    {
        var vm = InState(IslandVisualState.Expanded);

        Assert.True(vm.SetBusy(true));
        Assert.Equal(IslandVisualState.Busy, vm.State);
        Assert.True(vm.IsExpanded);
        Assert.Equal("Busy", vm.StatusText);

        Assert.True(vm.SetBusy(false));
        Assert.Equal(IslandVisualState.Expanded, vm.State);
    }

    [Fact]
    public void JobContinuesAfterCollapse()
    {
        var vm = InState(IslandVisualState.Busy);

        vm.Collapse();
        vm.CompleteCollapse();

        Assert.Equal(IslandVisualState.Idle, vm.State);
        Assert.True(vm.IsBusy);

        vm.Toggle();
        vm.CompleteExpansion();
        Assert.Equal(IslandVisualState.Busy, vm.State); // reopened while still busy

        vm.SetBusy(false);
        Assert.Equal(IslandVisualState.Expanded, vm.State);
    }

    [Fact]
    public void BusyFlagOutsideExpandedOnlyAppliesWhenExpansionCompletes()
    {
        var idle = new IslandViewModel();
        Assert.True(idle.SetBusy(true));
        Assert.Equal(IslandVisualState.Idle, idle.State);

        var expanding = InState(IslandVisualState.Expanding);
        expanding.SetBusy(true);
        Assert.Equal(IslandVisualState.Expanding, expanding.State);
        expanding.CompleteExpansion();
        Assert.Equal(IslandVisualState.Busy, expanding.State);

        var collapsing = InState(IslandVisualState.Busy);
        collapsing.Collapse();
        collapsing.SetBusy(false);
        Assert.Equal(IslandVisualState.Collapsing, collapsing.State);
        collapsing.CompleteCollapse();
        Assert.Equal(IslandVisualState.Idle, collapsing.State);
        Assert.False(collapsing.IsBusy);
    }

    [Fact]
    public void SettingTheSameBusyValueDoesNothing()
    {
        var vm = new IslandViewModel();
        var changes = Changes(vm);

        Assert.False(vm.SetBusy(false));
        vm.SetBusy(true);
        Assert.False(vm.SetBusy(true));

        Assert.Equal([nameof(IslandViewModel.IsBusy)], changes);
    }

    [Fact]
    public void ErrorIsInlineAndNeverChangesTheState()
    {
        foreach (var state in Enum.GetValues<IslandVisualState>())
        {
            var vm = InState(state);

            vm.ReportError(new OperationError(ErrorCode.IoFailure, "The file could not be read."));

            Assert.Equal(state, vm.State);
            Assert.True(vm.HasError);
            Assert.Equal("The file could not be read.", vm.ErrorMessage);
        }
    }

    [Fact]
    public void NewerErrorReplacesTheOldOneAndDismissClearsIt()
    {
        var vm = new IslandViewModel();
        var changes = Changes(vm);

        vm.ReportError(new OperationError(ErrorCode.IoFailure, "First."));
        vm.ReportError(new OperationError(ErrorCode.Timeout, "Second."));
        changes.Clear();
        vm.DismissError();
        vm.DismissError(); // nothing left to dismiss

        Assert.Null(vm.ErrorMessage);
        Assert.False(vm.HasError);
        Assert.Equal([nameof(IslandViewModel.ErrorMessage), nameof(IslandViewModel.HasError)], changes);
        Assert.Throws<ArgumentNullException>(() => vm.ReportError(null!));
    }

    [Fact]
    public void ErrorSurvivesCollapseAndExpansion()
    {
        var vm = InState(IslandVisualState.Expanded);
        vm.ReportError(new OperationError(ErrorCode.Unsupported, "Not supported."));

        vm.Collapse();
        vm.CompleteCollapse();
        vm.Toggle();
        vm.CompleteExpansion();

        Assert.Equal("Not supported.", vm.ErrorMessage);
    }

    [Fact]
    public void CommandsFollowTheStateAndNotifyWhenItChanges()
    {
        var vm = new IslandViewModel();
        var toggleNotices = 0;
        var collapseNotices = 0;
        vm.ToggleCommand.CanExecuteChanged += (_, _) => toggleNotices++;
        vm.CollapseCommand.CanExecuteChanged += (_, _) => collapseNotices++;

        Assert.True(vm.ToggleCommand.CanExecute(null));
        Assert.False(vm.CollapseCommand.CanExecute(null));

        vm.ToggleCommand.Execute(null); // Idle -> Expanding
        Assert.True(vm.ToggleCommand.CanExecute(null)); // a click can always act now
        Assert.True(vm.CollapseCommand.CanExecute(null)); // and Escape can reverse the expansion
        vm.ToggleCommand.Execute(null); // reverses at once
        Assert.Equal(IslandVisualState.Collapsing, vm.State);
        Assert.False(vm.CollapseCommand.CanExecute(null)); // nothing left to collapse

        vm.ToggleCommand.Execute(null); // reverses again
        Assert.Equal(IslandVisualState.Expanding, vm.State);
        vm.CompleteExpansion();
        vm.CollapseCommand.Execute(null);

        Assert.Equal(IslandVisualState.Collapsing, vm.State);
        Assert.Equal(5, toggleNotices); // one per accepted transition
        Assert.Equal(5, collapseNotices);
    }

    [Fact]
    public void EachAnimationHasItsOwnIdAndAStaleCompletionChangesNothing()
    {
        var vm = new IslandViewModel();
        vm.Toggle();
        var expandId = vm.TransitionId;
        vm.Toggle(); // interrupts: Collapsing with a new id
        var collapseId = vm.TransitionId;
        var changes = Changes(vm);

        Assert.NotEqual(expandId, collapseId);
        Assert.False(vm.CompleteExpansion(expandId)); // the interrupted animation reports late
        Assert.Equal(IslandVisualState.Collapsing, vm.State);
        Assert.Empty(changes);

        Assert.True(vm.CompleteCollapse(collapseId));
        Assert.Equal(IslandVisualState.Idle, vm.State);
        Assert.Equal(0, vm.TransitionId);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(9)]
    [InlineData(500)]
    public void RapidClicksNeverCorruptTheState(int clicks)
    {
        var vm = new IslandViewModel();

        for (var i = 0; i < clicks; i++)
            Assert.True(vm.Toggle());

        Assert.Equal(clicks % 2 == 1 ? IslandVisualState.Expanding : IslandVisualState.Collapsing, vm.State);
        Assert.True(vm.IsExpandedLayout);
        Assert.NotEqual(0, vm.TransitionId);

        if (clicks % 2 == 1)
            Assert.True(vm.CompleteExpansion());
        else
            Assert.True(vm.CompleteCollapse());
        Assert.Equal(clicks % 2 == 1 ? IslandVisualState.Expanded : IslandVisualState.Idle, vm.State);
    }

    [Fact]
    public void ListenersMayCallBackIntoTheViewModelFromANotification()
    {
        var vm = new IslandViewModel();
        var seenStates = new List<IslandVisualState>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IslandViewModel.TransitionId) && vm.State == IslandVisualState.Expanding)
                vm.CompleteExpansion(vm.TransitionId); // a view that completes at once (reduced motion)
            if (e.PropertyName == nameof(IslandViewModel.State))
                seenStates.Add(vm.State);
        };

        vm.Toggle();

        Assert.Equal(IslandVisualState.Expanded, vm.State);
        Assert.Contains(IslandVisualState.Expanding, seenStates);
    }

    [Fact]
    public void ConstructorRejectsNullAndTheDefaultOneWorks()
    {
        Assert.Throws<ArgumentNullException>(() => new IslandViewModel(null!));
        Assert.Equal(IslandVisualState.Idle, new IslandViewModel().State);
    }

    [Fact]
    public void DismissCommandIsEnabledOnlyWithAnError()
    {
        var vm = new IslandViewModel();
        var notices = 0;
        vm.DismissErrorCommand.CanExecuteChanged += (_, _) => notices++;

        Assert.False(vm.DismissErrorCommand.CanExecute(null));
        vm.ReportError(new OperationError(ErrorCode.Unexpected, "Something failed."));
        Assert.True(vm.DismissErrorCommand.CanExecute(null));
        vm.DismissErrorCommand.Execute(null);

        Assert.False(vm.HasError);
        Assert.Equal(2, notices);
    }

    [Fact]
    public void RelayCommandRejectsNullAndSkipsExecuteWhenDisabled()
    {
        Assert.Throws<ArgumentNullException>(() => new RelayCommand(null!));
        var calls = 0;
        var command = new RelayCommand(() => calls++, () => false);

        command.Execute(null);

        Assert.Equal(0, calls);
        Assert.False(command.CanExecute(null));
        Assert.True(new RelayCommand(() => { }).CanExecute(null));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RandomRequestSequencesAlwaysKeepTheInvariants(int seed)
    {
        var random = new Random(seed);
        var vm = new IslandViewModel();

        for (var i = 0; i < 5000; i++)
        {
            var before = vm.State;
            var accepted = random.Next(6) switch
            {
                0 => vm.Toggle(),
                1 => vm.Collapse(),
                2 => vm.CompleteExpansion(),
                3 => vm.CompleteCollapse(),
                4 => vm.SetBusy(random.Next(2) == 0),
                _ => false
            };

            Assert.Equal(vm.State is IslandVisualState.Expanded or IslandVisualState.Busy, vm.IsExpanded);
            if (vm.State == IslandVisualState.Busy)
                Assert.True(vm.IsBusy);
            if (vm.State == IslandVisualState.Expanded)
                Assert.False(vm.IsBusy);
            if (!accepted)
                Assert.Equal(before, vm.State);
            Assert.True(vm.CanToggle);
            Assert.Equal(vm.State is IslandVisualState.Expanding or IslandVisualState.Expanded or IslandVisualState.Busy, vm.CanCollapse);
            Assert.Equal(vm.State is IslandVisualState.Expanding or IslandVisualState.Collapsing, vm.TransitionId != 0);
        }
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new InvalidOperationException("WPF binding check failed.", failure);
    }

    [Fact]
    public void WindowExposesTheStateToAssistiveTechnologyThroughBinding() => RunOnSta(() =>
    {
        var vm = new IslandViewModel();
        var window = new IslandWindow { DataContext = vm };

        // Shown (non-activating), as in the app: bindings need the window to be part of a live tree.
        window.Show();
        try
        {
            Assert.Equal("Collapsed", AutomationProperties.GetItemStatus(window));

            vm.Toggle();
            Assert.Equal("Expanding", AutomationProperties.GetItemStatus(window));
            vm.CompleteExpansion();
            vm.SetBusy(true);
            Assert.Equal("Busy", AutomationProperties.GetItemStatus(window));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void WindowWithoutAViewModelStillWorks() => RunOnSta(() =>
    {
        var window = new IslandWindow();

        Assert.Equal(string.Empty, AutomationProperties.GetItemStatus(window));
        window.Close();
    });
}
