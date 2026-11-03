using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Shell;
using Lunar.Application.Settings.Models;
using Lunar.Application.Shell;
using Lunar.Application.Shell.Models;
using Lunar.Desktop.Shell;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Desktop.UITests.Shell;

// Rules are pure; the controller cases use real WPF windows on an STA thread and a fake display environment.
public sealed class IslandLayoutTests
{
    private static DisplayInfo Display(int width, int height, int dpi, int taskbar = 40) =>
        new("\\\\.\\DISPLAY1", true, new PixelRect(0, 0, width, height), new PixelRect(0, 0, width, height - taskbar), dpi, dpi);

    [Fact]
    public void IdleIsAFixedPill()
    {
        var limits = IslandLayout.LimitsFor(false, Display(1920, 1080, 96), 8);

        Assert.Equal(new IslandLayoutLimits(160, 44, 160, 44), limits);
        Assert.Equal((160d, 44d), IslandLayout.Fit(limits, 500, 500));
        Assert.Equal((160d, 44d), IslandLayout.Fit(limits, 0, 0));
    }

    [Fact]
    public void ExpandedIsAtLeastTheDefaultAndGrowsToFitContent()
    {
        var limits = IslandLayout.LimitsFor(true, Display(1920, 1080, 96), 8);

        Assert.Equal(380, limits.MinWidth);
        Assert.Equal(320, limits.MinHeight);
        Assert.Equal((380d, 320d), IslandLayout.Fit(limits, 100, 100)); // small content: default size
        Assert.Equal((500d, 400d), IslandLayout.Fit(limits, 500, 400)); // larger content: grows
    }

