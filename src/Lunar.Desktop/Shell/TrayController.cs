using System.Drawing;
using Forms = System.Windows.Forms;

namespace Lunar.Desktop.Shell;

/// <summary>
/// Notification-area icon and its context menu. Owns exactly one icon, removes it when disposed, and only
/// raises the callbacks it is given: it holds no application logic. "Settings" and "Start with Windows" are
/// shown disabled because their features do not exist yet (FE-23 and BE-29); they are not faked.
/// </summary>
internal sealed class TrayController : IDisposable
{
    private const string IconResource = "Lunar.Desktop.Resources.Lunar.ico";

    private readonly Action _open;
    private readonly Action _exit;
    private readonly Icon _iconImage;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.NotifyIcon _icon;

    /// <param name="visible">False only in tests, so no icon appears in the user's notification area.</param>
    internal TrayController(Action open, Action exit, bool visible = true)
    {
        _open = open;
        _exit = exit;

        using var stream = typeof(TrayController).Assembly.GetManifestResourceStream(IconResource)
            ?? throw new InvalidOperationException("The tray icon resource is missing.");
        _iconImage = new Icon(stream);

        OpenItem = new Forms.ToolStripMenuItem("&Open");
        SettingsItem = new Forms.ToolStripMenuItem("&Settings")
        {
            Enabled = false,
            ToolTipText = "Settings are not available yet."
        };
        StartupItem = new Forms.ToolStripMenuItem("Start with &Windows")
        {
            Enabled = false,
            Checked = false,
            ToolTipText = "Starting with Windows is not available yet."
        };
        ExitItem = new Forms.ToolStripMenuItem("E&xit");

        OpenItem.Click += OnOpen;
        ExitItem.Click += OnExit;

        _menu = new Forms.ContextMenuStrip { ShowItemToolTips = true };
        _menu.Items.AddRange([OpenItem, SettingsItem, StartupItem, new Forms.ToolStripSeparator(), ExitItem]);

        _icon = new Forms.NotifyIcon { Icon = _iconImage, Text = "Lunar", ContextMenuStrip = _menu };
        _icon.MouseClick += OnIconClick;
        _icon.Visible = visible;
    }

    internal bool IsDisposed { get; private set; }

    internal Forms.ToolStripMenuItem OpenItem { get; }

    internal Forms.ToolStripMenuItem SettingsItem { get; }

    internal Forms.ToolStripMenuItem StartupItem { get; }

    internal Forms.ToolStripMenuItem ExitItem { get; }

    internal Forms.ContextMenuStrip Menu => _menu;

    public void Dispose()
    {
        if (IsDisposed)
            return;
        IsDisposed = true;

        // Hide first so the icon leaves the notification area immediately instead of lingering.
        _icon.Visible = false;
        _icon.MouseClick -= OnIconClick;
        OpenItem.Click -= OnOpen;
        ExitItem.Click -= OnExit;
        _icon.Dispose();
        _menu.Dispose();
        _iconImage.Dispose();
    }

    private void OnIconClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left)
            _open();
    }

    private void OnOpen(object? sender, EventArgs e) => _open();

    private void OnExit(object? sender, EventArgs e) => _exit();
}
