using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Lunar.Desktop.Shell;

public partial class SpikeWindow : Window
{
    private readonly Action<string, object> record;
    private HwndSource? source;
    private bool keyboardMode;

    internal SpikeWindow(Action<string, object> record)
    {
        this.record = record;
        InitializeComponent();
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + 8;
        SourceInitialized += OnSourceInitialized;
        ContentRendered += OnContentRendered;
        DpiChanged += OnDpiChanged;
        KeyDown += OnKeyDown;
        Closed += OnClosed;
    }

    internal void OpenForKeyboard()
    {
        keyboardMode = true;
        Show();
        Activate();
        Focus();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        source?.AddHook(WindowMessage);
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0021 && !keyboardMode) // WM_MOUSEACTIVATE; do not steal foreground on idle click.
        {
            handled = true;
            return new IntPtr(3); // MA_NOACTIVATE
        }
        return IntPtr.Zero;
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        record("WindowRendered", new { AllowsTransparency, Topmost, ShowInTaskbar, ShowActivated,
            Width = ActualWidth, Height = ActualHeight, Left, Top,
            DpiX = dpi.PixelsPerInchX, DpiY = dpi.PixelsPerInchY,
            IsActive, RenderTier = RenderCapability.Tier >> 16 });
    }

    private void OnDpiChanged(object sender, DpiChangedEventArgs e) => record("DpiChanged",
        new { From = e.OldDpi.PixelsPerInchX, To = e.NewDpi.PixelsPerInchX });

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        source?.RemoveHook(WindowMessage);
        SourceInitialized -= OnSourceInitialized;
        ContentRendered -= OnContentRendered;
        DpiChanged -= OnDpiChanged;
        KeyDown -= OnKeyDown;
        Closed -= OnClosed;
    }
}
