using System.ComponentModel;
using Lunar.Desktop.Shell;

namespace Lunar.Desktop.Features.Notes;

/// <summary>
/// Saves the open note when the island collapses (FE-31), so nothing typed waits for the autosave pause. It starts and stops nothing else.
/// Use on the UI thread; dispose to let go.
/// </summary>
internal sealed class NotesLifetime : IDisposable
{
    private readonly IslandViewModel _island;
    private readonly NotesPageViewModel _notes;
    private bool _disposed;

    public NotesLifetime(IslandViewModel island, NotesPageViewModel notes)
    {
        ArgumentNullException.ThrowIfNull(island);
        ArgumentNullException.ThrowIfNull(notes);
        _island = island;
        _notes = notes;
        island.PropertyChanged += OnIslandChanged;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _island.PropertyChanged -= OnIslandChanged;
    }

    private void OnIslandChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IslandViewModel.IsExpanded) && !_disposed)
            _notes.SetIslandOpen(_island.IsExpanded);
    }
}
