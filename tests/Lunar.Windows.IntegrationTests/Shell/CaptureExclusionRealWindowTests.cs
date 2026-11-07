using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Lunar.Domain.Shared;
using Lunar.Windows.Shell;
using Xunit;
using WpfWindow = System.Windows.Window;

namespace Lunar.Windows.IntegrationTests.Shell;

/// <summary>
/// The capture exclusion on real WPF windows on this desktop (ADR-0019, BE-35). Each test runs on its own STA
/// thread with its own dispatcher. The read-back is checked here with a separate GetWindowDisplayAffinity call,
/// and one test samples the screen through GDI to show the window really leaves screen captures.
/// </summary>
public sealed class CaptureExclusionRealWindowTests
{
    private const uint WdaNone = 0;
    private const uint WdaExcludeFromCapture = 0x11;
    private const int GwlExStyle = -20;
    private const long WsExLayered = 0x00080000;
    private const uint WsChild = 0x40000000;
    private const uint WsVisible = 0x10000000;
    private const uint SrcCopy = 0x00CC0020;

    private static readonly WindowsCaptureExclusion Exclusion = new();

    [Fact]
    public void AnOrdinaryWpfWindowIsExcludedAndTheAffinityReadsBack() => RunSta(() =>
    {
        var window = new WpfWindow { Width = 320, Height = 200, Left = 40, Top = 40, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            Assert.Equal(WdaNone, ReadAffinity(hwnd));

            Assert.True(Exclusion.Exclude(hwnd).IsSuccess);

            Assert.Equal(WdaExcludeFromCapture, ReadAffinity(hwnd));
            Assert.True(IsWindowVisible(hwnd));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void ATransparentLayeredTopmostWindowLikeTheIslandIsExcluded() => RunSta(() =>
    {
        var window = IslandLike(Brushes.Transparent);
        window.Show();
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            Assert.NotEqual(0, GetWindowLongPtr(hwnd, GwlExStyle) & WsExLayered);

            Assert.True(Exclusion.Exclude(hwnd).IsSuccess);

            Assert.Equal(WdaExcludeFromCapture, ReadAffinity(hwnd));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void AWindowExcludedAsSoonAsItHasAHandleStaysExcludedWhenShownAndWhenExcludedAgain() => RunSta(() =>
    {
        var window = IslandLike(Brushes.Transparent);
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        try
        {
            Assert.True(Exclusion.Exclude(hwnd).IsSuccess);
            window.Show();
            Pump(TimeSpan.FromMilliseconds(100));
            window.Hide();
            window.Show();

            Assert.Equal(WdaExcludeFromCapture, ReadAffinity(hwnd));
            Assert.True(Exclusion.Exclude(hwnd).IsSuccess);
            Assert.Equal(WdaExcludeFromCapture, ReadAffinity(hwnd));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void AClosedWindowsHandleIsInvalidInputNotAnException() => RunSta(() =>
    {
        var window = new WpfWindow { ShowActivated = false, ShowInTaskbar = false };
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        window.Close();

        var result = Exclusion.Exclude(hwnd);

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
    });

    [Fact]
    public void AnotherProcessesWindowIsRefusedAsATypedFailure()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == 0)
            Assert.Skip("This desktop has no taskbar window to try.");

        var result = Exclusion.Exclude(taskbar);

        Assert.Equal(ErrorCode.AccessDenied, result.Error!.Code);
        Assert.Equal(WdaNone, ReadAffinity(taskbar));
    }

    [Fact]
    public void AChildWindowIsRefusedAsATypedFailure() => RunSta(() =>
    {
        var window = new WpfWindow { Width = 200, Height = 120, Left = 40, Top = 40, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        var parent = new WindowInteropHelper(window).Handle;
        var child = CreateWindowEx(0, "STATIC", null, WsChild | WsVisible, 0, 0, 20, 20, parent, 0, 0, 0);
        try
        {
            Assert.NotEqual(0, child);

            var result = Exclusion.Exclude(child);

            Assert.Equal(ErrorCode.AccessDenied, result.Error!.Code);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void AScreenCaptureNoLongerContainsTheWindowOnceExcluded() => RunSta(() =>
    {
        // A solid magenta island-like window, sampled at its centre through a GDI screen copy.
        var window = IslandLike(new SolidColorBrush(Color.FromRgb(255, 0, 255)));
        window.Left = 120;
        window.Top = 320;
        window.Show();
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            Assert.True(GetWindowRect(hwnd, out var rect));
            var x = (rect.Left + rect.Right) / 2;
            var y = (rect.Top + rect.Bottom) / 2;

            if (!WaitFor(() => IsMagenta(CapturePixel(x, y))))
                Assert.Skip("A screen copy on this desktop does not show the test window, so it cannot show it leaving.");

            Assert.True(Exclusion.Exclude(hwnd).IsSuccess);

            Assert.True(WaitFor(() => !IsMagenta(CapturePixel(x, y))), "The window is still in the screen copy after exclusion.");
            Assert.True(IsWindowVisible(hwnd));
        }
        finally
        {
            window.Close();
        }
    });

    // ---- the process-wide watcher (FE-30) ---------------------------------------------------------------------

    [Fact]
    public void UnderTheWatcherAWindowNobodyExcludedIsExcludedOnceShown() => RunSta(() =>
    {
        var exclusion = new WindowsCaptureExclusion();
        using var watch = exclusion.ExcludeEveryNewWindow().Value;
        var window = IslandLike(Brushes.Transparent);
        window.Show();
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            Assert.True(WaitFor(() => ReadAffinity(hwnd) == WdaExcludeFromCapture), "The watcher did not exclude the window.");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void UnderTheWatcherAPopupLikeATooltipIsExcludedAndNeverSeenInAScreenCopy() => RunSta(() =>
    {
        var exclusion = new WindowsCaptureExclusion();
        using var watch = exclusion.ExcludeEveryNewWindow().Value;
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            Placement = System.Windows.Controls.Primitives.PlacementMode.Absolute,
            HorizontalOffset = 140,
            VerticalOffset = 460,
            AllowsTransparency = true,
            Child = new System.Windows.Controls.Border { Width = 160, Height = 60, Background = new SolidColorBrush(Color.FromRgb(255, 0, 255)) },
        };
        popup.IsOpen = true;
        try
        {
            var source = (HwndSource)System.Windows.PresentationSource.FromVisual(popup.Child);
            var hwnd = source.Handle;
            Assert.True(GetWindowRect(hwnd, out var rect));
            var x = (rect.Left + rect.Right) / 2;
            var y = (rect.Top + rect.Bottom) / 2;
            var seen = 0;
            for (var sample = 0; sample < 20; sample++)
            {
                if (IsMagenta(CapturePixel(x, y)))
                    seen++;
                Pump(TimeSpan.FromMilliseconds(25));
            }

            Assert.Equal(0, seen);
            Assert.Equal(WdaExcludeFromCapture, ReadAffinity(hwnd));
        }
        finally
        {
            popup.IsOpen = false;
        }
    });

    [Fact]
    public void UnderTheWatcherAMessageBoxIsExcluded() => RunSta(() =>
    {
        var exclusion = new WindowsCaptureExclusion();
        using var watch = exclusion.ExcludeEveryNewWindow().Value;
        uint? affinity = null;
        var threadId = GetCurrentThreadId();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) =>
        {
            var box = FindDialog(threadId);
            if (box == 0)
                return;
            timer.Stop();
            affinity = GetWindowDisplayAffinity(box, out var value) ? value : null;
            PostMessage(box, WmClose, 0, 0);
        };
        timer.Start();

        System.Windows.MessageBox.Show("Lunar capture test", "Lunar test", System.Windows.MessageBoxButton.OK);

        Assert.Equal(WdaExcludeFromCapture, affinity);
    });

    [Fact]
    public void UnderTheWatcherTheWindowsFilePickerIsExcluded() => RunSta(() =>
    {
        var exclusion = new WindowsCaptureExclusion();
        using var watch = exclusion.ExcludeEveryNewWindow().Value;
        uint? affinity = null;
        var threadId = GetCurrentThreadId();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        var ticks = 0;
        timer.Tick += (_, _) =>
        {
            var picker = FindDialog(threadId);
            if (picker == 0 && ++ticks < 50)
                return;
            timer.Stop();
            if (picker != 0)
            {
                affinity = GetWindowDisplayAffinity(picker, out var value) ? value : null;
                PostMessage(picker, WmClose, 0, 0);
            }
        };
        timer.Start();

        new Microsoft.Win32.OpenFileDialog { Title = "Lunar capture test" }.ShowDialog();

        Assert.Equal(WdaExcludeFromCapture, affinity);
    });

    private const uint WmClose = 0x0010;

    private static nint FindDialog(uint threadId)
    {
        nint found = 0;
        EnumThreadWindows(threadId, (hwnd, _) =>
        {
            var name = new System.Text.StringBuilder(16);
            _ = GetClassName(hwnd, name, name.Capacity);
            if (name.ToString() == "#32770" && IsWindowVisible(hwnd))
            {
                found = hwnd;
                return false;
            }

            return true;
        }, 0);
        return found;
    }

    private delegate bool EnumThreadProc(nint hwnd, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumThreadWindows(uint threadId, EnumThreadProc callback, nint parameter);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, System.Text.StringBuilder name, int capacity);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private static WpfWindow IslandLike(Brush background) => new()
    {
        Width = 240,
        Height = 64,
        Left = 60,
        Top = 60,
        WindowStyle = System.Windows.WindowStyle.None,
        AllowsTransparency = true,
        Background = background,
        Topmost = true,
        ShowActivated = false,
        ShowInTaskbar = false,
        ResizeMode = System.Windows.ResizeMode.NoResize,
    };

    private static bool IsMagenta(uint colorRef) =>
        (colorRef & 0xFF) > 0xF0 && ((colorRef >> 8) & 0xFF) < 0x10 && ((colorRef >> 16) & 0xFF) > 0xF0;

    private static bool WaitFor(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            Pump(TimeSpan.FromMilliseconds(75));
            if (condition())
                return true;
        }

        return false;
    }

    private static uint CapturePixel(int x, int y)
    {
        var screen = GetDC(0);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, 1, 1);
        var old = SelectObject(memory, bitmap);
        try
        {
            Assert.True(BitBlt(memory, 0, 0, 1, 1, screen, x, y, SrcCopy));
            return GetPixel(memory, 0, 0);
        }
        finally
        {
            SelectObject(memory, old);
            DeleteObject(bitmap);
            DeleteDC(memory);
            _ = ReleaseDC(0, screen);
        }
    }

    private static uint ReadAffinity(nint hwnd)
    {
        Assert.True(GetWindowDisplayAffinity(hwnd, out var affinity));
        return affinity;
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void RunSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern long GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string className, string? windowName);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint rop);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(nint hdc, int x, int y);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint hdc);
}
