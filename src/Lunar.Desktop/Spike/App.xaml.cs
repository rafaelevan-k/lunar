using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lunar.Desktop.Shell;

namespace Lunar.Desktop;

public partial class App : System.Windows.Application
{
    private StreamWriter? evidence;
    private TrayController? tray;
    private DispatcherTimer? smokeTimer;
    private string? snapshotPath;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            // Startup is the sole composition root. Constructor injection, no service locator.
            var smoke = e.Args.Length == 2 && e.Args[0] == "--smoke";
            if (e.Args.Length != 0 && !smoke) throw new ArgumentException("Use --smoke <new-jsonl-file>");
            if (smoke)
            {
                evidence = new StreamWriter(new FileStream(e.Args[1], FileMode.CreateNew, FileAccess.Write, FileShare.Read));
                snapshotPath = Path.ChangeExtension(e.Args[1], ".png");
            }
            DispatcherUnhandledException += OnDispatcherFailure;
            var window = new SpikeWindow(Record);
            MainWindow = window;
            tray = new TrayController(window, () => Shutdown());
            window.Show();
            if (smoke)
            {
                smokeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                smokeTimer.Tick += OnSmokeComplete;
                smokeTimer.Start();
            }
        }
        catch (Exception ex)
        {
            Record("StartupFailed", new { ErrorType = ex.GetType().Name, HResult = $"0x{ex.HResult:X8}" });
            Shutdown(1);
        }
    }

    private void OnSmokeComplete(object? sender, EventArgs e)
    {
        smokeTimer?.Stop();
        if (MainWindow is not null && snapshotPath is not null)
        {
            var dpi = VisualTreeHelper.GetDpi(MainWindow);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(MainWindow.ActualWidth * dpi.DpiScaleX),
                (int)Math.Ceiling(MainWindow.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY,
                PixelFormats.Pbgra32);
            bitmap.Render(MainWindow);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = new FileStream(snapshotPath, FileMode.CreateNew, FileAccess.Write);
            encoder.Save(output);
            Record("WpfRenderSnapshot", new { bitmap.PixelWidth, bitmap.PixelHeight });
        }
        Record("SmokeIntervalElapsed", new { Seconds = 5 });
        Shutdown();
    }

    private void OnDispatcherFailure(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Record("DispatcherFailed", new { ErrorType = e.Exception.GetType().Name, HResult = $"0x{e.Exception.HResult:X8}" });
        e.Handled = true;
        Shutdown(1);
    }

    private void Record(string operation, object result)
    {
        if (evidence is null) return;
        evidence.WriteLine(JsonSerializer.Serialize(new { Timestamp = DateTimeOffset.UtcNow, Operation = operation, Result = result }));
        evidence.Flush();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (smokeTimer is not null)
        {
            smokeTimer.Stop();
            smokeTimer.Tick -= OnSmokeComplete;
        }
        tray?.Dispose();
        Record("Exit", new { e.ApplicationExitCode, TrayDisposed = tray?.IsDisposed });
        DispatcherUnhandledException -= OnDispatcherFailure;
        evidence?.Dispose();
        base.OnExit(e);
    }
}
