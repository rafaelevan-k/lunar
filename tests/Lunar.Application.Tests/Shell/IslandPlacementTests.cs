using Lunar.Application.Shell;
using Lunar.Application.Shell.Models;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Application.Tests.Shell;

public sealed class IslandPlacementTests
{
    // Primary 1920x1080 at 100%, taskbar at the bottom (work area 1040 high).
    private static readonly DisplayInfo Primary100 = Display("\\\\.\\DISPLAY1", true, 0, 0, 1920, 1080, 0, 0, 1920, 1040, 96);

    private static DisplayInfo Display(string id, bool primary, int x, int y, int w, int h, int wx, int wy, int ww, int wh, int dpi) =>
        new(id, primary, new PixelRect(x, y, w, h), new PixelRect(wx, wy, ww, wh), dpi, dpi);

    [Fact]
    public void CentersInTheWorkAreaWithTheTopOffsetAtOneHundredPercent()
    {
        var rect = IslandPlacement.PlaceTopCenter(Primary100, 160, 44, 8);

        Assert.Equal(new PixelRect(880, 8, 160, 44), rect);
    }

    [Theory]
    [InlineData(96, 160, 44, 8)]
    [InlineData(120, 200, 55, 10)]
    [InlineData(144, 240, 66, 12)]
    [InlineData(192, 320, 88, 16)]
    public void ScalesDipToPixelsWithTheDisplayScale(int dpi, int width, int height, int offset)
    {
        var display = Display("D", true, 0, 0, 3840, 2160, 0, 0, 3840, 2100, dpi);

        var rect = IslandPlacement.PlaceTopCenter(display, 160, 44, 8);

        Assert.Equal(width, rect.Width);
        Assert.Equal(height, rect.Height);
        Assert.Equal(offset, rect.Y);
        Assert.Equal((3840 - width) / 2, rect.X);
    }

    [Fact]
    public void SecondaryMonitorLeftOfThePrimaryHasNegativeCoordinates()
    {
        var left = Display("\\\\.\\DISPLAY2", false, -1920, 0, 1920, 1080, -1920, 0, 1920, 1080, 96);

        var rect = IslandPlacement.PlaceTopCenter(left, 160, 44, 8);

        Assert.Equal(new PixelRect(-1040, 8, 160, 44), rect);
        Assert.True(left.WorkArea.Contains(rect));
    }

    [Fact]
    public void SecondaryMonitorAbovethePrimaryHasNegativeY()
    {
        var above = Display("D2", false, 0, -1080, 1920, 1080, 0, -1080, 1920, 1080, 96);

        var rect = IslandPlacement.PlaceTopCenter(above, 160, 44, 8);

        Assert.Equal(-1072, rect.Y);
        Assert.True(above.WorkArea.Contains(rect));
    }

    [Theory]
    [InlineData("top", 0, 48, 1920, 1032, 48 + 8, 880)]
    [InlineData("left", 60, 0, 1860, 1080, 8, 60 + (1860 - 160) / 2)]
    [InlineData("right", 0, 0, 1860, 1080, 8, (1860 - 160) / 2)]
    [InlineData("bottom", 0, 0, 1920, 1040, 8, 880)]
    public void TaskbarOnAnyEdgeKeepsTheIslandInsideTheWorkArea(string edge, int wx, int wy, int ww, int wh, int expectedY, int expectedX)
    {
        var display = Display("D", true, 0, 0, 1920, 1080, wx, wy, ww, wh, 96);

        var rect = IslandPlacement.PlaceTopCenter(display, 160, 44, 8);

        Assert.True(display.WorkArea.Contains(rect), edge);
        Assert.Equal(expectedY, rect.Y);
        Assert.Equal(expectedX, rect.X);
    }

    [Fact]
    public void IslandLargerThanTheWorkAreaIsShrunkToFit()
    {
        var tiny = Display("D", true, 0, 0, 100, 40, 0, 0, 100, 30, 96);

        var rect = IslandPlacement.PlaceTopCenter(tiny, 380, 320, 8);

        Assert.Equal(new PixelRect(0, 0, 100, 30), rect);
    }

    [Fact]
    public void OffsetBeyondTheBottomIsClampedUp()
    {
        var rect = IslandPlacement.PlaceTopCenter(Primary100, 160, 44, 5000);

        Assert.Equal(1040 - 44, rect.Y);
    }

