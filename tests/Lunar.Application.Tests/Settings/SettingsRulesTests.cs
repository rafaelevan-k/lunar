using Lunar.Application.Settings;
using Lunar.Application.Settings.Models;
using Xunit;

namespace Lunar.Application.Tests.Settings;

public sealed class SettingsRulesTests
{
    [Fact]
    public void DefaultsAreSafeAndAlreadyNormalized()
    {
        var defaults = LunarSettings.Defaults;

        Assert.False(defaults.LaunchAtStartup);
        Assert.Null(defaults.Locale);
        Assert.Null(defaults.Island.PreferredMonitorId);
        Assert.Equal(8, defaults.Island.TopOffsetDip);
        Assert.Equal(ThemePreference.System, defaults.Appearance.Theme);
        Assert.False(defaults.Appearance.ReduceMotion);
        Assert.True(defaults.Appearance.EyeAnimationEnabled);
        Assert.True(defaults.Appearance.AutoCollapse);
        Assert.Null(defaults.Media.PreferredSessionId);
        Assert.Null(defaults.Conversion.OutputDirectory);
        Assert.False(defaults.Privacy.DiagnosticsEnabled);
        Assert.False(defaults.Usage.Enabled); // no network request until the user turns the Usage page on (ADR-0014)

        var normalized = SettingsRules.Normalize(defaults, out var changed);

        Assert.False(changed);
        Assert.Equal(defaults, normalized);
    }

    [Fact]
    public void NullSettingsAreRejected() =>
        Assert.Throws<ArgumentNullException>(() => SettingsRules.Normalize(null!, out _));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(200, 200)]
    [InlineData(37, 37)]
    [InlineData(-1, 8)]
    [InlineData(201, 8)]
    [InlineData(int.MinValue, 8)]
    [InlineData(int.MaxValue, 8)]
    public void TopOffsetOutsideRangeBecomesDefault(int input, int expected)
    {
        var settings = new LunarSettings { Island = new IslandSettings { TopOffsetDip = input } };

        var normalized = SettingsRules.Normalize(settings, out var changed);

        Assert.Equal(expected, normalized.Island.TopOffsetDip);
        Assert.Equal(input != expected, changed);
    }

    [Fact]
    public void UndefinedThemeBecomesSystem()
    {
        var settings = new LunarSettings { Appearance = new AppearanceSettings { Theme = (ThemePreference)99 } };

        var normalized = SettingsRules.Normalize(settings, out var changed);

        Assert.Equal(ThemePreference.System, normalized.Appearance.Theme);
        Assert.True(changed);
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void DefinedThemesAreKept(string name)
    {
        var theme = Enum.Parse<ThemePreference>(name);
        var settings = new LunarSettings { Appearance = new AppearanceSettings { Theme = theme } };

        var normalized = SettingsRules.Normalize(settings, out var changed);

        Assert.Equal(theme, normalized.Appearance.Theme);
        Assert.False(changed);
    }

    [Theory]
    [InlineData("  ", null)]
    [InlineData("", null)]
    [InlineData("\\\\.\\DISPLAY1", "\\\\.\\DISPLAY1")]
    [InlineData("  \\\\.\\DISPLAY2  ", "\\\\.\\DISPLAY2")]
    [InlineData("bad\nid", null)]
    [InlineData("bad\0id", null)]
    public void IdsAreTrimmedAndControlCharactersRejected(string input, string? expected)
    {
        var settings = new LunarSettings
        {
            Island = new IslandSettings { PreferredMonitorId = input },
            Media = new MediaSettings { PreferredSessionId = input }
        };

        var normalized = SettingsRules.Normalize(settings, out _);

        Assert.Equal(expected, normalized.Island.PreferredMonitorId);
        Assert.Equal(expected, normalized.Media.PreferredSessionId);
    }

    [Fact]
    public void IdsAndPathsLongerThanTheirLimitAreReset()
    {
        var settings = new LunarSettings
        {
            Island = new IslandSettings { PreferredMonitorId = new string('a', SettingsRules.MaxIdLength + 1) },
            Media = new MediaSettings { PreferredSessionId = new string('b', SettingsRules.MaxIdLength + 1) },
            Conversion = new ConversionSettings { OutputDirectory = "C:\\" + new string('c', SettingsRules.MaxPathLength) }
        };

        var normalized = SettingsRules.Normalize(settings, out var changed);

        Assert.True(changed);
        Assert.Null(normalized.Island.PreferredMonitorId);
        Assert.Null(normalized.Media.PreferredSessionId);
        Assert.Null(normalized.Conversion.OutputDirectory);
    }

    [Fact]
    public void IdAtExactlyTheLimitIsKept()
    {
        var id = new string('a', SettingsRules.MaxIdLength);
        var settings = new LunarSettings { Media = new MediaSettings { PreferredSessionId = id } };

        var normalized = SettingsRules.Normalize(settings, out var changed);

        Assert.Equal(id, normalized.Media.PreferredSessionId);
        Assert.False(changed);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("id-ID")]
    [InlineData("zh-Hant-TW")]
    public void PlausibleLocaleTagsAreKept(string tag)
    {
        var normalized = SettingsRules.Normalize(new LunarSettings { Locale = tag }, out var changed);

        Assert.Equal(tag, normalized.Locale);
        Assert.False(changed);
    }

    [Theory]
    [InlineData("1en")]
    [InlineData("en_US")]
    [InlineData("en US")]
    [InlineData("-en")]
    [InlineData("en-\u00e9")]
    [InlineData("")]
    public void ImplausibleLocaleTagsAreReset(string tag)
    {
        var normalized = SettingsRules.Normalize(new LunarSettings { Locale = tag }, out _);

        Assert.Null(normalized.Locale);
    }

    [Fact]
    public void LocaleLongerThanTheLimitIsReset()
    {
        var tag = "en-" + new string('x', SettingsRules.MaxLocaleLength);

        var normalized = SettingsRules.Normalize(new LunarSettings { Locale = tag }, out var changed);

        Assert.Null(normalized.Locale);
        Assert.True(changed);
    }

    [Fact]
    public void ValidSiblingsSurviveAnInvalidValue()
    {
        var settings = new LunarSettings
        {
            LaunchAtStartup = true,
            Island = new IslandSettings { TopOffsetDip = 9999, PreferredMonitorId = "\\\\.\\DISPLAY2" },
            Appearance = new AppearanceSettings { ReduceMotion = true, Theme = ThemePreference.Dark }
        };

        var normalized = SettingsRules.Normalize(settings, out var changed);

        Assert.True(changed);
        Assert.True(normalized.LaunchAtStartup);
        Assert.Equal(8, normalized.Island.TopOffsetDip);
        Assert.Equal("\\\\.\\DISPLAY2", normalized.Island.PreferredMonitorId);
        Assert.True(normalized.Appearance.ReduceMotion);
        Assert.Equal(ThemePreference.Dark, normalized.Appearance.Theme);
    }
}
