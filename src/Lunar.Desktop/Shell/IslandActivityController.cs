using System.ComponentModel;
using System.Windows;
using Lunar.Desktop.Accessibility;
using Lunar.Desktop.Features.Recorder;
using Lunar.Desktop.Resources;

namespace Lunar.Desktop.Shell;

/// <summary>
/// Keeps the collapsed island showing the right thing (ADR-0024): it follows the recorder, sets the pill's width and hands the shell the view to show.
/// A screen reader hears the start, the pause and the end, never the running timer. Use on the UI thread.
/// </summary>
public sealed class IslandActivityController : IDisposable
{
    private readonly IslandViewModel _island;
    private readonly IslandShellView _shell;
    private readonly RecordsViewModel _records;
    private readonly FrameworkElement _recordingView;
    private bool _wasShown;
    private bool _wasPaused;
    private bool _disposed;

    public IslandActivityController(IslandViewModel island, IslandShellView shell, RecordsViewModel records, FrameworkElement recordingView)
    {
        ArgumentNullException.ThrowIfNull(island);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(recordingView);
        _island = island;
        _shell = shell;
        _records = records;
        _recordingView = recordingView;
        _records.PropertyChanged += OnRecordsChanged;
        Update();
    }

    /// <summary>What the collapsed island shows now.</summary>
    public IslandActivity Current { get; private set; }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _records.PropertyChanged -= OnRecordsChanged;
        _shell.SetIdleActivity(null);
    }

    private void OnRecordsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RecordsViewModel.ShowsInIsland) or nameof(RecordsViewModel.IsPaused) && !_disposed)
            Update();
    }

    private void Update()
    {
        Current = IslandActivitySelector.Choose(_records.ShowsInIsland, focusSession: false);
        _island.IdleWidth = IslandLayout.IdleWidthFor(Current);
        _shell.SetIdleActivity(Current == IslandActivity.Recording ? _recordingView : null);
        Announce();
    }

    private void Announce()
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
}
