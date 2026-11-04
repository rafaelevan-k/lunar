using System.ComponentModel;
using Lunar.Application.Shell.Models;
using Lunar.Desktop.Shell;

namespace Lunar.Desktop.Features.Converter;

/// <summary>
/// Keeps the island open while the file dialog is up or files are being dragged over the Converter page (PRD section 3: no
/// auto-collapse while a dialog is open or a drag/drop is active). It only passes the page's two flags to the island's guards.
/// Use on the UI thread; dispose to let go (the guards are cleared).
/// </summary>
internal sealed class ConverterGuardLink : IDisposable
{
    private readonly IslandViewModel _island;
    private readonly ConverterPageViewModel _page;
    private bool _disposed;

    public ConverterGuardLink(IslandViewModel island, ConverterPageViewModel page)
    {
        ArgumentNullException.ThrowIfNull(island);
        ArgumentNullException.ThrowIfNull(page);
        _island = island;
        _page = page;
        page.PropertyChanged += OnPageChanged;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _page.PropertyChanged -= OnPageChanged;
        _island.SetGuards(IslandGuards.None);
    }

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_disposed && e.PropertyName is nameof(ConverterPageViewModel.IsDialogOpen) or nameof(ConverterPageViewModel.IsDragActive))
            _island.SetGuards(new IslandGuards { DialogOpen = _page.IsDialogOpen, DragActive = _page.IsDragActive });
    }
}
