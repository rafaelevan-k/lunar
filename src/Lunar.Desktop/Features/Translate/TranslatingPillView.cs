using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Lunar.Desktop.Controls;
using Lunar.Desktop.Resources;

namespace Lunar.Desktop.Features.Translate;

/// <summary>
/// The collapsed island while the screen is being read and translated (ADR-0028 §12): "Translating…" and a Cancel button, in the normal pill. Cancel
/// never opens the island; the rest of the pill does nothing, because there is nothing to open yet.
/// </summary>
public sealed class TranslatingPillView : UserControl
{
    public TranslatingPillView(ICommand cancel)
    {
        ArgumentNullException.ThrowIfNull(cancel);
        Focusable = false;
        IsTabStop = false;
        Background = System.Windows.Media.Brushes.Transparent;
        AutomationProperties.SetName(this, Strings.Get("Translate.Working"));

        var grid = new Grid { Margin = new Thickness(16, 0, 9, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new TextBlock { Text = Strings.Get("Translate.Working"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        text.SetResourceReference(StyleProperty, "Lunar.Text.Body");
        grid.Children.Add(text);
        CancelButton = new IconButton { Width = 28, Height = 28, Glyph = "", Label = Strings.Get("Translate.Cancel"), Command = cancel, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(CancelButton, 1);
        grid.Children.Add(CancelButton);
        Content = grid;
    }

    internal IconButton CancelButton { get; }
}
