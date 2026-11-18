using Lunar.Application.Settings;
using Lunar.Application.Settings.Models;
using Lunar.Application.Shell;
using Lunar.Application.Shell.Models;
using Xunit;

namespace Lunar.Application.Tests.Shell;

// BE-46 (ADR-0027): the Notch is attached to the top of the work area; the Pill keeps the user's offset.
public sealed class IslandStylePlacementTests
{
    private static DisplayInfo Display(int x, int y, int w, int h, int wx, int wy, int ww, int wh, int dpi) =>
        new("D", true, new PixelRect(x, y, w, h), new PixelRect(wx, wy, ww, wh), dpi, dpi);

    [Theory]
    [InlineData(IslandStyle.Pill, 8, 8)]
    [InlineData(IslandStyle.Pill, 0, 0)]
    [InlineData(IslandStyle.Pill, -5, 0)]
    [InlineData(IslandStyle.Notch, 8, 0)]
    [InlineData(IslandStyle.Notch, 300, 0)]
    public void TheNotchIgnoresTheOffsetAndThePillKeepsIt(IslandStyle style, int configured, int expected) =>
        Assert.Equal(expected, IslandPlacement.TopOffsetFor(style, configured));

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    public void TheNotchTouchesTheTopOfTheWorkAreaAtEveryScale(int dpi)
    {
        var display = Display(0, 0, 3840, 2160, 0, 0, 3840, 2100, dpi);
        var rect = IslandPlacement.PlaceTopCenter(display, 160, 44, IslandPlacement.TopOffsetFor(IslandStyle.Notch, 24));
        Assert.Equal(0, rect.Y);
        Assert.Equal((3840 - rect.Width) / 2, rect.X);
    }

    [Fact]
    public void WithTheTaskbarAtTheTopTheNotchSitsRightUnderIt()
    {
        var display = Display(0, 0, 1920, 1080, 0, 48, 1920, 1032, 96);
        var rect = IslandPlacement.PlaceTopCenter(display, 160, 44, IslandPlacement.TopOffsetFor(IslandStyle.Notch, 8));
        Assert.Equal(48, rect.Y);
    }

    [Theory]
    [InlineData(0, 0, 1920, 1040)]   // taskbar at the bottom
    [InlineData(60, 0, 1860, 1080)]  // taskbar at the left
    [InlineData(0, 0, 1860, 1080)]   // taskbar at the right
    public void OtherTaskbarEdgesKeepTheNotchAtTheTopCentreOfTheWorkArea(int wx, int wy, int ww, int wh)
    {
        var display = Display(0, 0, 1920, 1080, wx, wy, ww, wh, 96);
        var rect = IslandPlacement.PlaceTopCenter(display, 160, 44, IslandPlacement.TopOffsetFor(IslandStyle.Notch, 8));
        Assert.Equal(wy, rect.Y);
        Assert.Equal(wx + ((ww - 160) / 2), rect.X);
    }

    [Fact]
    public void ASecondaryDisplayWithNegativeCoordinatesWorksToo()
    {
        var display = new DisplayInfo("D2", false, new PixelRect(-2560, -300, 2560, 1440), new PixelRect(-2560, -300, 2560, 1400), 144, 144);
        var rect = IslandPlacement.PlaceTopCenter(display, 160, 44, IslandPlacement.TopOffsetFor(IslandStyle.Notch, 8));
        Assert.Equal(-300, rect.Y);
        Assert.Equal(-2560 + ((2560 - 240) / 2), rect.X);
    }

    [Fact]
    public void AnUnknownStyleIsRepairedToThePill()
    {
        var broken = LunarSettings.Defaults with { Appearance = LunarSettings.Defaults.Appearance with { IslandStyle = (IslandStyle)42 } };
        var normal = SettingsRules.Normalize(broken, out var changed);
        Assert.True(changed);
        Assert.Equal(IslandStyle.Pill, normal.Appearance.IslandStyle);
        Assert.Equal(IslandStyle.Pill, LunarSettings.Defaults.Appearance.IslandStyle);
    }
}
