using Lunar.Application.Settings;
using Lunar.Application.Settings.Models;
using Xunit;

namespace Lunar.Application.Tests.Settings;

public sealed class MotionPolicyTests
{
    [Theory]
    [InlineData(false, true, true, false, false)] // everything on: all motion runs
    [InlineData(true, true, true, true, true)] // the user's reduce motion stops everything
    [InlineData(false, false, true, true, true)] // Windows has animations off: everything stops
    [InlineData(false, true, false, false, true)] // idle eyes off stops only the eyes
    [InlineData(true, true, false, true, true)]
    [InlineData(false, false, false, true, true)]
    public void MotionStopsWhereTheRulesSay(bool reduce, bool windowsAnimations, bool eyes, bool interfaceOff, bool eyesOff)
    {
        var appearance = new AppearanceSettings { ReduceMotion = reduce, EyeAnimationEnabled = eyes };

        Assert.Equal(interfaceOff, MotionPolicy.InterfaceMotionOff(appearance, windowsAnimations));
        Assert.Equal(eyesOff, MotionPolicy.EyeMotionOff(appearance, windowsAnimations));
    }

    [Fact]
    public void TheDefaultsLetEverythingMove()
    {
        Assert.False(MotionPolicy.InterfaceMotionOff(LunarSettings.Defaults.Appearance, windowsAnimationsOn: true));
        Assert.False(MotionPolicy.EyeMotionOff(LunarSettings.Defaults.Appearance, windowsAnimationsOn: true));
    }
}
