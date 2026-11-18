using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

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
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => ApplySoftPalette();
    }

    private ResourceDictionary? _soft;
    private INotifyPropertyChanged? _source;

    /// <summary>The soft palette in use ("Dark" or "Light"), or null in High Contrast or before it is loaded. For tests.</summary>
    internal string? SoftPalette { get; private set; }

    /// <summary>
    /// The Settings window uses its own soft black and white (2026-11-18, the owner's request): charcoal or paper grey, never near-black with pure
    /// white. Which of the two follows the theme the rest of Lunar uses, read from the app's background colour; High Contrast keeps Windows' colours.
    /// The choice is made again after every settings change, because the theme may just have changed.
    /// </summary>
    internal void ApplySoftPalette()
    {
        if (_soft is not null)
        {
            Resources.MergedDictionaries.Remove(_soft);
            _soft = null;
            SoftPalette = null;
        }

        if (SystemParameters.HighContrast)
            return;
        var dark = System.Windows.Application.Current?.TryFindResource("Lunar.Color.Background") is not Color background || Luminance(background) < 0.5;
        var name = dark ? "Dark" : "Light";
        try
        {
            _soft = new ResourceDictionary { Source = new Uri($"/Lunar.Desktop;component/Themes/Colors.Settings.{name}.xaml", UriKind.Relative) };
        }
        catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or System.Windows.Markup.XamlParseException)
        {
            return; // without its palette the window keeps the Lunar one
        }

        Resources.MergedDictionaries.Add(_soft);
        SoftPalette = name;
    }

    private static double Luminance(Color c) => ((0.2126 * c.R) + (0.7152 * c.G) + (0.0722 * c.B)) / 255.0;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_source is not null)
            _source.PropertyChanged -= OnModelChanged;
        _source = e.NewValue as INotifyPropertyChanged;
        if (_source is not null)
            _source.PropertyChanged += OnModelChanged;
    }

    // After the theme applier has swapped the app's palette for this change.
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, ApplySoftPalette);

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
        DataContextChanged -= OnDataContextChanged;
        if (_source is not null)
            _source.PropertyChanged -= OnModelChanged;
    }
}
