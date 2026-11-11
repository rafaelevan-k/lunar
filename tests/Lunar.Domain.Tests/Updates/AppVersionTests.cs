using Lunar.Domain.Updates;
using Xunit;

namespace Lunar.Domain.Tests.Updates;

// BE-44, ADR-0025: the version comparison that decides whether a release is newer.
public sealed class AppVersionTests
{
    [Theory]
    [InlineData("v1.0.5", 1, 0, 5)]
    [InlineData("1.0.6", 1, 0, 6)]
    [InlineData("V2.10.0", 2, 10, 0)]
    [InlineData("1.2", 1, 2, 0)]
    [InlineData("1.0.5.0", 1, 0, 5)]
    [InlineData(" v1.0.7 ", 1, 0, 7)]
    public void ReleaseTagsAndFileVersionsParse(string text, int major, int minor, int patch)
    {
        Assert.True(AppVersion.TryParse(text, out var version));
        Assert.Equal(new AppVersion(major, minor, patch), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v1")]
    [InlineData("v1.0.6-beta")]
    [InlineData("1.0.6+build5")]
    [InlineData("1.0.5.1")]
    [InlineData("1.-1.0")]
    [InlineData("1..0")]
    [InlineData("1.0.0.0.0")]
    [InlineData("9999999.0.0")]
    [InlineData("１.０.５")] // full-width digits are not ASCII digits
    public void AnythingElseIsRefused(string? text) => Assert.False(AppVersion.TryParse(text, out _));

    [Theory]
    [InlineData("1.0.6", "1.0.5")]
    [InlineData("1.1.0", "1.0.99")]
    [InlineData("2.0.0", "1.99.99")]
    [InlineData("1.0.10", "1.0.9")] // numbers, not text
    public void HigherVersionsCompareHigher(string higher, string lower)
    {
        AppVersion.TryParse(higher, out var a);
        AppVersion.TryParse(lower, out var b);
        Assert.True(a > b);
        Assert.True(b < a);
        Assert.False(a <= b);
    }

    [Fact]
    public void TheRunningAssemblyVersionIgnoresItsFourthPart() =>
        Assert.Equal(new AppVersion(1, 0, 5), AppVersion.From(new Version(1, 0, 5, 0)));

    [Fact]
    public void ADigestMustBeSixtyFourHexCharacters()
    {
        Assert.True(UpdateRules.IsSha256Hex(new string('a', 64)));
        Assert.True(UpdateRules.IsSha256Hex("87A42AE27D683731BEC08FB9F8118616DC3492EEEFEE2CFFA487F366C835A2A4"));
        Assert.False(UpdateRules.IsSha256Hex(new string('a', 63)));
        Assert.False(UpdateRules.IsSha256Hex(new string('g', 64)));
        Assert.False(UpdateRules.IsSha256Hex(null));
    }
}
