using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Lunar.Desktop.Accessibility;
using Lunar.Desktop.Shell;

namespace Lunar.Desktop.Features.Usage;

/// <summary>
/// The reset pop-up window (FE-28). Window mechanics only: a borderless, transparent, topmost window that never takes activation (so it
/// cannot steal focus from what the user is typing in), carrying the Claude palette. Placement and timing belong to
/// <see cref="ResetToastPresenter"/> and <see cref="UsageResetNotifier"/>.
/// </summary>
public partial class ResetToastWindow : Window
{
    public ResetToastWindow()
    {
        InitializeComponent();
        var palette = SystemParameters.HighContrast ? "Colors.Usage.HighContrast.xaml" : "Colors.Usage.xaml";
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Lunar.Desktop;component/Themes/" + palette, UriKind.Relative) });
        SourceInitialized += OnSourceInitialized;
        MouseEnter += (_, _) => PointerEntered?.Invoke();
        MouseLeave += (_, _) => PointerLeft?.Invoke();
        MouseLeftButtonUp += (_, _) => CloseRequested?.Invoke();
    }

    internal event Action? CloseRequested;

    internal event Action? PointerEntered;

    internal event Action? PointerLeft;

    /// <summary>
    /// Tells the screen reader about the pop-up. A window that appears without taking focus is not announced by itself, so the message is
    /// sent as a notification from the text element (a no-op when no screen reader listens).
    /// </summary>
    internal void Announce(string text)
    {
        LiveAnnouncer.Announce(MessageText, text, "Lunar.UsageReset", interrupt: true);
    }

    private void OnSourceInitialized(object? sender, EventArgs e) =>
        NonActivatingWindowStyle.Apply(new WindowInteropHelper(this).Handle);

    // The close button raises the same request as a click on the card (the click on the button itself is handled here so it is not counted twice).
    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        CloseRequested?.Invoke();
    }

    protected override void OnClosed(EventArgs e)
    {
        SourceInitialized -= OnSourceInitialized;
        base.OnClosed(e);
    }
}
