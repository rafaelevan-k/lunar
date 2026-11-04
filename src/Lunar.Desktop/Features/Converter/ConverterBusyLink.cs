using System.ComponentModel;
using Lunar.Desktop.Shell;

namespace Lunar.Desktop.Features.Converter;

/// <summary>
/// Tells the island whether a conversion is running (PRD section 3: the Busy state), so the pill can show it while collapsed. It only
/// reports the flag: collapsing never cancels or pauses a conversion, because the queue belongs to the application, not to the island.
/// Use on the UI thread; dispose to let go (the island is told nothing is running).
/// </summary>
internal sealed class ConverterBusyLink : IDisposable
{
    private readonly IslandViewModel _island;
    private readonly ConverterQueueViewModel _queue;
    private bool _disposed;

    public ConverterBusyLink(IslandViewModel island, ConverterQueueViewModel queue)
    {
        ArgumentNullException.ThrowIfNull(island);
        ArgumentNullException.ThrowIfNull(queue);
        _island = island;
        _queue = queue;
        queue.PropertyChanged += OnQueueChanged;
        island.SetBusy(queue.IsBusy);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _queue.PropertyChanged -= OnQueueChanged;
        _island.SetBusy(false);
    }

    private void OnQueueChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_disposed && e.PropertyName == nameof(ConverterQueueViewModel.IsBusy))
            _island.SetBusy(_queue.IsBusy);
    }
}
