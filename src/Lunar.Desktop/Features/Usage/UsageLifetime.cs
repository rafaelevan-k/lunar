using System.ComponentModel;
using Lunar.Desktop.Shell;

namespace Lunar.Desktop.Features.Usage;

/// <summary>
/// Tells the Usage page whether the island is open (FE-27), which sets how often the monitor looks: more often while the island is expanded,
/// slowly while it is only the pill. It starts and stops nothing itself; the page decides that from the opt-in switch. Use on the UI thread;
/// dispose to let go.
/// </summary>
internal sealed class UsageLifetime : IDisposable
{
    private readonly IslandViewModel _island;
    private readonly UsagePageViewModel _usage;
    private bool _disposed;

    public UsageLifetime(IslandViewModel island, UsagePageViewModel usage)
    {
        ArgumentNullException.ThrowIfNull(island);
        ArgumentNullException.ThrowIfNull(usage);
        _island = island;
        _usage = usage;
        island.PropertyChanged += OnIslandChanged;
        usage.SetIslandOpen(island.IsExpanded);
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
            _usage.SetIslandOpen(_island.IsExpanded);
    }
}
