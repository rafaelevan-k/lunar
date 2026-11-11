using System.ComponentModel;
using System.Windows;
using Lunar.Desktop.Accessibility;
using Lunar.Desktop.Features.Pomodoro;
using Lunar.Desktop.Features.Recorder;
using Lunar.Desktop.Resources;

namespace Lunar.Desktop.Shell;

/// <summary>
/// Keeps the collapsed island showing the right thing (ADR-0024): it follows the recorder and the focus timer, sets the pill's width and hands the shell
/// the view to show. When the focus time runs out the eyes come back and celebrate. A screen reader hears the start, the pause and the end, never the
/// running time. Use on the UI thread.
/// </summary>
public sealed class IslandActivityController : IDisposable
{
    private readonly IslandViewModel _island;
    private readonly IslandShellView _shell;
    private readonly RecordsViewModel _records;
    private readonly FrameworkElement _recordingView;
    private readonly PomodoroPageViewModel _focus;
    private readonly FrameworkElement _focusView;
    private bool _wasShown;
    private bool _wasPaused;
    private bool _focusWasActive;
    private bool _focusWasPaused;
    private bool _disposed;

    public IslandActivityController(
        IslandViewModel island,
        IslandShellView shell,
        RecordsViewModel records,
        FrameworkElement recordingView,
        PomodoroPageViewModel focus,
        FrameworkElement focusView)
    {
        ArgumentNullException.ThrowIfNull(island);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(recordingView);
        ArgumentNullException.ThrowIfNull(focus);
        ArgumentNullException.ThrowIfNull(focusView);
        _island = island;
        _shell = shell;
        _records = records;
        _recordingView = recordingView;
        _focus = focus;
        _focusView = focusView;
        _wasShown = records.ShowsInIsland;
        _wasPaused = records.IsPaused;
        _focusWasActive = focus.IsActive;
        _focusWasPaused = focus.IsPaused;
        _records.PropertyChanged += OnRecordsChanged;
        _focus.PropertyChanged += OnFocusChanged;
        Update();
    }

    /// <summary>What the collapsed island shows now.</summary>
    public IslandActivity Current { get; private set; }

    /// <summary>A recording or a focus session started or ended (<see cref="Current"/> changed). The update prompt waits for these (FE-38).</summary>
    public event EventHandler? Changed;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _records.PropertyChanged -= OnRecordsChanged;
        _focus.PropertyChanged -= OnFocusChanged;
        _shell.SetIdleActivity(null);
    }

    private void OnRecordsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RecordsViewModel.ShowsInIsland) or nameof(RecordsViewModel.IsPaused) && !_disposed)
        {
            Update();
            AnnounceRecording();
        }
    }

    // The time itself is not followed here: the pill binds to it, and nothing about the island changes as it counts.
    private void OnFocusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PomodoroPageViewModel.IsActive) or nameof(PomodoroPageViewModel.IsPaused) or nameof(PomodoroPageViewModel.IsFinished) && !_disposed)
        {
            Update();
            FollowFocus();
        }
    }

    private void Update()
    {
        var previous = Current;
        Current = IslandActivitySelector.Choose(_records.ShowsInIsland, _focus.IsActive);
        _island.IdleWidth = IslandLayout.IdleWidthFor(Current);
        _shell.SetIdleActivity(Current switch
        {
            IslandActivity.Recording => _recordingView,
            IslandActivity.FocusCountdown => _focusView,
            _ => null
        });
        if (Current != previous)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    private void AnnounceRecording()
    {
        var shown = _records.ShowsInIsland;
        var paused = _records.IsPaused;
        string? words = null;
        if (shown && !_wasShown)
            words = Strings.Get("Records.AnnounceStarted");
        else if (!shown && _wasShown)
            words = Strings.Get("Records.AnnounceEnded");
        else if (shown && paused != _wasPaused)
            words = paused ? Strings.Get("Records.AnnouncePaused") : Strings.Get("Records.AnnounceResumed");
        _wasShown = shown;
        _wasPaused = paused;
        if (words is not null)
            LiveAnnouncer.Announce(_shell, words, "Lunar.Recording");
    }

    private void FollowFocus()
    {
        var active = _focus.IsActive;
        var paused = _focus.IsPaused;
        string? words = null;
        if (active && !_focusWasActive)
        {
            words = Strings.Get("Pomodoro.AnnounceStarted");
        }
        else if (!active && _focusWasActive)
        {
            if (_focus.IsFinished)
            {
                words = Strings.Get("Pomodoro.AnnounceFinished");
                _shell.Celebrate(); // the eyes are back by now, so they smile
            }
            else
            {
                words = Strings.Get("Pomodoro.AnnounceStopped");
            }
        }
        else if (active && paused != _focusWasPaused)
        {
            words = paused ? Strings.Get("Pomodoro.AnnouncePaused") : Strings.Get("Pomodoro.AnnounceResumed");
        }

        _focusWasActive = active;
        _focusWasPaused = paused;
        if (words is not null)
            LiveAnnouncer.Announce(_shell, words, "Lunar.Focus");
    }
}
