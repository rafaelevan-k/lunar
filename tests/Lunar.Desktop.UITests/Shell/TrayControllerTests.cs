using Lunar.Desktop.Shell;
using Xunit;
using Forms = System.Windows.Forms;

namespace Lunar.Desktop.UITests.Shell;

// Creates the real NotifyIcon and menu on an STA thread but never makes the icon visible, so nothing appears in
// the user's notification area. Click-to-open on the icon itself and keyboard/mouse use of the real tray menu are
// manual checks recorded in the FE-06 evidence.
public sealed class TrayControllerTests
{
    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                StaDispatcher.ShutDownCurrent();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new InvalidOperationException("Tray check failed.", failure);
    }

    [Fact]
    public void MenuListsTheRequiredItemsInOrder() => RunOnSta(() =>
    {
        using var tray = new TrayController(() => { }, () => { }, visible: false);

        var texts = tray.Menu.Items.Cast<Forms.ToolStripItem>().Select(item => item.Text).ToArray();

        Assert.Equal(new[] { "&Open", "&Settings", "Start with &Windows", "", "E&xit" }, texts);
    });

    [Fact]
    public void FeaturesThatDoNotExistYetAreDisabledAndExplained() => RunOnSta(() =>
    {
        using var tray = new TrayController(() => { }, () => { }, visible: false);

        Assert.True(tray.OpenItem.Enabled);
        Assert.True(tray.ExitItem.Enabled);
        Assert.False(tray.SettingsItem.Enabled);
        Assert.False(tray.StartupItem.Enabled);
        Assert.False(tray.StartupItem.Checked);
        Assert.False(string.IsNullOrWhiteSpace(tray.SettingsItem.ToolTipText));
        Assert.False(string.IsNullOrWhiteSpace(tray.StartupItem.ToolTipText));
    });

    [Fact]
    public void EveryMenuItemHasAUniqueAccessKey() => RunOnSta(() =>
    {
        using var tray = new TrayController(() => { }, () => { }, visible: false);

        var keys = tray.Menu.Items.OfType<Forms.ToolStripMenuItem>()
            .Select(item => char.ToUpperInvariant(item.Text![item.Text.IndexOf('&') + 1]))
            .ToArray();

        Assert.Equal(4, keys.Length);
        Assert.Equal(keys.Length, keys.Distinct().Count());
    });

    [Fact]
    public void OpenAndExitInvokeTheirCallbacksExactlyOnce() => RunOnSta(() =>
    {
        int opened = 0, exited = 0;
        using var tray = new TrayController(() => opened++, () => exited++, visible: false);

        tray.OpenItem.PerformClick();
        Assert.Equal((1, 0), (opened, exited));

        tray.ExitItem.PerformClick();
        Assert.Equal((1, 1), (opened, exited));

        tray.SettingsItem.PerformClick();
        tray.StartupItem.PerformClick();
        Assert.Equal((1, 1), (opened, exited));
    });

    [Fact]
    public void DisposeIsIdempotentAndStopsCallbacks() => RunOnSta(() =>
    {
        var exited = 0;
        var tray = new TrayController(() => { }, () => exited++, visible: false);

        tray.Dispose();
        tray.Dispose();
        tray.ExitItem.PerformClick();

        Assert.True(tray.IsDisposed);
        Assert.Equal(0, exited);
    });
}
