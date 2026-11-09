using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Lunar.Architecture.Tests;
using Xunit;

namespace Lunar.Desktop.UITests.Themes;

// Static checks over the theme XAML source (no WPF runtime needed): token parity, WCAG contrast, no stray colors.
public sealed class ThemeTokenTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    internal static string DesktopDirectory => Path.Combine(LayerBoundaryTests.RepositoryRoot, "src", "Lunar.Desktop");

    private static XDocument Load(string relative) => XDocument.Load(Path.Combine(DesktopDirectory, relative));

    private static string Key(XElement element) => (string)element.Attribute(Xaml + "Key")!;

    private static Dictionary<string, string> ColorTokens(string theme) =>
        Load(Path.Combine("Themes", $"Colors.{theme}.xaml")).Root!.Elements()
            .ToDictionary(Key, element => element.Value.Trim());

    public static TheoryData<string> Palettes => new() { "Dark", "Light" };

    [Fact]
    public void AllThemesDefineTheSameColorTokens()
    {
        var dark = ColorTokens("Dark").Keys.Order().ToArray();

        Assert.NotEmpty(dark);
        Assert.Equal(dark, ColorTokens("Light").Keys.Order());
        Assert.Equal(dark, ColorTokens("HighContrast").Keys.Order());
    }

    [Fact]
    public void EveryColorTokenHasExactlyOneBrushAndEveryBrushPointsAtAnExistingColor()
    {
        var colors = ColorTokens("Dark").Keys.ToHashSet();
        var brushes = Load(Path.Combine("Themes", "Components.xaml")).Root!
            .Elements(Presentation + "SolidColorBrush").ToList();

        var referenced = brushes.Select(brush =>
        {
            var match = Regex.Match((string)brush.Attribute("Color")!, @"^\{DynamicResource (?<key>[^}]+)\}$");
            Assert.True(match.Success, $"Brush {Key(brush)} must reference a color token, not a literal.");
            return match.Groups["key"].Value;
        }).ToList();

        Assert.Equal(referenced.Count, referenced.Distinct().Count());
        Assert.Equal(colors.Order(), referenced.Order());
        Assert.All(brushes, brush => Assert.Equal(
            "Lunar.Brush." + ((string)brush.Attribute("Color")!).Split('.')[^1].TrimEnd('}'), Key(brush)));
    }

    [Fact]
    public void DarkPaletteMatchesTheProductSpecification()
    {
        var dark = ColorTokens("Dark");

        Assert.Equal("#0B0C10", dark["Lunar.Color.Background"]);
        Assert.Equal("#181A22", dark["Lunar.Color.Surface"]);
        Assert.Equal("#F6F7FB", dark["Lunar.Color.TextPrimary"]);
        Assert.Equal("#A8ADBE", dark["Lunar.Color.TextSecondary"]);
        Assert.Equal("#F6F7FB", dark["Lunar.Color.Accent"]);
        Assert.Equal("#FF737E", dark["Lunar.Color.Error"]);
        Assert.Equal("#86D7B0", dark["Lunar.Color.Success"]);
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void TextAndStatusColorsMeetNormalTextContrastOnBothSurfaces(string theme)
    {
        var colors = ColorTokens(theme);

        foreach (var background in new[] { "Background", "Surface" })
        {
            foreach (var foreground in new[] { "TextPrimary", "TextSecondary", "Accent", "Error", "Recording", "Success" })
            {
                var ratio = Contrast(colors["Lunar.Color." + foreground], colors["Lunar.Color." + background]);
                Assert.True(ratio >= 4.5, $"{theme}: {foreground} on {background} is {ratio:0.00}:1, needs 4.5:1.");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void AccentFillsAndFocusIndicatorMeetTheirContrast(string theme)
    {
        var colors = ColorTokens(theme);

        Assert.True(Contrast(colors["Lunar.Color.OnAccent"], colors["Lunar.Color.Accent"]) >= 4.5,
            $"{theme}: text on accent fill needs 4.5:1.");
        foreach (var background in new[] { "Background", "Surface" })
            Assert.True(Contrast(colors["Lunar.Color.Focus"], colors["Lunar.Color." + background]) >= 3.0,
                $"{theme}: focus indicator on {background} needs 3:1.");
    }

    [Fact]
    public void HighContrastTokensAreSystemColorsOnly()
    {
        var root = Load(Path.Combine("Themes", "Colors.HighContrast.xaml")).Root!;

        Assert.All(root.Elements(), element =>
        {
            Assert.Equal("Static", element.Name.LocalName);
            Assert.StartsWith("SystemColors.", (string)element.Attribute("Member")!);
        });
    }

    [Fact]
    public void SpacingScaleMatchesTheProductSpecification()
    {
        var spacing = Load(Path.Combine("Themes", "Spacing.xaml")).Root!.Elements()
            .Where(element => element.Name.LocalName == "Double")
            .ToDictionary(Key, element => double.Parse(element.Value, CultureInfo.InvariantCulture));

        Assert.Equal(new[] { 4d, 8d, 12d, 16d, 24d },
            new[] { "XS", "S", "M", "L", "XL" }.Select(name => spacing["Lunar.Space." + name]));
    }

    [Fact]
    public void ViewXamlUsesTokensInsteadOfLiteralColors()
    {
        // Theme palettes are the only place literal colors may live. Retained discovery files (Spike/) are excluded.
        var offenders = Directory.EnumerateFiles(DesktopDirectory, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}Spike{Path.DirectorySeparatorChar}")
                && !Path.GetFileName(path).StartsWith("Colors.", StringComparison.Ordinal))
            .Where(path => Regex.IsMatch(File.ReadAllText(path), @"#[0-9A-Fa-f]{6,8}\b"))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }

    internal static double Contrast(string foreground, string background)
    {
        var a = Luminance(foreground);
        var b = Luminance(background);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(string hex)
    {
        Assert.Matches("^#[0-9A-Fa-f]{6}$", hex);
        double Channel(int offset)
        {
            var value = int.Parse(hex.AsSpan(1 + offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(0) + 0.7152 * Channel(2) + 0.0722 * Channel(4);
    }
}
