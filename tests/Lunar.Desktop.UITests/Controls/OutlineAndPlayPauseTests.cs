using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Lunar.Desktop.Controls;
using Lunar.Desktop.UITests.Themes;
using Xunit;

namespace Lunar.Desktop.UITests.Controls;

// Two looks the user asked to be tidied (2026-11-05): every white (and other) outline on buttons, chips and switches has one and the same width that
// lands on whole device pixels, and the play and pause buttons draw solid shapes instead of thin outlined font glyphs. How they look on a real 125%
// display was checked with a throwaway harness (the island's own DPI); these tests keep the rules from slipping back.
public sealed class OutlineAndPlayPauseTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private const string Token = "{DynamicResource Lunar.Thickness.Outline}";

    private static XDocument Source(string relative) => XDocument.Load(System.IO.Path.Combine(ThemeTokenTests.DesktopDirectory, relative));

    private static ResourceDictionary Load(params string[] relative)
    {
        var path = System.IO.Path.Combine([ThemeTokenTests.DesktopDirectory, .. relative]);
        var text = File.ReadAllText(path).Replace("\"clr-namespace:Lunar.Desktop.Controls\"", "\"clr-namespace:Lunar.Desktop.Controls;assembly=Lunar.Desktop\"")
            .Replace("\"clr-namespace:Lunar.Desktop.Shell\"", "\"clr-namespace:Lunar.Desktop.Shell;assembly=Lunar.Desktop\"");
        return (ResourceDictionary)XamlReader.Parse(text);
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
            throw new InvalidOperationException("WPF control check failed.", failure);
    }

    private static Window Host(UIElement content)
    {
        var window = new Window
        {
            Content = new Border { Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0C, 0x10)), Padding = new Thickness(12), Child = content },
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStyle = WindowStyle.None,
            ShowActivated = false,
            ShowInTaskbar = false,
            Left = -20000,
            Top = -20000,
            UseLayoutRounding = true
        };
        foreach (var name in new[] { "Colors.Dark.xaml", "Typography.xaml", "Spacing.xaml", "Components.xaml" })
            window.Resources.MergedDictionaries.Add(Load("Themes", name));
        window.Resources.MergedDictionaries.Add(Load("Controls", "ControlStyles.xaml"));
        window.Show();
        window.UpdateLayout();
        return window;
    }

    // ---- one outline width ------------------------------------------------------------------------------------------

    [Fact]
    public void TheOutlineWidthIsOneTokenOfOnePointSixDips()
    {
        var token = Source(System.IO.Path.Combine("Themes", "Components.xaml")).Root!.Elements(Presentation + "Thickness")
            .Single(e => (string?)e.Attribute(Xaml + "Key") == "Lunar.Thickness.Outline");

        Assert.Equal("1.6", token.Value.Trim());
    }

    [Theory]
    [InlineData("Lunar.Style.SwitchButton")]
    [InlineData("Lunar.Style.Toggle")]
    [InlineData("Lunar.Style.ActionButton")]
    [InlineData("c:IconButton")] // the default style of the round icon buttons
    [InlineData("c:StatusChip")]
    public void EveryOutlineOfAButtonChipOrSwitchUsesTheOneToken(string key)
    {
        var styles = Source(System.IO.Path.Combine("Controls", "ControlStyles.xaml")).Root!.Elements(Presentation + "Style")
            .Where(s => (string?)s.Attribute(Xaml + "Key") == key || (string?)s.Attribute("TargetType") == key && s.Attribute(Xaml + "Key") is null)
            .ToList();
        Assert.NotEmpty(styles);

        var outlined = styles.SelectMany(s => s.Descendants())
            .Where(e => e.Name.LocalName is "Border" or "Rectangle" && e.Attribute("BorderBrush") is not null)
            .ToList();
        Assert.NotEmpty(outlined);
        Assert.All(outlined, e => Assert.Equal(Token, (string?)e.Attribute("BorderThickness")));
    }

    [Fact]
    public void TheSwiperArrowsTheConverterFormatButtonsAndTheUsageButtonsUseTheSameToken()
    {
        foreach (var file in new[] { @"Navigation\SwiperView.xaml", @"Features\Converter\ConverterPage.xaml", @"Features\Usage\UsagePage.xaml" })
        {
            var text = File.ReadAllText(System.IO.Path.Combine(ThemeTokenTests.DesktopDirectory, file));
            Assert.Contains(Token, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheChipsOutlineIsTheTokenWidthForEveryKind() => RunOnSta(() =>
    {
        var panel = new StackPanel();
        foreach (StatusKind kind in Enum.GetValues<StatusKind>())
            panel.Children.Add(new StatusChip { Text = kind.ToString(), Kind = kind, Margin = new Thickness(0, 0, 0, 6) });
        var window = Host(panel);
        try
        {
            var frames = panel.Children.OfType<StatusChip>().Select(c => (Border)c.Template.FindName("Frame", c)).ToList();

            Assert.Equal(Enum.GetValues<StatusKind>().Length, frames.Count);
            Assert.All(frames, f => Assert.Equal(1.6, f.BorderThickness.Left, 3));
            Assert.All(frames, f => Assert.Equal(f.BorderThickness.Top, f.BorderThickness.Right)); // the same on all four sides
            Assert.All(frames, f => Assert.InRange(f.ActualHeight, 21.9, 22.5)); // the thicker line did not make the chip taller (22 DIP, rounded to whole pixels)
        }
        finally
        {
            window.Close();
        }
    });

    // ---- solid play and pause ---------------------------------------------------------------------------------------

    private static (IconButton Button, Window Window) PrimaryButton(string glyph)
    {
        var button = new IconButton { Glyph = glyph, Label = "x" };
        var holder = new StackPanel { Orientation = Orientation.Horizontal, Children = { button } };
        var window = Host(holder);
        // Applied after the resources exist: the style comes from the window's dictionaries.
        button.SetResourceReference(FrameworkElement.StyleProperty, "Lunar.Style.IconButtonPrimary");
        window.UpdateLayout();
        return (button, window);
    }

    private static T Part<T>(IconButton button, string name)
        where T : class =>
        button.Template.FindName(name, button) as T ?? throw new InvalidOperationException(name + " is missing.");

    [Fact]
    public void PauseShowsTwoSolidBarsAndPlayAOneSolidTriangleAndOtherGlyphsKeepTheFontIcon() => RunOnSta(() =>
    {
        var (pause, pauseWindow) = PrimaryButton("");
        var (play, playWindow) = PrimaryButton("");
        var (mute, muteWindow) = PrimaryButton("");
        try
        {
            Assert.Equal(Visibility.Visible, Part<StackPanel>(pause, "PauseShape").Visibility);
            Assert.Equal(Visibility.Collapsed, Part<TextBlock>(pause, "Icon").Visibility);
            Assert.Equal(Visibility.Collapsed, Part<System.Windows.Shapes.Path>(pause, "PlayShape").Visibility);

            Assert.Equal(Visibility.Visible, Part<System.Windows.Shapes.Path>(play, "PlayShape").Visibility);
            Assert.Equal(Visibility.Collapsed, Part<TextBlock>(play, "Icon").Visibility);
            Assert.Equal(Visibility.Collapsed, Part<StackPanel>(play, "PauseShape").Visibility);

            // Any other glyph (the mute button uses the same style) still shows its font icon.
            Assert.Equal(Visibility.Visible, Part<TextBlock>(mute, "Icon").Visibility);
            Assert.Equal(Visibility.Collapsed, Part<StackPanel>(mute, "PauseShape").Visibility);
            Assert.Equal(Visibility.Collapsed, Part<System.Windows.Shapes.Path>(mute, "PlayShape").Visibility);
        }
        finally
        {
            pauseWindow.Close();
            playWindow.Close();
            muteWindow.Close();
        }
    });

    [Fact]
    public void TheBarsAreBoldAndEvenAndTheShapesStayInsideTheCircle() => RunOnSta(() =>
    {
        var (pause, window) = PrimaryButton("");
        try
        {
            var bars = Part<StackPanel>(pause, "PauseShape").Children.OfType<Border>().ToList();

            Assert.Equal(2, bars.Count);
            Assert.All(bars, b => Assert.True(b.ActualWidth >= 4.5 && b.ActualHeight >= 17, $"A bar is {b.ActualWidth}x{b.ActualHeight}."));
            Assert.Equal(bars[0].ActualWidth, bars[1].ActualWidth, 2);
            Assert.Equal(bars[0].ActualHeight, bars[1].ActualHeight, 2);
            var total = Part<StackPanel>(pause, "PauseShape").ActualWidth;
            Assert.True(total < pause.ActualWidth * 0.5, "The pause mark sits well inside the 46 DIP circle.");
        }
        finally
        {
            window.Close();
        }
    });

    private static Color PixelAt(FrameworkElement element, Visual root, Window window, double x, double y)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var bounds = new Rect(0, 0, ((FrameworkElement)root).ActualWidth, ((FrameworkElement)root).ActualHeight);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width * dpi.DpiScaleX), (int)Math.Ceiling(bounds.Height * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render((Visual)root);
        var origin = element.TransformToAncestor(root).Transform(new Point(x, y));
        var px = new byte[4];
        bitmap.CopyPixels(new Int32Rect((int)Math.Round(origin.X * dpi.DpiScaleX), (int)Math.Round(origin.Y * dpi.DpiScaleY), 1, 1), px, 4, 0);
        return Color.FromRgb(px[2], px[1], px[0]);
    }

    [Fact]
    public void TheCentreOfEachBarAndOfTheTriangleIsSolidDarkNotHollow() => RunOnSta(() =>
    {
        var (pause, pauseWindow) = PrimaryButton("");
        var (play, playWindow) = PrimaryButton("");
        try
        {
            var bar = Part<StackPanel>(pause, "PauseShape").Children.OfType<Border>().First();
            var barColor = PixelAt(bar, (Visual)pauseWindow.Content, pauseWindow, bar.ActualWidth / 2, bar.ActualHeight / 2);
            var triangle = Part<System.Windows.Shapes.Path>(play, "PlayShape");
            var triangleColor = PixelAt(triangle, (Visual)playWindow.Content, playWindow, triangle.ActualWidth * 0.3, triangle.ActualHeight / 2);

            // The font glyphs were hollow outlines: their middle was the white circle. Solid shapes are the dark "on accent" colour.
            Assert.True(barColor.R < 60 && barColor.G < 60 && barColor.B < 60, $"The middle of a bar is {barColor}.");
            Assert.True(triangleColor.R < 60 && triangleColor.G < 60 && triangleColor.B < 60, $"The middle of the triangle is {triangleColor}.");
        }
        finally
        {
            pauseWindow.Close();
            playWindow.Close();
        }
    });

    [Fact]
    public void ADisabledPlayButtonDimsItsShapeLikeItsIcon() => RunOnSta(() =>
    {
        var (play, window) = PrimaryButton("");
        try
        {
            play.IsEnabled = false;
            window.UpdateLayout();

            var shape = Part<System.Windows.Shapes.Path>(play, "PlayShape");
            var fill = ((SolidColorBrush)shape.Fill).Color;
            var disabled = ((SolidColorBrush)window.FindResource("Lunar.Brush.TextDisabled")).Color;

            Assert.Equal(disabled, fill);
        }
        finally
        {
            window.Close();
        }
    });
}
