using System.ComponentModel;
using System.Windows.Input;
using Lunar.Domain.Shared;

namespace Lunar.Desktop.Shell;

/// <summary>
/// Presentation state of the island (FE-04): the visual state, a background-busy flag and an inline error, with
/// commands for the view. It makes no OS, file or settings calls and owns no timers: the view reports when an
/// animation has finished, so every transition is a pure, deterministic function of the current state and the
/// request. A request that does not apply in the current state is ignored and reported by returning false.
/// Must be used on the UI thread. Guards (dialog open, drag, focus), debouncing, interruption of a running
/// animation and time are BE-09; until then a request made while an animation runs is ignored.
/// </summary>
public sealed class IslandViewModel : INotifyPropertyChanged
{
    private IslandVisualState _state = IslandVisualState.Idle;
    private bool _isBusy;
    private string? _errorMessage;

    public IslandViewModel()
    {
        ToggleCommand = new RelayCommand(() => Toggle(), () => CanToggle);
        CollapseCommand = new RelayCommand(() => Collapse(), () => CanCollapse);
        DismissErrorCommand = new RelayCommand(DismissError, () => HasError);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IslandVisualState State => _state;

    /// <summary>True while fully expanded (Expanded or Busy); false during both animations.</summary>
    public bool IsExpanded => _state is IslandVisualState.Expanded or IslandVisualState.Busy;

    /// <summary>
    /// The large layout is needed: true in every state except Idle, including both animations, so the window is
    /// never smaller than the content that is still being shown.
    /// </summary>
    public bool IsExpandedLayout => _state != IslandVisualState.Idle;

    /// <summary>A background job is active. It survives a collapse, so an idle island can still be busy.</summary>
    public bool IsBusy => _isBusy;

    /// <summary>Recoverable failure shown inline; it never changes <see cref="State"/>.</summary>
    public string? ErrorMessage => _errorMessage;

    public bool HasError => _errorMessage is not null;

    /// <summary>Short plain-text status for assistive technology.</summary>
    public string StatusText => _state switch
    {
        IslandVisualState.Idle => "Collapsed",
        IslandVisualState.Expanding => "Expanding",
        IslandVisualState.Expanded => "Expanded",
        IslandVisualState.Collapsing => "Collapsing",
        _ => "Busy"
    };

    public bool CanToggle => _state is IslandVisualState.Idle or IslandVisualState.Expanded or IslandVisualState.Busy;

    public bool CanCollapse => _state is IslandVisualState.Expanded or IslandVisualState.Busy;

    public ICommand ToggleCommand { get; }

    public ICommand CollapseCommand { get; }

    public ICommand DismissErrorCommand { get; }

    /// <summary>Idle starts expanding; expanded or busy starts collapsing; during an animation nothing happens.</summary>
    public bool Toggle() => _state switch
    {
        IslandVisualState.Idle => Move(IslandVisualState.Expanding),
        IslandVisualState.Expanded or IslandVisualState.Busy => Move(IslandVisualState.Collapsing),
        _ => false
    };

    /// <summary>Starts collapsing from expanded or busy (Escape, outside click); otherwise nothing happens.</summary>
    public bool Collapse() => CanCollapse && Move(IslandVisualState.Collapsing);

    /// <summary>The view reports that the expansion animation finished.</summary>
    public bool CompleteExpansion() =>
        _state == IslandVisualState.Expanding && Move(_isBusy ? IslandVisualState.Busy : IslandVisualState.Expanded);

    /// <summary>The view reports that the collapse animation finished.</summary>
    public bool CompleteCollapse() => _state == IslandVisualState.Collapsing && Move(IslandVisualState.Idle);

    /// <summary>
    /// Sets whether a background job is active. While expanded this switches between Expanded and Busy; in every
    /// other state only the flag changes and the state follows when the expansion completes.
    /// </summary>
    public bool SetBusy(bool busy)
    {
        if (_isBusy == busy)
            return false;

        _isBusy = busy;
        Raise(nameof(IsBusy));
        if (_state == IslandVisualState.Expanded && busy)
            Move(IslandVisualState.Busy);
        else if (_state == IslandVisualState.Busy && !busy)
            Move(IslandVisualState.Expanded);
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

    private bool Move(IslandVisualState next)
    {
        if (_state == next)
            return false;

        _state = next;
        Raise(nameof(State));
        Raise(nameof(IsExpanded));
        Raise(nameof(IsExpandedLayout));
        Raise(nameof(StatusText));
        Raise(nameof(CanToggle));
        Raise(nameof(CanCollapse));
        ((RelayCommand)ToggleCommand).NotifyCanExecuteChanged();
        ((RelayCommand)CollapseCommand).NotifyCanExecuteChanged();
        return true;
    }

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
