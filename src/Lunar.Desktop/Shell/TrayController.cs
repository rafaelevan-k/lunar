using System.Drawing;
using Lunar.Desktop.Resources;
using Forms = System.Windows.Forms;

namespace Lunar.Desktop.Shell;

/// <summary>
/// Notification-area icon and its context menu. Owns exactly one icon, removes it when disposed, and only
/// raises the callbacks it is given: it holds no application logic. "Settings" opens the Settings window (FE-23). "Start with Windows" shows
/// and flips the shared <see cref="Features.Settings.StartupSwitch"/> (read again each time the menu opens); without one it is disabled.
/// </summary>
internal sealed class TrayController : IDisposable
{
    private const string IconResource = "Lunar.Desktop.Resources.Lunar.ico";

    private readonly Action _open;
    private readonly Action _exit;
    private readonly Action? _openSettings;
    private readonly Features.Settings.StartupSwitch? _startup;
    private readonly Icon _iconImage;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.NotifyIcon _icon;

    /// <param name="visible">False only in tests, so no icon appears in the user's notification area.</param>
    /// <param name="openSettings">Opens the Settings window; when null the item stays disabled and says so.</param>
    /// <param name="startup">The shared Start with Windows switch; when null the item stays disabled and says so.</param>
    internal TrayController(Action open, Action exit, bool visible = true, Action? openSettings = null,
        Features.Settings.StartupSwitch? startup = null)
    {
        _open = open;
        _exit = exit;
        _openSettings = openSettings;
        _startup = startup;

        using var stream = typeof(TrayController).Assembly.GetManifestResourceStream(IconResource)
            ?? throw new InvalidOperationException("The tray icon resource is missing.");
        _iconImage = new Icon(stream);

        OpenItem = new Forms.ToolStripMenuItem(Strings.Get("Tray.Open"));
        SettingsItem = new Forms.ToolStripMenuItem(Strings.Get("Tray.Settings"))
        {
            Enabled = openSettings is not null,
            ToolTipText = Strings.Get(openSettings is null ? "Tray.SettingsMissingTip" : "Tray.SettingsTip")
        };
        StartupItem = new Forms.ToolStripMenuItem(Strings.Get("Tray.Startup"));
        ShowStartup();
        ExitItem = new Forms.ToolStripMenuItem(Strings.Get("Tray.Exit"));

        OpenItem.Click += OnOpen;
        SettingsItem.Click += OnSettings;
        StartupItem.Click += OnStartup;
        ExitItem.Click += OnExit;

        _menu = new Forms.ContextMenuStrip { ShowItemToolTips = true };
        _menu.Opening += OnMenuOpening;
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
        SettingsItem.Click -= OnSettings;
        StartupItem.Click -= OnStartup;
        ExitItem.Click -= OnExit;
        _menu.Opening -= OnMenuOpening;
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

    private void OnSettings(object? sender, EventArgs e) => _openSettings?.Invoke();

    private void OnExit(object? sender, EventArgs e) => _exit();

    // Windows may have changed it meanwhile (Task Manager, or the Settings window): read it again before the menu shows.
    private void OnMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _startup?.Refresh();
        ShowStartup();
    }

    private void OnStartup(object? sender, EventArgs e)
    {
        if (_startup is null)
            return;
        var result = _startup.Toggle();
        ShowStartup(failed: result.IsFailure);
    }

    private void ShowStartup(bool failed = false)
    {
        var available = _startup?.IsAvailable == true;
        StartupItem.Enabled = available;
        StartupItem.Checked = available && _startup!.IsOn;
        StartupItem.ToolTipText = Strings.Get(failed ? "Tray.StartupFailedTip" : available ? "Tray.StartupTip" : "Tray.StartupUnavailableTip");
    }
}
