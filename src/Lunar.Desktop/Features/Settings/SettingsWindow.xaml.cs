using System.Windows;
using System.Windows.Input;

namespace Lunar.Desktop.Features.Settings;

/// <summary>
/// The Settings window (FE-23): an ordinary window with full keyboard focus (the island cannot host text or choices, ADR-0007). It only
/// shows <see cref="SettingsViewModel"/>; it saves, reads and decides nothing itself. Escape closes it; it refreshes the display list
/// whenever it is brought forward, so a monitor plugged in meanwhile appears.
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        Activated += OnActivated;
        PreviewKeyDown += OnPreviewKeyDown;
        Closed += OnClosed;
    }

    private SettingsViewModel? Model => DataContext as SettingsViewModel;

    private void OnActivated(object? sender, EventArgs e) => Model?.RefreshDisplays();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !e.Handled)
        {
            e.Handled = true;
            Close();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Activated -= OnActivated;
        PreviewKeyDown -= OnPreviewKeyDown;
        Closed -= OnClosed;
    }
}
