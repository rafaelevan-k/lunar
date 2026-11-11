using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Shell;
using Lunar.Application.Settings.Models;
using Lunar.Application.Shell.Models;
using Lunar.Desktop.Features.Eyes;
using Lunar.Desktop.Navigation;
using Lunar.Desktop.Shell;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Desktop.UITests.Shell;

/// <summary>
/// FE-33: what really reaches the screen while the island expands and collapses. A real island window (not excluded from capture here) has
/// magenta eyes on a green pill, and a second thread copies the top of the screen as fast as it can. The eyes are centred on the display
/// in every state, so any copy in which they are not is a frame the user saw in the wrong place.
/// </summary>
public sealed class IslandPillFrameTests
{
    private const int CaptureWidth = 900;
    private const int CaptureHeight = 140;
    private const int CenterTolerance = 4;
    private const int Cycles = 8;

    [Fact]
    public void TheEyesStayCentredInEveryFrameOfAnExpandAndCollapse()
    {
        var report = new StringBuilder();
        var bad = 0;
        var seenEyes = 0;
        RunOnSta(() =>
        {
            using var island = Island.Show(new IslandSettings());

            const int screenCenter = 960;
            var left = screenCenter - (CaptureWidth / 2);
            var stop = new ManualResetEventSlim();
            var capture = new Thread(() =>
            {
                using var grabber = new ScreenGrabber(left, 0, CaptureWidth, CaptureHeight);
                while (!stop.IsSet)
                {
                    var frame = grabber.Grab();
                    if (frame.EyeCount == 0)
                        continue;
                    seenEyes++;
                    if (Math.Abs(left + ((frame.EyeMin + frame.EyeMax) / 2.0) - screenCenter) <= CenterTolerance)
                        continue;
                    bad++;
                    report.AppendLine($"eyes x {left + frame.EyeMin}..{left + frame.EyeMax}, pill x {left + frame.PillMin}..{left + frame.PillMax}");
                }
            });
            capture.Start();
            try
            {
                for (var cycle = 0; cycle < Cycles; cycle++)
                {
                    island.ViewModel.Toggle();
                    Pump(() => island.ViewModel.State == IslandVisualState.Expanded, 3000);
                    Pump(() => false, 120);
                    island.ViewModel.Toggle();
                    Pump(() => island.ViewModel.State == IslandVisualState.Idle, 3000);
                    Pump(() => false, 120);
                }
            }
            finally
            {
                stop.Set();
                capture.Join();
            }
        });

        if (seenEyes == 0)
            Assert.Skip("A screen copy on this desktop does not show the test window.");
        Assert.True(bad == 0, $"{bad} of {seenEyes} screen copies showed the eyes away from the centre of the display: {report}");
    }

    [Fact]
    public void TheWindowBesideTheIdlePillIsNotThereForTheMouse() => RunOnSta(() =>
    {
        // Lower than usual, so a Lunar the user has running at the top of the screen is not what answers.
        using var island = Island.Show(new IslandSettings { TopOffsetDip = 200 });
        Assert.True(GetWindowRect(island.Handle, out var rect));
        var middleY = (rect.Top + rect.Bottom) / 2;

        var onThePill = WindowFromPoint(new NativePoint((rect.Left + rect.Right) / 2, middleY));
        var besideThePill = WindowFromPoint(new NativePoint(rect.Left + 10, middleY));

        if (onThePill != island.Handle)
            Assert.Skip("This desktop does not report the test window under the pointer, so it cannot show what lies beside it.");
        Assert.NotEqual(island.Handle, besideThePill);
    });

    private sealed class Island : IDisposable
    {
        private readonly IslandWindow _window;
        private readonly IslandShellView _shell;
        private readonly IslandPositionController _position;
        private readonly IslandTransitionController _transition;

        private Island(IslandWindow window, IslandViewModel viewModel, IslandShellView shell, IslandPositionController position, IslandTransitionController transition)
        {
            (_window, ViewModel, _shell, _position, _transition) = (window, viewModel, shell, position, transition);
            Handle = new WindowInteropHelper(window).Handle;
        }

        public IslandViewModel ViewModel { get; }

        public nint Handle { get; }

