using System.ComponentModel;
using Lunar.Desktop.Navigation;
using Lunar.Desktop.Shell;

namespace Lunar.Desktop.Features.SystemControls;

/// <summary>
/// Tells the brightness card when it can be seen (FE-32): the island is open and the System page is the one shown. Only then does the card
/// re-read and, for an external monitor, read again now and then (ADR-0021). Use on the UI thread; dispose to let go.
/// </summary>
internal sealed class BrightnessLifetime : IDisposable
{
    internal const int SystemPageIndex = 0;

    private readonly IslandViewModel _island;
    private readonly PageNavigationViewModel _navigation;
    private readonly BrightnessCardViewModel _card;
    private bool _disposed;

    public BrightnessLifetime(IslandViewModel island, PageNavigationViewModel navigation, BrightnessCardViewModel card)
    {
        ArgumentNullException.ThrowIfNull(island);
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(card);
        _island = island;
        _navigation = navigation;
        _card = card;
        island.PropertyChanged += OnChanged;
        navigation.PropertyChanged += OnChanged;
        Update();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _island.PropertyChanged -= OnChanged;
        _navigation.PropertyChanged -= OnChanged;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IslandViewModel.IsExpanded) or nameof(PageNavigationViewModel.SelectedIndex))
            Update();
    }

    private void Update()
    {
        if (!_disposed)
            _card.SetVisible(_island.IsExpanded && _navigation.SelectedIndex == SystemPageIndex);
    }
}
