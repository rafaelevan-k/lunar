using System.Windows;
using System.Windows.Controls;
using Lunar.Desktop.Navigation;

namespace Lunar.Desktop.Features.SystemControls;

/// <summary>
/// The System page (FE-13). Presentation only: the XAML binds to <see cref="SystemPageViewModel"/> and this class holds no
/// logic and calls no Windows API.
/// </summary>
public partial class SystemPage : UserControl
{
    public SystemPage()
    {
        InitializeComponent();
        ApplyLayout(CompactLayout.GetIsCompact(this));
        TranslateButton.IsVisibleChanged += (_, _) => ApplyLayout(IsCompact); // the centring margin follows whether the button is there
    }

    /// <summary>True while the page shows its two-column Notch layout.</summary>
    internal bool IsCompact { get; private set; }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == CompactLayout.IsCompactProperty)
            ApplyLayout((bool)e.NewValue);
    }

    /// <summary>
    /// The open Notch is wide and short (ADR-0027, amended 2026-11-18). There the page needs no scrolling, in the order the owner drew it:
    /// the clock centred on top, Wi-Fi and Bluetooth side by side, and under them one wide card with brightness above volume. The Pill keeps its
    /// layout: the clock at the left, volume above brightness, then Wi-Fi and Bluetooth, clear of its side arrows.
    /// </summary>
    private void ApplyLayout(bool compact)
    {
        IsCompact = compact;
        // The same side and bottom room as the other pages (40 and 6 DIP), in both styles (2026-11-18, the owner).
        PageScroll.Margin = compact ? new Thickness(40, 0, 40, 6) : new Thickness(38, 0, 38, 0);

        // The open Notch centres the time and date under the eyes (2026-11-18, the owner); the Translate button keeps its place at the right, and an
        // equal margin at the left keeps the clock in the true centre.
        Clock.HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        var balance = TranslateButton.Visibility == Visibility.Visible ? TranslateButton.Width + TranslateButton.Margin.Left : 0;
        Header.Margin = compact ? new Thickness(balance, 0, 0, 4) : new Thickness(0, 0, 0, 4);

        // Rows: Wi-Fi and Bluetooth first in the Notch, the volume card first in the Pill.
        Grid.SetRow(Radios, compact ? 1 : 2);
        Grid.SetRow(VolumeCard, compact ? 2 : 1);
        Radios.Margin = compact ? new Thickness(0, 0, 0, 6) : new Thickness(0);
        VolumeCard.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 0, 4);

        // Inside the card: brightness above volume in the Notch.
        VolumeStack.Children.Remove(BrightnessRow);
        VolumeStack.Children.Insert(compact ? 0 : 1, BrightnessRow);
        BrightnessRow.Margin = compact ? new Thickness(0, 0, 0, 4) : new Thickness(0, 4, 0, 0);
    }
}
