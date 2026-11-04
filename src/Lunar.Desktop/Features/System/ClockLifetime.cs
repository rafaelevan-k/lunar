using System.ComponentModel;
using Lunar.Desktop.Shell;

namespace Lunar.Desktop.Features.SystemControls;

/// <summary>
/// Ties the System page's clock to the island being open (FE-16): the clock starts (fresh time, then a tick every minute)
/// when the island expands and stops when it collapses, so nothing runs, and nothing is redrawn, while the island is just
/// the pill. Use on the UI thread; dispose to let go of the island.
/// </summary>
internal sealed class ClockLifetime : IDisposable
{
    private readonly IslandViewModel _island;
    private readonly SystemPageViewModel _system;
    private bool _disposed;

    public ClockLifetime(IslandViewModel island, SystemPageViewModel system)
    {
        ArgumentNullException.ThrowIfNull(island);
        ArgumentNullException.ThrowIfNull(system);
        _island = island;
        _system = system;
        island.PropertyChanged += OnIslandChanged;
        Apply();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _island.PropertyChanged -= OnIslandChanged;
        _system.StopClock();
    }

    private void OnIslandChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IslandViewModel.IsExpanded) && !_disposed)
            Apply();
    }

    private void Apply()
    {
        if (_island.IsExpanded)
            _system.StartClock();
        else
            _system.StopClock();
    }
}
