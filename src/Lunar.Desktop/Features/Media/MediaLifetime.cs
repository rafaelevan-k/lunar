using System.ComponentModel;
using Lunar.Desktop.Shell;

namespace Lunar.Desktop.Features.MediaControls;

/// <summary>
/// Ties the Media page's position ticker to the island being open (FE-17): the position moves along once a second while the
/// island is expanded and a session plays, and nothing runs while it is just the pill. Use on the UI thread; dispose to let go.
/// </summary>
internal sealed class MediaLifetime : IDisposable
{
    private readonly IslandViewModel _island;
    private readonly MediaPageViewModel _media;
    private bool _disposed;

    public MediaLifetime(IslandViewModel island, MediaPageViewModel media)
    {
        ArgumentNullException.ThrowIfNull(island);
        ArgumentNullException.ThrowIfNull(media);
        _island = island;
        _media = media;
        island.PropertyChanged += OnIslandChanged;
        Apply();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _island.PropertyChanged -= OnIslandChanged;
        _media.StopTicker();
    }

    private void OnIslandChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IslandViewModel.IsExpanded) && !_disposed)
            Apply();
    }

    private void Apply()
    {
        if (_island.IsExpanded)
            _media.StartTicker();
        else
            _media.StopTicker();
    }
}
