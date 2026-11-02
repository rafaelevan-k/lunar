using System;
using System.Drawing;
using Forms = System.Windows.Forms;

namespace Lunar.Desktop.Shell;

internal sealed class TrayController : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ContextMenuStrip menu;
    internal bool IsDisposed { get; private set; }

    internal TrayController(SpikeWindow window, Action exit)
    {
        menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open prototype", null, (_, _) => window.OpenForKeyboard());
        menu.Items.Add("Exit", null, (_, _) => exit());
        icon = new Forms.NotifyIcon { Icon = SystemIcons.Application, Text = "Lunar Sprint 0 prototype",
            ContextMenuStrip = menu, Visible = true };
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        icon.Visible = false;
        icon.Dispose();
        menu.Dispose();
        IsDisposed = true;
    }
}
