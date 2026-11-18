using System.Windows;

namespace Lunar.Desktop.Navigation;

/// <summary>
/// Tells the pages that they sit in the open Notch (ADR-0027, amended 2026-11-18): a wide, short panel. The value is inherited from the swiper down to
/// every page, and a page that has a compact layout switches to it (for example two columns instead of one tall stack), so it fits without
/// scrolling. Pages read it in <see cref="FrameworkElement.OnPropertyChanged"/>, so there is nothing to subscribe to or let go of.
/// </summary>
public static class CompactLayout
{
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.RegisterAttached(
        "IsCompact", typeof(bool), typeof(CompactLayout), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsCompact(DependencyObject element) => (bool)element.GetValue(IsCompactProperty);

    public static void SetIsCompact(DependencyObject element, bool value) => element.SetValue(IsCompactProperty, value);
}
