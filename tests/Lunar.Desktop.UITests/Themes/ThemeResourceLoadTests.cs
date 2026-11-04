using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Xunit;

namespace Lunar.Desktop.UITests.Themes;

// Loads the real XAML with the WPF parser on an STA thread. No window is shown and no UI is interacted with.
public sealed class ThemeResourceLoadTests
{
    private static ResourceDictionary LoadDictionary(string name)
    {
        using var stream = File.OpenRead(Path.Combine(ThemeTokenTests.DesktopDirectory, "Themes", name));
        return (ResourceDictionary)XamlReader.Load(stream);
    }

    private static TextBlock Host(string colors)
    {
        var host = new TextBlock();
        host.Resources.MergedDictionaries.Add(LoadDictionary(colors));
        host.Resources.MergedDictionaries.Add(LoadDictionary("Typography.xaml"));
        host.Resources.MergedDictionaries.Add(LoadDictionary("Spacing.xaml"));
        host.Resources.MergedDictionaries.Add(LoadDictionary("Components.xaml"));
        return host;
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
            throw new InvalidOperationException("WPF resource check failed.", failure);
    }

    [Theory]
    [InlineData("Colors.Dark.xaml")]
    [InlineData("Colors.Light.xaml")]
    [InlineData("Colors.HighContrast.xaml")]
    public void EveryTokenResolvesFromEachTheme(string colors) => RunOnSta(() =>
    {
        var host = Host(colors);
        foreach (var key in new[] { "Background", "Surface", "TextPrimary", "TextSecondary", "TextDisabled", "Accent", "OnAccent", "Focus", "Border", "Error", "Success" })
        {
            Assert.IsType<Color>(host.FindResource("Lunar.Color." + key));
            Assert.IsType<SolidColorBrush>(host.FindResource("Lunar.Brush." + key));
        }

        foreach (var key in new[] { "Lunar.Space.M", "Lunar.Padding.M", "Lunar.Radius.Pill", "Lunar.Radius.Panel", "Lunar.Font.Family", "Lunar.Font.Size.Body", "Lunar.Style.FocusVisual", "Lunar.Text.Body", "Lunar.Text.Error" })
            Assert.NotNull(host.FindResource(key));
    });

    [Fact]
    public void BrushesReflectTheLoadedPaletteAndTextStylesUseThem() => RunOnSta(() =>
    {
        var dark = Host("Colors.Dark.xaml");
        var light = Host("Colors.Light.xaml");

        Assert.Equal(Color.FromRgb(0x0B, 0x0C, 0x10), ((SolidColorBrush)dark.FindResource("Lunar.Brush.Background")).Color);
        Assert.Equal(Color.FromRgb(0xF6, 0xF7, 0xFB), ((SolidColorBrush)light.FindResource("Lunar.Brush.Background")).Color);

        dark.Style = (Style)dark.FindResource("Lunar.Text.Secondary");
        dark.ApplyTemplate();
        Assert.Equal(14d, dark.FontSize);
        Assert.Equal(Color.FromRgb(0xA8, 0xAD, 0xBE), ((SolidColorBrush)dark.Foreground).Color);
    });

    [Fact]
    public void HighContrastTokensFollowSystemColors() => RunOnSta(() =>
    {
        var host = Host("Colors.HighContrast.xaml");

        Assert.Equal(SystemColors.WindowColor, (Color)host.FindResource("Lunar.Color.Background"));
        Assert.Equal(SystemColors.WindowTextColor, (Color)host.FindResource("Lunar.Color.TextPrimary"));
        Assert.Equal(SystemColors.HighlightColor, (Color)host.FindResource("Lunar.Color.Accent"));
    });

    [Fact]
    public void FocusVisualStyleIsAControlTemplateWithAVisibleStroke() => RunOnSta(() =>
    {
        var style = (Style)Host("Colors.Dark.xaml").FindResource("Lunar.Style.FocusVisual");

        var template = style.Setters.OfType<Setter>().Single(setter => setter.Property == Control.TemplateProperty);
        Assert.IsType<ControlTemplate>(template.Value);
    });
}
