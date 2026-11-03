using Lunar.Desktop.Features.Eyes;
using Xunit;

namespace Lunar.Desktop.UITests.Features;

// Pure rule: no WPF.
public sealed class EyeGazeTests
{
    private static double Length((double X, double Y) v) => Math.Sqrt((v.X * v.X) + (v.Y * v.Y));

    [Fact]
    public void PointerAtTheEyeCentreMeansNeutralGaze() => Assert.Equal((0d, 0d), EyeGaze.Offset(0, 0));

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(0, double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity, double.NaN)]
    public void NonFiniteInputMeansNeutralGaze(double dx, double dy) => Assert.Equal((0d, 0d), EyeGaze.Offset(dx, dy));

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, -1)]
    [InlineData(70, 70)]
    [InlineData(500, -300)]
    [InlineData(1e9, 1e9)]
    [InlineData(-3, 4)]
    public void DisplacementNeverExceedsTheCap(double dx, double dy) =>
        Assert.True(Length(EyeGaze.Offset(dx, dy)) <= EyeGaze.MaxOffset + 1e-9);

    [Fact]
    public void FarPointerReachesExactlyTheCapInTheRightDirection()
    {
        var right = EyeGaze.Offset(400, 0);
        Assert.Equal(EyeGaze.MaxOffset, right.X, 6);
        Assert.Equal(0, right.Y, 6);

        var up = EyeGaze.Offset(0, -400);
        Assert.Equal(0, up.X, 6);
        Assert.Equal(-EyeGaze.MaxOffset, up.Y, 6);

        var diagonal = EyeGaze.Offset(300, 300);
        Assert.Equal(EyeGaze.MaxOffset, Length(diagonal), 6);
        Assert.Equal(diagonal.X, diagonal.Y, 6);
    }

    [Fact]
    public void DisplacementGrowsSmoothlyAndMonotonicallyWithDistance()
    {
        var previous = 0d;
        for (var distance = 0d; distance <= 120; distance += 0.5)
        {
            var length = Length(EyeGaze.Offset(distance, 0));
            Assert.True(length >= previous - 1e-9, $"Not monotonic at {distance}.");
            Assert.True(length - previous <= 0.15, $"Jump of {length - previous} at {distance}."); // no jitter-causing steps
            previous = length;
        }
    }

    [Fact]
    public void ANearPointerBarelyMovesThePupil() =>
        Assert.True(Length(EyeGaze.Offset(2, 0)) < 0.05); // sub-pixel pointer noise near the centre does not jitter

    [Fact]
    public void OppositePointersGiveOppositeOffsets()
    {
        var a = EyeGaze.Offset(30, -20);
        var b = EyeGaze.Offset(-30, 20);
        Assert.Equal(-a.X, b.X, 9);
        Assert.Equal(-a.Y, b.Y, 9);
    }
}
