using System.Windows;
using Lunar.Application.Abstractions.Settings;
using Lunar.Application.Settings;
using Lunar.Application.Settings.Models;
using Lunar.Desktop.Features.Settings;
using Lunar.Desktop.Themes;
using Lunar.Desktop.UITests.Themes;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Desktop.UITests.Features;

// ADR-0016: the saved settings reach every part that follows them, at start-up and after each change. Windows' own switches are faked.
public sealed class SettingsApplierTests : IDisposable
{
    private sealed class Repository : ISettingsRepository
    {
        public LunarSettings Stored { get; set; } = LunarSettings.Defaults;

        public Task<Result<SettingsLoadResult>> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success(new SettingsLoadResult(Stored, SettingsRecovery.None)));

        public Task<Result> SaveAsync(LunarSettings settings, CancellationToken cancellationToken)
        {
            Stored = settings;
            return Task.FromResult(Result.Success());
        }
    }

    private readonly SettingsService _service = new(new Repository());
    private readonly ThemeApplierTests.FakeThemeSource _system = new();
    private readonly List<Action> _posted = [];
    private readonly List<ThemePreference> _themes = [];
    private readonly List<IslandSettings> _positions = [];
    private readonly List<(IslandSettings Island, bool Off)> _toasts = [];
    private readonly List<bool> _interfaceMotion = [];
    private readonly List<bool> _eyeMotion = [];
    private readonly List<string?> _folders = [];
    private readonly ResourceDictionary _resources = new();
    private readonly ThemeApplier _theme;
    private readonly SettingsApplier _applier;
    private bool _windowsAnimations = true;

    public SettingsApplierTests()
    {
        _resources.MergedDictionaries.Add(new ResourceDictionary());
        _theme = new ThemeApplier(_resources, _system, () => false, name =>
        {
            _themes.Add(name == ThemeApplier.Light ? ThemePreference.Light : ThemePreference.Dark);
            return new ResourceDictionary();
        }, work => _posted.Add(work));
        _applier = new SettingsApplier(
            _service,
            work => _posted.Add(work),
            () => _windowsAnimations,
            _theme,
            island => _positions.Add(island),
            (island, off) => _toasts.Add((island, off)),
            off => _interfaceMotion.Add(off),
            off => _eyeMotion.Add(off),
            folder => _folders.Add(folder),
            watchWindows: false);
    }

    public void Dispose()
    {
        _applier.Dispose();
        _theme.Dispose();
        _service.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RunPosted()
    {
        var work = _posted.ToArray();
        _posted.Clear();
        foreach (var item in work)
            item();
    }

    // FE-25: an applier that is disposed no longer hears the service, however often one is made and let go.
    [Fact]
    public void ADisposedApplierLeavesNoSubscriptionOnTheService()
    {
        var baseline = Lifecycle.EventAudit.HandlerCount(_service, nameof(SettingsService.Changed));

        for (var i = 0; i < 5; i++)
        {
            var extra = new SettingsApplier(_service, _ => { }, () => true, _theme, _ => { }, (_, _) => { }, _ => { }, _ => { }, _ => { }, watchWindows: false);
            Assert.Equal(baseline + 1, Lifecycle.EventAudit.HandlerCount(_service, nameof(SettingsService.Changed)));
            extra.Dispose();
            extra.Dispose();
            Assert.Equal(baseline, Lifecycle.EventAudit.HandlerCount(_service, nameof(SettingsService.Changed)));
        }
    }

    [Fact]
    public void ApplyTellsEveryPartWhatTheSettingsSay()
    {
        var settings = LunarSettings.Defaults with
        {
            Appearance = new AppearanceSettings { Theme = ThemePreference.Light, ReduceMotion = false, EyeAnimationEnabled = false },
            Island = new IslandSettings { PreferredMonitorId = @"\\.\DISPLAY2", TopOffsetDip = 30 },
            Conversion = new ConversionSettings { OutputDirectory = @"C:\Out" }
        };

        _applier.Apply(settings);

        Assert.Equal([ThemePreference.Light], _themes);
        Assert.Equal([settings.Island], _positions);
        Assert.Equal([(settings.Island, false)], _toasts);
        Assert.Equal([false], _interfaceMotion);
        Assert.Equal([true], _eyeMotion); // idle eyes off stops only the eyes
        Assert.Equal([@"C:\Out"], _folders);
    }

    [Fact]
    public void ReduceMotionAndWindowsAnimationsStopEverything()
    {
        _applier.Apply(LunarSettings.Defaults with { Appearance = new AppearanceSettings { ReduceMotion = true } });
        _windowsAnimations = false;
        _applier.Apply(LunarSettings.Defaults);

        Assert.Equal([true, true], _interfaceMotion);
        Assert.Equal([true, true], _eyeMotion);
        Assert.Equal([true, true], _toasts.Select(t => t.Off));
    }

    [Fact]
    public async Task ASavedChangeIsAppliedOnTheUiThreadWithTheNewValues()
    {
        _applier.Apply(_service.Current);
        _positions.Clear();
        _folders.Clear();

        var saved = await _service.UpdateAsync(s => s with
        {
            Island = s.Island with { TopOffsetDip = 44 },
            Conversion = s.Conversion with { OutputDirectory = @"D:\Converted" },
            Appearance = s.Appearance with { ReduceMotion = true }
        }, Ct);

        Assert.True(saved.IsSuccess);
        Assert.Empty(_positions); // handed to the UI thread first
        RunPosted();
        Assert.Equal(44, Assert.Single(_positions).TopOffsetDip);
        Assert.Equal([@"D:\Converted"], _folders);
        Assert.True(_interfaceMotion[^1]);
    }

    [Fact]
    public async Task AFailedOrUnchangedSaveAppliesNothing()
    {
        _applier.Apply(_service.Current);
        _positions.Clear();

        await _service.UpdateAsync(s => s, Ct);
        RunPosted();

        Assert.Empty(_positions);
    }

    [Fact]
    public async Task AfterDisposeNothingIsAppliedAnyMore()
    {
        _applier.Dispose();
        _applier.Dispose();

        await _service.UpdateAsync(s => s with { Island = s.Island with { TopOffsetDip = 90 } }, Ct);
        RunPosted();
        _applier.Apply(_service.Current);

        Assert.Empty(_positions);
        Assert.Empty(_themes);
    }

    [Fact]
    public void ARequiredPartIsNeverNull()
    {
        Assert.Throws<ArgumentNullException>(() => new SettingsApplier(_service, _ => { }, () => true, _theme, null!, (_, _) => { }, _ => { }, _ => { }, _ => { }, watchWindows: false));
        Assert.Throws<ArgumentNullException>(() => _applier.Apply(null!));
    }
}