    [Fact]
    public void ExpandedNeverExceedsTheWorkAreaMinusMargins()
    {
        var display = Display(1920, 1080, 96); // work area 1920 x 1040 DIPs at 100%
        var limits = IslandLayout.LimitsFor(true, display, 8);

        Assert.Equal(1920 - (2 * IslandLayout.EdgeMargin), limits.MaxWidth);
        Assert.Equal(1040 - 8 - IslandLayout.EdgeMargin, limits.MaxHeight);
        Assert.Equal((limits.MaxWidth, limits.MaxHeight), IslandLayout.Fit(limits, 99999, 99999));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-5)]
    public void InvalidContentSizesCountAsZero(double size)
    {
        var limits = IslandLayout.LimitsFor(true, Display(1920, 1080, 96), 8);

        Assert.Equal((380d, 320d), IslandLayout.Fit(limits, size, size));
    }

    [Fact]
    public void TinyWorkAreaShrinksTheMinimumButNeverBelowThePill()
    {
        var tiny = IslandLayout.LimitsFor(true, Display(300, 200, 96, taskbar: 0), 8);

        Assert.Equal(300 - (2 * IslandLayout.EdgeMargin), tiny.MaxWidth);
        Assert.Equal(tiny.MaxWidth, tiny.MinWidth); // 284 < 380: the minimum follows the maximum
        Assert.True(tiny.MaxHeight >= IslandLayout.IdleHeight);

        var absurd = IslandLayout.LimitsFor(true, Display(100, 60, 96, taskbar: 0), 8);

        Assert.Equal(IslandLayout.IdleWidth, absurd.MaxWidth);
        Assert.Equal(IslandLayout.IdleHeight, absurd.MaxHeight);
    }

    [Fact]
    public void InvalidArgumentsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => IslandLayout.LimitsFor(true, null!, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => IslandLayout.LimitsFor(true, Display(1920, 1080, 96), -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => IslandLayout.LimitsFor(true, Display(1920, 1080, 96), double.NaN));
        Assert.Throws<ArgumentNullException>(() => IslandLayout.Fit(null!, 1, 1));
    }

    // 100, 125, 150 and 200% on common work areas, idle and expanded, with content that fits, grows or is huge.
    // The physical rectangle must fit the work area without PlaceTopCenter having to shrink it (shrinking = clipping).
    [Theory]
    [InlineData(96, 1920, 1080)]
    [InlineData(120, 1920, 1080)]
    [InlineData(144, 1920, 1080)]
    [InlineData(192, 1920, 1080)]
    [InlineData(96, 1366, 768)]
    [InlineData(120, 1366, 768)]
    [InlineData(144, 1366, 768)]
    [InlineData(192, 1366, 768)]
    [InlineData(192, 1280, 720)]
    [InlineData(192, 1024, 600)]
    [InlineData(144, 800, 600)]
    [InlineData(192, 3840, 2160)]
    public void NothingIsClippedAtAnyScale(int dpi, int width, int height)
    {
        var display = Display(width, height, dpi);
        foreach (var expanded in new[] { false, true })
        {
            foreach (var content in new[] { (0d, 0d), (400d, 350d), (5000d, 5000d) })
            {
                var limits = IslandLayout.LimitsFor(expanded, display, 8);
                var (w, h) = IslandLayout.Fit(limits, content.Item1, content.Item2);

                var rect = IslandPlacement.PlaceTopCenter(display, w, h, 8);
                var expectedWidth = (int)Math.Round(w * display.ScaleX, MidpointRounding.AwayFromZero);
                var expectedHeight = (int)Math.Round(h * display.ScaleY, MidpointRounding.AwayFromZero);

                Assert.Equal(expectedWidth, rect.Width);
                Assert.Equal(expectedHeight, rect.Height);
                Assert.True(display.WorkArea.Contains(rect));
            }
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
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new InvalidOperationException("WPF layout check failed.", failure);
    }

    // The tests do not run App.xaml, so the shape tokens the window refers to are added to the window itself.
    private static void LoadSpacing(IslandWindow window)
    {
        using var stream = System.IO.File.OpenRead(System.IO.Path.Combine(Themes.ThemeTokenTests.DesktopDirectory, "Themes", "Spacing.xaml"));
        window.Resources.MergedDictionaries.Add((ResourceDictionary)System.Windows.Markup.XamlReader.Load(stream));
    }

    private static (IslandWindow Window, IslandViewModel ViewModel, IslandPositionController Controller, double Scale) Start(
        IReadOnlyList<DisplayInfo>? displaysForScale = null)
    {
        var window = new IslandWindow();
        var viewModel = new IslandViewModel();
        window.DataContext = viewModel;
        LoadSpacing(window);
        _ = new WindowInteropHelper(window).EnsureHandle();
        var scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        var dpi = (int)Math.Round(scale * 96);
        var displays = displaysForScale ?? [Display(1920, 1080, dpi)];
        var controller = new IslandPositionController(window, viewModel, new FakeEnvironment(displays), new FakeLog(), new IslandSettings());
        controller.Start();
        return (window, viewModel, controller, scale);
    }

    [Fact]
    public void WindowStartsAsTheIdlePillAndGrowsWhenTheLayoutExpands() => RunOnSta(() =>
    {
        var (window, viewModel, controller, _) = Start();
        using (controller)
        {
            window.Show();
            try
            {
                Assert.Equal(160d, window.Width);
                Assert.Equal(44d, window.Height);

                viewModel.Toggle(); // Expanding uses the expanded layout
                Assert.Equal(380d, window.Width);
                Assert.Equal(320d, window.Height);
                Assert.Equal(380d, window.MinWidth);
                Assert.True(window.MaxWidth > 380d);

                viewModel.CompleteExpansion();
                Assert.Equal(380d, window.Width);

                viewModel.Collapse();
                Assert.Equal(380d, window.Width); // still large while collapsing
                viewModel.CompleteCollapse();
                Assert.Equal(160d, window.Width);
                Assert.Equal(44d, window.Height);
                Assert.Equal(160d, window.MaxWidth);
            }
            finally
            {
                window.Close();
            }
        }
    });

    [Fact]
    public void ResizingKeepsTheWindowCenteredAtTheTop() => RunOnSta(() =>
    {
        var (window, viewModel, controller, scale) = Start();
        using (controller)
        {
            window.Show();
            try
            {
                var display = Display(1920, 1080, (int)Math.Round(scale * 96));
                viewModel.Toggle();

                var expected = IslandPlacement.PlaceTopCenter(display, 380, 320, 8);
                Assert.Equal(expected.X, (int)Math.Round(window.Left * scale));
                Assert.Equal(expected.Y, (int)Math.Round(window.Top * scale));
                Assert.Equal(expected.Width, (int)Math.Round(window.Width * scale));
            }
            finally
            {
                window.Close();
            }
        }
    });

    [Fact]
    public void ContentIsMeasuredAndTheWindowGrowsToFitIt() => RunOnSta(() =>
    {
        // A small fake display: Windows caps a real window at the real screen (a hosted CI runner's is small), so the
        // work area the test asserts against must fit any screen.
        var dpi = (int)Math.Round(VisualTreeHelper.GetDpi(new IslandWindow()).DpiScaleX * 96);
        var (window, viewModel, controller, _) = Start([Display(800, 600, dpi)]);
        using (controller)
        {
            window.Show();
            try
            {
                viewModel.Toggle();
                window.ViewContent = new Border { Width = 500, Height = 400 };

                Assert.True(window.Width >= 500d);
                Assert.True(window.Height >= 400d);

                window.ViewContent = new Border { Width = 9000, Height = 9000 };

                Assert.Equal(window.MaxWidth, window.Width);
                Assert.Equal(window.MaxHeight, window.Height);

                window.ViewContent = null;
                Assert.Equal(380d, window.Width);
                Assert.Equal(320d, window.Height);
            }
            finally
            {
                window.Close();
            }
        }
    });

    [Fact]
    public void IdleStaysFixedEvenWithOversizedContent() => RunOnSta(() =>
    {
        var (window, _, controller, _) = Start();
        using (controller)
        {
            window.Show();
            try
            {
                window.ViewContent = new Border { Width = 900, Height = 900 };

                Assert.Equal(160d, window.Width);
                Assert.Equal(44d, window.Height);
            }
            finally
            {
                window.Close();
            }
        }
    });

    [Fact]
    public void SmallWorkAreaCapsTheExpandedWindow() => RunOnSta(() =>
    {
        var window = new IslandWindow();
        var viewModel = new IslandViewModel();
        window.DataContext = viewModel;
        _ = new WindowInteropHelper(window).EnsureHandle();
        var scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        var dpi = (int)Math.Round(scale * 96);
        var smallPixels = (int)Math.Round(300 * scale);
        var display = new DisplayInfo("\\\\.\\DISPLAY1", true, new PixelRect(0, 0, smallPixels, smallPixels), new PixelRect(0, 0, smallPixels, smallPixels), dpi, dpi);
        using var controller = new IslandPositionController(window, viewModel, new FakeEnvironment([display]), new FakeLog(), new IslandSettings());
        controller.Start();
        window.Show();
        try
        {
            viewModel.Toggle();

            Assert.True(window.Width <= 300d - (2 * IslandLayout.EdgeMargin) + 0.01);
            Assert.True(window.Height <= 300d - 8 - IslandLayout.EdgeMargin + 0.01);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void CornerRadiusIsPillWhileIdleAndPanelWhenExpanded() => RunOnSta(() =>
    {
        var (window, viewModel, controller, _) = Start();
        using (controller)
        {
            window.Show();
            try
            {
                var pill = Assert.IsType<Border>(window.Content);
                Assert.Equal(new CornerRadius(999), pill.CornerRadius);

                viewModel.Toggle();
                Assert.Equal(new CornerRadius(24), pill.CornerRadius);

                viewModel.CompleteExpansion();
                viewModel.Collapse();
                viewModel.CompleteCollapse();
                Assert.Equal(new CornerRadius(999), pill.CornerRadius);
            }
            finally
            {
                window.Close();
            }
        }
    });

    [Fact]
    public void ExpandedLayoutFollowsTheViewModelState()
    {
        var viewModel = new IslandViewModel();
        Assert.False(viewModel.IsExpandedLayout);

        foreach (var step in new Action[] { () => viewModel.Toggle(), () => viewModel.CompleteExpansion(), () => viewModel.SetBusy(true) })
        {
            step();
            Assert.True(viewModel.IsExpandedLayout);
        }

        viewModel.Collapse();
        Assert.True(viewModel.IsExpandedLayout);
        viewModel.CompleteCollapse();
        Assert.False(viewModel.IsExpandedLayout);
    }

    private sealed class FakeEnvironment(IReadOnlyList<DisplayInfo> displays) : IWindowEnvironment
    {
        public event Action? DisplaysChanged
        {
            add { }
            remove { }
        }

        public Result Start() => Result.Success();

        public Result<IReadOnlyList<DisplayInfo>> GetDisplays() => Result.Success(displays);

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
}
