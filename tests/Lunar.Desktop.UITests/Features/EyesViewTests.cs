using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Lunar.Desktop.Features.Eyes;
using Lunar.Desktop.UITests.Themes;
using Xunit;

namespace Lunar.Desktop.UITests.Features;

// Creates the real EyesView on an STA thread and renders it off screen. No window is shown.
public sealed class EyesViewTests
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
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new InvalidOperationException("WPF eyes check failed.", failure);
    }

    private static ResourceDictionary Load(string name)
    {
        using var stream = File.OpenRead(System.IO.Path.Combine(ThemeTokenTests.DesktopDirectory, "Themes", name));
        return (ResourceDictionary)XamlReader.Load(stream);
    }

    private static EyesView Create(string colors)
    {
        var view = new EyesView();
        // Same load order as App.xaml. The view is created first so a missing brush would throw on first render.
        view.Resources.MergedDictionaries.Add(Load(colors));
        view.Resources.MergedDictionaries.Add(Load("Typography.xaml"));
        view.Resources.MergedDictionaries.Add(Load("Spacing.xaml"));
        view.Resources.MergedDictionaries.Add(Load("Components.xaml"));
        return view;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child))
                yield return nested;
        }
    }

    private static RenderTargetBitmap Render(EyesView view, double dipWidth, double dipHeight, double dpi)
    {
        view.Measure(new Size(dipWidth, dipHeight));
        view.Arrange(new Rect(0, 0, dipWidth, dipHeight));
        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(dipWidth * dpi / 96), (int)Math.Ceiling(dipHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(view);
        return bitmap;
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    [Fact]
    public void ArtworkIsPureVectorWithNoBitmapOrExternalImage() => RunOnSta(() =>
    {
        var view = Create("Colors.Dark.xaml");
        view.ApplyTemplate();
        view.Measure(new Size(160, 42));
        view.Arrange(new Rect(0, 0, 160, 42));

        var all = Descendants(view).ToList();
        Assert.DoesNotContain(all, node => node is Image);
        Assert.All(all.OfType<Shape>(), shape => Assert.True(shape is Ellipse or Rectangle));
        Assert.Equal(6, all.OfType<Ellipse>().Count()); // per eye: eye white, pupil, highlight
        Assert.Equal(2, all.OfType<Rectangle>().Count()); // two eyelids
    });

    [Fact]
    public void EachEyeExposesAGazeTransformAndAnOpenEyelid() => RunOnSta(() =>
    {
        var view = Create("Colors.Dark.xaml");
        view.Measure(new Size(160, 42));
        view.Arrange(new Rect(0, 0, 160, 42));
        view.UpdateLayout();

        foreach (var name in new[] { "LeftPupilOffset", "RightPupilOffset" })
        {
            var transform = Assert.IsType<TranslateTransform>(view.FindName(name));
            Assert.Equal(0, transform.X); // neutral gaze
            Assert.Equal(0, transform.Y);
        }

        foreach (var name in new[] { "LeftEyelid", "RightEyelid" })
        {
            var lid = Assert.IsType<Rectangle>(view.FindName(name));
            Assert.Equal(0, Assert.IsType<ScaleTransform>(lid.RenderTransform).ScaleY); // open
        }
    });

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void RendersAtEveryTestedDpiWithBothEyesVisible(double dpi) => RunOnSta(() =>
    {
        var view = Create("Colors.Dark.xaml");
        var bitmap = Render(view, 158, 42, dpi);
        var pixels = Pixels(bitmap);

        // The pill background is not drawn by the view, so every lit pixel belongs to an eye.
        var lit = Enumerable.Range(0, pixels.Length / 4).Count(i => pixels[(i * 4) + 3] > 0);
        Assert.True(lit > bitmap.PixelWidth * bitmap.PixelHeight / 25, "Both eyes should cover a visible area.");

        // Anti-aliased vector edges: partially transparent pixels exist (a bitmap scaled by nearest neighbour would have none).
        Assert.Contains(Enumerable.Range(0, pixels.Length / 4), i => pixels[(i * 4) + 3] is > 0 and < 255);

        // The left and right halves are mirror images of each other in coverage.
        var half = bitmap.PixelWidth / 2;
        int Coverage(int from, int to) => Enumerable.Range(0, bitmap.PixelHeight)
            .Sum(y => Enumerable.Range(from, to - from).Count(x => pixels[((y * bitmap.PixelWidth) + x) * 4 + 3] > 0));
        var left = Coverage(0, half);
        var right = Coverage(half, bitmap.PixelWidth);
        Assert.InRange(Math.Abs(left - right), 0, Math.Max(2, left / 20));
    });

    [Theory]
    [InlineData("Colors.Dark.xaml")]
    [InlineData("Colors.Light.xaml")]
    [InlineData("Colors.HighContrast.xaml")]
    public void ColorsComeFromTheThemeAndRenderInEveryPalette(string colors) => RunOnSta(() =>
    {
        var view = Create(colors);
        var pixels = Pixels(Render(view, 158, 42, 96));

        Assert.Contains(Enumerable.Range(0, pixels.Length / 4), i => pixels[(i * 4) + 3] == 255);
    });

    private static ScaleTransform LidScale(EyesView view, string name) => (ScaleTransform)((Rectangle)view.FindName(name)).RenderTransform;

    [Fact]
    public void AttachedViewAnimatesTheLidsFromTheControllerAndDetachLeavesThemOpen() => RunOnSta(() =>
    {
        var timer = new EyeAnimationControllerTests.FakeTimer();
        var controller = new EyeAnimationController(timer, new EyeAnimationControllerTests.ScriptedRandom(0.0, 0.5, 0.9), new EyeAnimationControllerTests.FakeTime());
        var view = Create("Colors.Dark.xaml");
        view.Attach(controller);
        Assert.True(controller.IsHidden); // the view is not shown, so it reports hidden
        controller.IsHidden = false;
        controller.Start();

        timer.Fire(); // Closing
        foreach (var name in new[] { "LeftEyelid", "RightEyelid" })
        {
            var scale = LidScale(view, name);
            Assert.Equal(1.0, scale.ScaleY); // resting value is the target
            Assert.True(scale.HasAnimatedProperties); // and it is animated toward it, not snapped
        }

        timer.Fire();
        timer.Fire();
        timer.Fire(); // Open again
        Assert.Equal(0.0, LidScale(view, "LeftEyelid").ScaleY);

        timer.Fire(); // next blink: Closing
        view.Detach();
        Assert.Equal(0.0, LidScale(view, "LeftEyelid").ScaleY);
        Assert.False(LidScale(view, "LeftEyelid").HasAnimatedProperties);
        Assert.True(controller.IsHidden);
        Assert.Null(timer.Pending); // detached: no timer is left running

        controller.IsHidden = false; // a detached view no longer follows the controller
        Assert.Equal(0.0, LidScale(view, "RightEyelid").ScaleY);
    });

    [Fact]
    public void AsleepEyesHalfCloseTheLids() => RunOnSta(() =>
    {
        var timer = new EyeAnimationControllerTests.FakeTimer();
        var time = new EyeAnimationControllerTests.FakeTime();
        var controller = new EyeAnimationController(timer, new EyeAnimationControllerTests.ScriptedRandom(0.0), time);
        var view = Create("Colors.Dark.xaml");
        view.Attach(controller);
        controller.IsHidden = false;
        controller.Start();

        time.Advance(TimeSpan.FromMinutes(6));
        timer.Fire();

        Assert.Equal(EyePhase.Asleep, controller.State.Phase);
        Assert.Equal(EyesView.AsleepClosure, LidScale(view, "LeftEyelid").ScaleY);
        view.Detach();
    });

    [Theory]
    [InlineData(96)]
    [InlineData(192)]
    public void EyesStayWellInsideTheIdlePill(double dpi) => RunOnSta(() =>
    {
        // The idle pill is 160x44 DIP with a 1 DIP border: 158x42 of content area.
        var bitmap = Render(Create("Colors.Dark.xaml"), 158, 42, dpi);
        var pixels = Pixels(bitmap);
        int top = int.MaxValue, bottom = -1, left = int.MaxValue, right = -1;
        for (var y = 0; y < bitmap.PixelHeight; y++)
            for (var x = 0; x < bitmap.PixelWidth; x++)
                if (pixels[((y * bitmap.PixelWidth) + x) * 4 + 3] > 0)
                {
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                }

        var scale = dpi / 96;
        Assert.True(top >= 8 * scale, "At least 8 DIP of margin above the eyes.");
        Assert.True(bitmap.PixelHeight - 1 - bottom >= 8 * scale, "At least 8 DIP of margin below the eyes.");
        Assert.True(left >= 40 * scale, "The eyes keep clear of the rounded ends.");
        Assert.True(bitmap.PixelWidth - 1 - right >= 40 * scale, "The eyes keep clear of the rounded ends.");
        Assert.True((bottom - top + 1) <= 22 * scale, "The eyes are at most about 20 DIP high.");
    });
}
