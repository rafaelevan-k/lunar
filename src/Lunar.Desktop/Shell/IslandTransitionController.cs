using System.ComponentModel;
using System.Windows.Threading;

namespace Lunar.Desktop.Shell;

/// <summary>
/// Plays the pill-to-panel and panel-to-pill animation (FE-10) for the transitions the view model starts, and reports
/// each animation's end back with its transition id. The state machine (BE-09) decides what happens; this class only
/// draws it:
/// - Expanding: the window is already at the expanded height (IslandPositionController; its width never changes, FE-33), the pill glides
///   from the idle size to the window size.
/// - Collapsing: the pill glides to the idle size, and the window loses its height only when the animation has finished.
/// - Interruption: a reversal starts from the pill's live size, so there is no jump, and the replaced animation's
///   completion is never reported; a late or repeated completion is also ignored by the state machine.
/// - Reduced motion: no animation. The pill is placed at once and the transition is completed on the next dispatcher
///   turn (never from inside the property-change notification that started it).
/// It owns no timer; the animation runs on WPF's render thread. Must be used on the UI thread.
/// </summary>
public sealed class IslandTransitionController : IDisposable
{
    internal static readonly TimeSpan ExpandDuration = TimeSpan.FromMilliseconds(260);
    internal static readonly TimeSpan CollapseDuration = TimeSpan.FromMilliseconds(200);
    internal static readonly TimeSpan WidthChangeDuration = TimeSpan.FromMilliseconds(180);

    private readonly IslandWindow _window;
    private readonly IslandViewModel _viewModel;
    private bool _reduceMotion;
    private bool _disposed;

    public IslandTransitionController(IslandWindow window, IslandViewModel viewModel, bool reduceMotion)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);
        _window = window;
        _viewModel = viewModel;
        _reduceMotion = reduceMotion;
        _viewModel.PropertyChanged += OnViewModelChanged;
    }

    /// <summary>The motion setting changed (ADR-0016); the next transition follows it. UI thread only.</summary>
    internal void SetReduceMotion(bool reduceMotion) => _reduceMotion = reduceMotion;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _viewModel.PropertyChanged -= OnViewModelChanged;
        _window.ResetPill(!_viewModel.IsExpandedLayout, _viewModel.IdleWidth);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed)
            return;

        if (e.PropertyName == nameof(IslandViewModel.IdleWidth))
        {
            ResizeIdlePill();
            return;
        }

        // The view model raises State first, then IsExpandedLayout (the position controller resizes the window), then TransitionId.
        if (e.PropertyName != nameof(IslandViewModel.TransitionId))
            return;

        var id = _viewModel.TransitionId;
        switch (_viewModel.State)
        {
            case IslandVisualState.Expanding:
                Run(_viewModel.IdleWidth, IslandLayout.IdleHeight, _window.Width, _window.Height, ExpandDuration, () => _viewModel.CompleteExpansion(id), id);
                break;
            case IslandVisualState.Collapsing:
                Run(_window.Width, _window.Height, _viewModel.IdleWidth, IslandLayout.IdleHeight, CollapseDuration, () => _viewModel.CompleteCollapse(id), id);
                break;
            default:
                // Not animating: a completion (or exit) has finished the transition, so the pill takes its resting size.
                _window.ResetPill(!_viewModel.IsExpandedLayout, _viewModel.IdleWidth);
                break;
        }
    }

    // What the collapsed island shows changed: only the pill changes width, inside a window that stays where it is. While expanded it waits for the collapse.
    private void ResizeIdlePill()
    {
        if (_viewModel.State != IslandVisualState.Idle)
            return;
        var width = _viewModel.IdleWidth;
        if (_reduceMotion)
        {
            _window.ResetPill(idle: true, width);
            return;
        }

        _window.AnimatePill(width, IslandLayout.IdleHeight, width, IslandLayout.IdleHeight, WidthChangeDuration, () =>
        {
            if (!_disposed && _viewModel.State == IslandVisualState.Idle)
                _window.ResetPill(idle: true, _viewModel.IdleWidth);
        });
    }

    private void Run(double fromWidth, double fromHeight, double width, double height, TimeSpan duration, Action complete, long id)
    {
        if (_reduceMotion)
        {
            _window.AnimatePill(fromWidth, fromHeight, width, height, TimeSpan.Zero, () => { });
            _window.Dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
            {
                if (!_disposed && _viewModel.TransitionId == id)
                    complete();
            });
            return;
        }

        _window.AnimatePill(fromWidth, fromHeight, width, height, duration, () =>
        {
            if (!_disposed)
                complete();
        });
    }
}
