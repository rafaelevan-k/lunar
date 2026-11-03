using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Lunar.Desktop.Controls;

/// <summary>
/// A round button that shows only a glyph (FE-12). Because an icon alone tells a screen reader nothing, it requires a
/// <see cref="Label"/>: the label becomes the accessible name and, unless a tooltip was set, the tooltip. It is a
/// normal tab stop and reacts to Space and Enter like any button.
/// </summary>
public sealed class IconButton : Button
{
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(IconButton), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(IconButton), new PropertyMetadata(string.Empty, OnLabelChanged));

    static IconButton() =>
        DefaultStyleKeyProperty.OverrideMetadata(typeof(IconButton), new FrameworkPropertyMetadata(typeof(IconButton)));

    /// <summary>The icon, as text: a Segoe Fluent Icons or MDL2 character, or a plain symbol.</summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>What the button does, in words. Required: it is the accessible name and the tooltip.</summary>
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    private static void OnLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var button = (IconButton)d;
        var label = (string?)e.NewValue ?? string.Empty;
        AutomationProperties.SetName(button, label);
        if (button.ReadLocalValue(ToolTipProperty) == DependencyProperty.UnsetValue || Equals(button.ToolTip, e.OldValue))
            button.ToolTip = string.IsNullOrWhiteSpace(label) ? null : label;
    }
}
