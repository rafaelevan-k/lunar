using System.Windows;
using Lunar.Application.Abstractions.System;
using Lunar.Application.Settings.Models;
using Lunar.Desktop.Themes;
using Xunit;

namespace Lunar.Desktop.UITests.Themes;

// The palette choice and the swap of the application's palette dictionary, with fakes for Windows' theme, High Contrast and the loader. How the
// real palettes look is covered by the theme token tests; that the real Windows theme switch raises the notification is a manual check.
public sealed class ThemeApplierTests
{
    internal sealed class FakeThemeSource : ISystemThemeSource
    {
        public bool PrefersLight { get; set; }

        public int Subscribers => Handlers?.GetInvocationList().Length ?? 0;

        private Action? Handlers { get; set; }

        public event Action? Changed
        {
            add => Handlers += value;
            remove => Handlers -= value;
        }

        public void Raise() => Handlers?.Invoke();
    }

    private sealed class Rig
    {
        public FakeThemeSource System { get; } = new();

        public bool HighContrast { get; set; }

        public List<string> Loaded { get; } = [];

        public List<Action> Posted { get; } = [];

        public ResourceDictionary Resources { get; } = new();

        public ThemeApplier Applier { get; }

        public ResourceDictionary Palette => Resources.MergedDictionaries[0];

        public string PaletteName => (string)Palette["Name"];

        public Rig()
        {
            Resources.MergedDictionaries.Add(new ResourceDictionary { ["Name"] = "start" });
            Resources.MergedDictionaries.Add(new ResourceDictionary { ["Name"] = "typography" });
            Applier = new ThemeApplier(Resources, System, () => HighContrast, name =>
            {
                Loaded.Add(name);
                return new ResourceDictionary { ["Name"] = name };
            }, work => Posted.Add(work));
        }

        public void RunPosted()
        {
            var work = Posted.ToArray();
            Posted.Clear();
            foreach (var item in work)
                item();
        }
    }

    [Theory]
    [InlineData(ThemePreference.Dark, false, false, ThemeApplier.Dark)]
    [InlineData(ThemePreference.Dark, true, false, ThemeApplier.Dark)]
    [InlineData(ThemePreference.Light, false, false, ThemeApplier.Light)]
    [InlineData(ThemePreference.Light, true, false, ThemeApplier.Light)]
    [InlineData(ThemePreference.System, false, false, ThemeApplier.Dark)]
    [InlineData(ThemePreference.System, true, false, ThemeApplier.Light)]
    [InlineData(ThemePreference.Dark, false, true, ThemeApplier.HighContrast)]
    [InlineData(ThemePreference.Light, true, true, ThemeApplier.HighContrast)]
    [InlineData(ThemePreference.System, true, true, ThemeApplier.HighContrast)]
    public void TheChoiceFollowsTheSettingWindowsThemeAndHighContrast(ThemePreference preference, bool windowsLight, bool highContrast, string expected)
    {
        var rig = new Rig { HighContrast = highContrast };
        rig.System.PrefersLight = windowsLight;

        Assert.Equal(expected, rig.Applier.Choose(preference));
    }

    [Fact]
    public void ApplyReplacesOnlyThePaletteAndNothingElse()
    {
        var rig = new Rig();
        var typography = rig.Resources.MergedDictionaries[1];

        rig.Applier.Apply(ThemePreference.Light);

        Assert.Equal(ThemeApplier.Light, rig.PaletteName);
        Assert.Same(typography, rig.Resources.MergedDictionaries[1]);
        Assert.Equal(2, rig.Resources.MergedDictionaries.Count);
        Assert.Equal(ThemeApplier.Light, rig.Applier.Current);
    }

    [Fact]
    public void TheSamePaletteIsNotLoadedAgain()
    {
        var rig = new Rig();

        rig.Applier.Apply(ThemePreference.Dark);
        rig.Applier.Apply(ThemePreference.Dark);
        rig.Applier.Apply(ThemePreference.System); // Windows is dark: the same palette
        rig.Applier.Apply(ThemePreference.Light);

        Assert.Equal([ThemeApplier.Dark, ThemeApplier.Light], rig.Loaded);
    }

    [Fact]
    public void FollowingWindowsSwapsWhenWindowsChangesAndOnlyThen()
    {
        var rig = new Rig();
        rig.Applier.Apply(ThemePreference.System);
        Assert.Equal(ThemeApplier.Dark, rig.PaletteName);

        rig.System.PrefersLight = true;
        rig.System.Raise();
        Assert.Empty(rig.Loaded.Skip(1)); // the swap is handed to the UI thread, not done on the notifying thread
        rig.RunPosted();

        Assert.Equal(ThemeApplier.Light, rig.PaletteName);

        rig.Applier.Apply(ThemePreference.Dark); // an explicit choice ignores Windows
        rig.System.PrefersLight = false;
        rig.System.Raise();
        rig.System.PrefersLight = true;
        rig.RunPosted();
        Assert.Equal(ThemeApplier.Dark, rig.PaletteName);
    }

    [Fact]
    public void HighContrastWinsAndTheChoiceComesBackWhenItEnds()
    {
        var rig = new Rig();
        rig.Applier.Apply(ThemePreference.Light);

        rig.HighContrast = true;
        rig.Applier.Apply(ThemePreference.Light);
        Assert.Equal(ThemeApplier.HighContrast, rig.PaletteName);

        rig.HighContrast = false;
        rig.Applier.Apply(ThemePreference.Light);
        Assert.Equal(ThemeApplier.Light, rig.PaletteName);
    }

    [Fact]
    public void DisposingStopsListeningAndIgnoresLaterChanges()
    {
        var rig = new Rig();
        rig.Applier.Apply(ThemePreference.System);
        Assert.Equal(1, rig.System.Subscribers);

        rig.Applier.Dispose();
        rig.Applier.Dispose();
        rig.System.PrefersLight = true;
        rig.System.Raise();
        rig.Applier.Apply(ThemePreference.Light);
        rig.RunPosted();

        Assert.Equal(0, rig.System.Subscribers);
        Assert.Equal(ThemeApplier.Dark, rig.PaletteName);
    }

    [Fact]
    public void ThePaletteFilesTheApplierLoadsExist()
    {
        // The three names the applier loads are the three palette files the application ships.
        var desktop = ThemeTokenTests.DesktopDirectory;
        foreach (var name in new[] { ThemeApplier.Dark, ThemeApplier.Light, ThemeApplier.HighContrast })
            Assert.True(File.Exists(Path.Combine(desktop, "Themes", name)), name);
    }
}
