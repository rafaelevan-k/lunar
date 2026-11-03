using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Lunar.Desktop.Controls;

/// <summary>The meaning of a <see cref="StatusChip"/>. Each kind has its own symbol, so colour is never the only signal.</summary>
public enum StatusKind
{
    Neutral,
    Info,
    Success,
    Error,
    Unavailable
}

/// <summary>
/// A small read-only label for a state such as "Connected" or "Unavailable" (FE-12): a symbol that differs per kind plus
/// the text. Not a tab stop. Its text is the accessible name and it is a polite live region, so a change of status is
/// announced without taking focus.
/// </summary>
public sealed class StatusChip : Control
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(StatusChip), new PropertyMetadata(string.Empty, OnTextChanged));

    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(nameof(Kind), typeof(StatusKind), typeof(StatusChip), new PropertyMetadata(StatusKind.Neutral, OnKindChanged));

    private static readonly DependencyPropertyKey GlyphPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(Glyph), typeof(string), typeof(StatusChip), new PropertyMetadata(GlyphFor(StatusKind.Neutral)));

    public static readonly DependencyProperty GlyphProperty = GlyphPropertyKey.DependencyProperty;

    static StatusChip() =>
        DefaultStyleKeyProperty.OverrideMetadata(typeof(StatusChip), new FrameworkPropertyMetadata(typeof(StatusChip)));

    public StatusChip()
    {
        Focusable = false;
        IsTabStop = false;
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public StatusKind Kind
    {
        get => (StatusKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>The symbol for <see cref="Kind"/>.</summary>
    public string Glyph => (string)GetValue(GlyphProperty);

    internal static string GlyphFor(StatusKind kind) => kind switch
    {
        StatusKind.Info => "i",
        StatusKind.Success => "✓",
        StatusKind.Error => "!",
        StatusKind.Unavailable => "–",
        _ => "•"
    };

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        AutomationProperties.SetName(d, (string?)e.NewValue ?? string.Empty);

    private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        d.SetValue(GlyphPropertyKey, GlyphFor((StatusKind)e.NewValue));
}