        // Magenta eyes on a green pill, so a screen copy can tell them apart from anything else on the desktop.
        public static Island Show(IslandSettings settings)
        {
            var dpi = (int)GetDpiForSystem();
            var display = new DisplayInfo("DISPLAY1", true, new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1020), dpi, dpi);
            var viewModel = new IslandViewModel();
            var navigation = new PageNavigationViewModel(["One", "Two", "Three", "Four", "Five", "Six"]);
            var swiper = new SwiperView();
            var shell = new IslandShellView(new EyesView(), swiper, viewModel, reduceMotion: false);
            var window = new IslandWindow { DataContext = viewModel };
            foreach (var key in new[] { "Lunar.Brush.Background", "Lunar.Brush.Surface", "Lunar.Brush.Border" })
                window.Resources[key] = new SolidColorBrush(Color.FromRgb(0, 255, 0));
            window.Resources["Lunar.Brush.TextPrimary"] = new SolidColorBrush(Color.FromRgb(255, 0, 255));
            window.ViewContent = shell;
            swiper.Resources.MergedDictionaries.Clear();
            swiper.Initialize(navigation, IslandShellView.CreatePlaceholderPages(navigation.Titles), false);

            _ = new WindowInteropHelper(window).EnsureHandle();
            var position = new IslandPositionController(window, viewModel, new FakeEnvironment(display), new FakeLog(), settings);
            var transition = new IslandTransitionController(window, viewModel, reduceMotion: false);
            position.Start();
            window.Show();
            Pump(() => false, 500);
            return new Island(window, viewModel, shell, position, transition);
        }

        public void Dispose()
        {
            _transition.Dispose();
            _position.Dispose();
            _shell.Dispose();
            _window.Close();
        }
    }

    private static void Pump(Func<bool> until, int timeoutMs)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!until() && DateTime.UtcNow < end)
        {
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
            Thread.Sleep(1);
        }
    }

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
            throw new InvalidOperationException("The frame check failed.", failure);
    }

    private readonly record struct Frame(int EyeMin, int EyeMax, int EyeCount, int PillMin, int PillMax);

    private sealed class ScreenGrabber : IDisposable
    {
        private const uint SrcCopy = 0x00CC0020;
        private readonly int _x;
        private readonly int _y;
        private readonly int _width;
        private readonly int _height;
        private readonly nint _screen;
        private readonly nint _memory;
        private readonly nint _bitmap;
        private readonly nint _old;
        private readonly byte[] _pixels;
        private BitmapInfoHeader _header;

        public ScreenGrabber(int x, int y, int width, int height)
        {
            (_x, _y, _width, _height) = (x, y, width, height);
            _screen = GetDC(0);
            _memory = CreateCompatibleDC(_screen);
            _bitmap = CreateCompatibleBitmap(_screen, width, height);
            _old = SelectObject(_memory, _bitmap);
            _pixels = new byte[width * height * 4];
            _header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32
            };
        }

        public Frame Grab()
        {
            BitBlt(_memory, 0, 0, _width, _height, _screen, _x, _y, SrcCopy);
            _ = GetDIBits(_memory, _bitmap, 0, (uint)_height, _pixels, ref _header, 0);
            int eyeMin = int.MaxValue, eyeMax = int.MinValue, eyes = 0, pillMin = int.MaxValue, pillMax = int.MinValue;
            for (var row = 0; row < _height; row++)
            {
                for (var column = 0; column < _width; column++)
                {
                    var i = ((row * _width) + column) * 4;
                    var (b, g, r) = (_pixels[i], _pixels[i + 1], _pixels[i + 2]);
                    if (r > 230 && g < 40 && b > 230)
                    {
                        eyes++;
                        eyeMin = Math.Min(eyeMin, column);
                        eyeMax = Math.Max(eyeMax, column);
                    }
                    else if (r < 40 && g > 230 && b < 40)
                    {
                        pillMin = Math.Min(pillMin, column);
                        pillMax = Math.Max(pillMax, column);
                    }
                }
            }

            return new Frame(eyeMin, eyeMax, eyes, pillMin, pillMax);
        }

        public void Dispose()
        {
            SelectObject(_memory, _old);
            DeleteObject(_bitmap);
            DeleteDC(_memory);
            _ = ReleaseDC(0, _screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private sealed class FakeEnvironment(DisplayInfo display) : IWindowEnvironment
    {
        public event Action? DisplaysChanged
        {
            add { }
            remove { }
        }

        public Result Start() => Result.Success();

        public Result<IReadOnlyList<DisplayInfo>> GetDisplays() => Result.Success<IReadOnlyList<DisplayInfo>>([display]);

        public void Dispose()
        {
        }
    }

    private sealed class FakeLog : IDiagnosticLog
    {
        public void Write(DiagnosticEvent diagnosticEvent)
        {
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out Rect rect);

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
    private static extern int GetDIBits(nint hdc, nint bitmap, uint start, uint lines, byte[] bits, ref BitmapInfoHeader info, uint usage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint hdc);
}
