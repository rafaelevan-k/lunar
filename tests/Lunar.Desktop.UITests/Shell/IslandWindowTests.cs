using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Lunar.Desktop.Shell;
using Xunit;

namespace Lunar.Desktop.UITests.Shell;

// Creates real WPF windows on an STA thread. Windows that are shown are closed before the test ends, and
// ShowActivated=false means they do not take focus. These need an interactive Windows session.
public sealed class IslandWindowTests
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_NOACTIVATE = 0x08000000L;
    private const long WS_EX_TOPMOST = 0x00000008L;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

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
            throw new InvalidOperationException("WPF window check failed.", failure);
    }

    [Fact]
    public void WindowIsBorderlessTransparentTopmostAndNotInTheTaskbar() => RunOnSta(() =>
    {
        var window = new IslandWindow();

        Assert.Equal(WindowStyle.None, window.WindowStyle);
        Assert.True(window.AllowsTransparency);
        Assert.Equal(System.Windows.Media.Brushes.Transparent, window.Background);
        Assert.Equal(ResizeMode.NoResize, window.ResizeMode);
        Assert.True(window.Topmost);
        Assert.False(window.ShowInTaskbar);
        Assert.False(window.ShowActivated);
        Assert.Equal(160d, window.Width);
        Assert.Equal(44d, window.Height);
        Assert.Equal("Lunar", System.Windows.Automation.AutomationProperties.GetName(window));
    });

    [Fact]
    public void PillHostsExactlyOneViewHost() => RunOnSta(() =>
    {
        var window = new IslandWindow();
        var pill = Assert.IsType<Border>(window.Content);
        var host = Assert.IsType<ContentControl>(pill.Child);

        Assert.Null(window.ViewContent);
        var content = new TextBlock();
        window.ViewContent = content;
        Assert.Same(content, host.Content);
        Assert.False(host.Focusable);
    });

    [Fact]
    public void ShownWindowIsNonActivatingTopmostAndClosesCleanly() => RunOnSta(() =>
    {
        var window = new IslandWindow();
        var closed = false;
        window.Closed += (_, _) => closed = true;

        window.Show();
        var handle = new WindowInteropHelper(window).Handle;
        var style = GetWindowLongPtr(handle, GWL_EXSTYLE).ToInt64();

        Assert.NotEqual(IntPtr.Zero, handle);
        Assert.NotEqual(0, style & WS_EX_NOACTIVATE);
        Assert.NotEqual(0, style & WS_EX_TOPMOST);
        Assert.False(window.IsActive);

        window.Close();

        Assert.True(closed);
    });
}
