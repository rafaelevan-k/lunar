using System.Windows;
using System.Windows.Interop;
using Lunar.Desktop.Accessibility;
using Lunar.Desktop.Shell;

namespace Lunar.Desktop.Features.Updates;

/// <summary>
/// The new-version prompt window (FE-38). Window mechanics only: borderless, transparent, topmost and never activated, so it cannot take focus from
/// what the user is typing in; its buttons still answer the mouse. Placement and when it shows belong to <see cref="UpdatePromptPresenter"/>.
/// </summary>
public partial class UpdatePromptWindow : Window
{
    public UpdatePromptWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    /// <summary>A window that appears without focus is not announced by itself, so the words are sent as a notification.</summary>
    internal void Announce(string text) => LiveAnnouncer.Announce(MessageText, text, "Lunar.Update", interrupt: true);

    private void OnSourceInitialized(object? sender, EventArgs e) =>
        NonActivatingWindowStyle.Apply(new WindowInteropHelper(this).Handle);

    protected override void OnClosed(EventArgs e)
    {
        SourceInitialized -= OnSourceInitialized;
        base.OnClosed(e);
    }
}
