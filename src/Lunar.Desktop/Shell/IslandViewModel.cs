using System.ComponentModel;
using System.Windows.Input;
using Lunar.Application.Shell.Models;
using Lunar.Application.Shell.UseCases;
using Lunar.Desktop.Composition;
using Lunar.Domain.Shared;

namespace Lunar.Desktop.Shell;

/// <summary>
/// Presentation state of the island (FE-04, driven by the BE-09 state machine since FE-10): the visual state, a
/// background-busy flag and an inline error, with commands for the view. Transitions are validated by
/// <see cref="TransitionIsland"/>; this class only adapts them for binding. It makes no OS, file or settings calls and
/// owns no timers: the view reports when an animation has finished, together with the animation's
/// <see cref="TransitionId"/>, so a completion that belongs to an interrupted animation is ignored. A request that
/// does not apply in the current state is ignored and reported by returning false. Property change notifications
/// are raised after the state machine has finished its own notification, so a listener can safely call back into
/// this view model. Must be used on the UI thread.
/// </summary>
public sealed class IslandViewModel : INotifyPropertyChanged
{
    private readonly TransitionIsland _island;
    private string? _errorMessage;

    public IslandViewModel()
        : this(new TransitionIsland(SystemClock.Instance))
    {
    }

    public IslandViewModel(TransitionIsland island)
    {
        _island = island ?? throw new ArgumentNullException(nameof(island));
        ToggleCommand = new RelayCommand(() => Toggle(), () => CanToggle);
        CollapseCommand = new RelayCommand(() => Collapse(), () => CanCollapse);
        DismissErrorCommand = new RelayCommand(DismissError, () => HasError);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IslandVisualState State => _island.State switch
    {
        IslandState.Expanding => IslandVisualState.Expanding,
        IslandState.Expanded => IslandVisualState.Expanded,
        IslandState.Collapsing => IslandVisualState.Collapsing,
        IslandState.Busy => IslandVisualState.Busy,
        _ => IslandVisualState.Idle // Idle, and Closing (this view model never starts it)
    };

    /// <summary>Identifies the running animation (zero when none); the view passes it back when the animation ends.</summary>
    public long TransitionId => _island.TransitionId;

    /// <summary>True while fully expanded (Expanded or Busy); false during both animations.</summary>
    public bool IsExpanded => State is IslandVisualState.Expanded or IslandVisualState.Busy;

    /// <summary>
    /// The large layout is needed: true in every state except Idle, including both animations, so the window is
    /// never smaller than the content that is still being shown.
    /// </summary>
    public bool IsExpandedLayout => State != IslandVisualState.Idle;

    /// <summary>A background job is active. It survives a collapse, so an idle island can still be busy.</summary>
    public bool IsBusy => _island.IsBusy;

    /// <summary>Recoverable failure shown inline; it never changes <see cref="State"/>.</summary>
    public string? ErrorMessage => _errorMessage;

    public bool HasError => _errorMessage is not null;

    /// <summary>Short plain-text status for assistive technology.</summary>
    public string StatusText => State switch
    {
        IslandVisualState.Idle => "Collapsed",
        IslandVisualState.Expanding => "Expanding",
        IslandVisualState.Expanded => "Expanded",
        IslandVisualState.Collapsing => "Collapsing",
        _ => "Busy"
    };

    /// <summary>A click can always act: it expands an idle island and reverses a running animation.</summary>
    public bool CanToggle => _island.State != IslandState.Closing;

    /// <summary>Collapse applies while expanding (it reverses the expansion), expanded or busy.</summary>
    public bool CanCollapse => State is IslandVisualState.Expanding or IslandVisualState.Expanded or IslandVisualState.Busy;

    public ICommand ToggleCommand { get; }

    public ICommand CollapseCommand { get; }

    public ICommand DismissErrorCommand { get; }

    /// <summary>
    /// Idle starts expanding; expanded or busy starts collapsing; during an animation the direction is reversed at
    /// once, so rapid clicks cannot get stuck.
    /// </summary>
    public bool Toggle() => Apply(_island.Toggle());

    /// <summary>Starts collapsing from expanded or busy (Escape), or reverses a running expansion.</summary>
    public bool Collapse() => Apply(_island.Collapse());

    /// <summary>The view reports that the current expansion animation finished.</summary>
    public bool CompleteExpansion() => CompleteExpansion(_island.TransitionId);

    /// <summary>The view reports that expansion animation <paramref name="transitionId"/> finished.</summary>
    public bool CompleteExpansion(long transitionId) => Apply(_island.CompleteExpansion(transitionId));

    /// <summary>The view reports that the current collapse animation finished.</summary>
    public bool CompleteCollapse() => CompleteCollapse(_island.TransitionId);

    /// <summary>The view reports that collapse animation <paramref name="transitionId"/> finished.</summary>
    public bool CompleteCollapse(long transitionId) => Apply(_island.CompleteCollapse(transitionId));

    /// <summary>
    /// Sets whether a background job is active. While expanded this switches between Expanded and Busy; in every
    /// other state only the flag changes and the state follows when the expansion completes.
    /// </summary>
    public bool SetBusy(bool busy)
    {
        var result = _island.SetBusy(busy);
        if (!result.Accepted)
            return false;

        Raise(nameof(IsBusy));
        if (result.Transition is not null)
            RaiseStateChanged();
        return true;
    }

    /// <summary>Shows a recoverable failure inline using its user-safe message; a newer error replaces the old one.</summary>
    public void ReportError(OperationError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        SetError(error.SafeMessage);
    }

    public void DismissError() => SetError(null);

    private void SetError(string? message)
    {
        if (_errorMessage == message)
            return;

        _errorMessage = message;
        Raise(nameof(ErrorMessage));
        Raise(nameof(HasError));
        ((RelayCommand)DismissErrorCommand).NotifyCanExecuteChanged();
    }

    private bool Apply(IslandRequestResult result)
    {
        if (result.Transition is null)
            return false;

        RaiseStateChanged();
        return true;
    }

    // TransitionId goes last on purpose: the view starts an animation when it changes, by which time the window
    // has already been resized for the new layout (IsExpandedLayout comes before it).
    private void RaiseStateChanged()
    {
        Raise(nameof(State));
        Raise(nameof(IsExpanded));
        Raise(nameof(IsExpandedLayout));
        Raise(nameof(StatusText));
        Raise(nameof(CanToggle));
        Raise(nameof(CanCollapse));
        ((RelayCommand)ToggleCommand).NotifyCanExecuteChanged();
        ((RelayCommand)CollapseCommand).NotifyCanExecuteChanged();
        Raise(nameof(TransitionId));
    }

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
