using System.Windows.Threading;

namespace Lunar.Desktop.UITests;

internal static class StaDispatcher
{
    /// <summary>
    /// Shuts down the dispatcher of the calling STA test thread before the thread ends. A dispatcher left running keeps
    /// WPF's input threads alive, so the test process would not exit by itself and xUnit would report a fatal
    /// "foreground threads were left running" error (seen on CI) even though every test passed.
    /// </summary>
    public static void ShutDownCurrent()
    {
        Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        Dispatcher.Run();
    }
}