    [Fact]
    public void MixedScalesPlaceEachDisplayIndependently()
    {
        var at100 = Display("A", true, 0, 0, 1920, 1080, 0, 0, 1920, 1040, 96);
        var at200 = Display("B", false, 1920, 0, 3840, 2160, 1920, 0, 3840, 2120, 192);

        var first = IslandPlacement.PlaceTopCenter(at100, 160, 44, 8);
        var second = IslandPlacement.PlaceTopCenter(at200, 160, 44, 8);

        Assert.Equal(160, first.Width);
        Assert.Equal(320, second.Width);
        Assert.Equal(1920 + (3840 - 320) / 2, second.X);
        Assert.Equal(16, second.Y);
    }

    [Theory]
    [InlineData(0, 44, 8)]
    [InlineData(-1, 44, 8)]
    [InlineData(160, 0, 8)]
    [InlineData(160, 44, -1)]
    [InlineData(double.NaN, 44, 8)]
    [InlineData(160, double.PositiveInfinity, 8)]
    public void InvalidSizesAreProgrammerErrors(double width, double height, double offset)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => IslandPlacement.PlaceTopCenter(Primary100, width, height, offset));
    }

    [Fact]
    public void SelectsThePreferredDisplayIgnoringCase()
    {
        var other = Display("\\\\.\\DISPLAY2", false, 1920, 0, 1920, 1080, 1920, 0, 1920, 1080, 96);

        var result = IslandPlacement.SelectDisplay([Primary100, other], "\\\\.\\display2");

        Assert.Same(other, result.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("\\\\.\\DISPLAY9")]
    public void FallsBackToThePrimaryWhenThePreferredDisplayIsMissingOrBlank(string? preferred)
    {
        var other = Display("\\\\.\\DISPLAY2", false, 1920, 0, 1920, 1080, 1920, 0, 1920, 1080, 96);

        var result = IslandPlacement.SelectDisplay([other, Primary100], preferred);

        Assert.Same(Primary100, result.Value);
    }

    [Fact]
    public void FallsBackToTheFirstDisplayWhenNoneIsPrimary()
    {
        var a = Display("A", false, 0, 0, 800, 600, 0, 0, 800, 600, 96);
        var b = Display("B", false, 800, 0, 800, 600, 800, 0, 800, 600, 96);

        Assert.Same(a, IslandPlacement.SelectDisplay([a, b], "missing").Value);
    }

    [Fact]
    public void NoDisplaysIsADeviceUnavailableFailure()
    {
        var result = IslandPlacement.SelectDisplay([], "anything");

        Assert.Equal(ErrorCode.DeviceUnavailable, result.Error!.Code);
    }

    [Fact]
    public void FallbackDisplayAlwaysYieldsAVisiblePlacement()
    {
        var rect = IslandPlacement.PlaceTopCenter(DisplayInfo.Fallback, 160, 44, 8);

        Assert.True(DisplayInfo.Fallback.WorkArea.Contains(rect));
        Assert.Equal(new PixelRect(560, 8, 160, 44), rect);
    }
}

public sealed class DisplayModelTests
{
    [Fact]
    public void PixelRectRejectsNegativeSizeAndComputesEdges()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PixelRect(0, 0, -1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PixelRect(0, 0, 10, -1));

        var rect = new PixelRect(-10, 5, 30, 20);
        Assert.Equal(20, rect.Right);
        Assert.Equal(25, rect.Bottom);
        Assert.True(rect.Contains(new PixelRect(-10, 5, 30, 20)));
        Assert.False(rect.Contains(new PixelRect(-11, 5, 30, 20)));
        Assert.False(rect.Contains(new PixelRect(0, 5, 30, 21)));
    }

    [Fact]
    public void DisplayInfoValidatesItsFields()
    {
        var bounds = new PixelRect(0, 0, 100, 100);

        Assert.ThrowsAny<ArgumentException>(() => new DisplayInfo(" ", true, bounds, bounds, 96, 96));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DisplayInfo("D", true, bounds, bounds, 0, 96));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DisplayInfo("D", true, bounds, bounds, 96, -1));
        Assert.Throws<ArgumentException>(() => new DisplayInfo("D", true, bounds, new PixelRect(50, 50, 100, 100), 96, 96));
    }

    [Fact]
    public void ScaleIsDpiOverNinetySix()
    {
        var bounds = new PixelRect(0, 0, 100, 100);

        var display = new DisplayInfo("D", true, bounds, bounds, 144, 192);

        Assert.Equal(1.5, display.ScaleX);
        Assert.Equal(2.0, display.ScaleY);
    }
}
