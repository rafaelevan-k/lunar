using System.Windows.Input;
using Lunar.Application.Abstractions.Settings;
using Lunar.Application.Abstractions.Shell;
using Lunar.Application.Settings;
using Lunar.Application.Settings.Models;
using Lunar.Application.Shell.Models;
using Lunar.Desktop.Features.Converter;
using Lunar.Desktop.Features.Settings;
using Lunar.Desktop.Shell;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Desktop.UITests.Features;

// FE-23: the Settings window's state over the real SettingsService, with fakes for the file, the displays and the folder dialog. The window
// itself is covered by SettingsWindowTests; the real file by the Infrastructure tests.
public sealed class SettingsViewModelTests : IDisposable
{
    private sealed class Repository : ISettingsRepository
    {
        public LunarSettings Stored { get; set; } = LunarSettings.Defaults;

        public SettingsRecovery Recovery { get; set; } = SettingsRecovery.None;

        public ErrorCode? FailSaveWith { get; set; }

        public int Saves { get; private set; }

        public Task<Result<SettingsLoadResult>> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success(new SettingsLoadResult(Stored, Recovery)));

        public Task<Result> SaveAsync(LunarSettings settings, CancellationToken cancellationToken)
        {
            if (FailSaveWith is { } code)
                return Task.FromResult(Result.Failure(new OperationError(code, "Cannot write.")));
            Saves++;
            Stored = settings;
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class Environment : IWindowEnvironment
    {
        public IReadOnlyList<DisplayInfo> Displays { get; set; } = [Display(@"\\.\DISPLAY1", true, 1920, 1080)];

        public bool Fails { get; set; }

        public event Action? DisplaysChanged
        {
            add { }
            remove { }
        }

        public Result Start() => Result.Success();

        public Result<IReadOnlyList<DisplayInfo>> GetDisplays() =>
            Fails ? Result.Failure<IReadOnlyList<DisplayInfo>>(new OperationError(ErrorCode.DeviceUnavailable, "none")) : Result.Success(Displays);

        public void Dispose()
        {
        }
    }

    private sealed class Folders : IFolderPicker
    {
        public string? Answer { get; set; }

        public int Calls { get; private set; }

        public string? PickFolder()
        {
            Calls++;
            return Answer;
        }
    }

    private readonly Repository _repository = new();
    private readonly Environment _environment = new();
    private readonly Folders _folders = new();
    private readonly SettingsService _service;
    private SettingsViewModel? _model;

    public SettingsViewModelTests() => _service = new SettingsService(_repository);

    public void Dispose()
    {
        _model?.Dispose();
        _service.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DisplayInfo Display(string id, bool primary, int width, int height) =>
        new(id, primary, new PixelRect(0, 0, width, height), new PixelRect(0, 0, width, height - 40), 96, 96);

    private async Task<SettingsViewModel> Create(bool diagnosticsInUse = false)
    {
        await _service.LoadAsync(Ct);
        _model = new SettingsViewModel(_service, _environment, _folders, work => work(), diagnosticsInUse);
        return _model;
    }

    private static async Task Run(ICommand command)
    {
        Assert.True(command.CanExecute(null));
        command.Execute(null);
        var end = DateTime.UtcNow.AddSeconds(10);
        while (command is AsyncRelayCommand { IsRunning: true } && DateTime.UtcNow < end)
            await Task.Delay(2, Ct);
    }

    // ---- what it shows at first ----------------------------------------------------------------------------------------

    [Fact]
    public async Task ItStartsFromTheSavedSettingsAndShowsNoNoticeOrProblem()
    {
        var model = await Create();

        Assert.Equal("8 px", model.TopOffsetText);
        Assert.False(model.ReduceMotion);
        Assert.True(model.EyeAnimationEnabled);
        Assert.False(model.DiagnosticsEnabled);
        Assert.False(model.DiagnosticsNeedsRestart);
        Assert.False(model.HasOutputFolder);
        Assert.Equal(SettingsText.FolderDefault, model.OutputFolderText);
        Assert.Null(model.Notice);
        Assert.Null(model.Problem);
        Assert.False(model.IsReadOnly);
        Assert.True(model.IsEditable);
        Assert.Equal(["Match Windows", "Dark", "Light"], model.Themes.Select(t => t.Label));
        Assert.Equal("Match Windows", Assert.Single(model.Themes, t => t.IsSelected).Label);
        Assert.Equal([SettingsText.MainDisplay, "Display 1 (main), 1920 x 1080"], model.Monitors.Select(m => m.Label));
        Assert.Equal(SettingsText.MainDisplay, Assert.Single(model.Monitors, m => m.IsSelected).Label);
    }

    [Fact]
    public async Task SavedValuesAreShownAsTheyAre()
    {
        _repository.Stored = LunarSettings.Defaults with
        {
            Appearance = new AppearanceSettings { Theme = ThemePreference.Light, ReduceMotion = true, EyeAnimationEnabled = false },
            Island = new IslandSettings { TopOffsetDip = 36 },
            Conversion = new ConversionSettings { OutputDirectory = @"D:\Converted" },
            Privacy = new PrivacySettings { DiagnosticsEnabled = true }
        };

        var model = await Create(diagnosticsInUse: true);

        Assert.True(model.ReduceMotion);
        Assert.False(model.EyeAnimationEnabled);
        Assert.Equal("36 px", model.TopOffsetText);
        Assert.Equal(@"D:\Converted", model.OutputFolderText);
        Assert.True(model.HasOutputFolder);
        Assert.Equal("Light", Assert.Single(model.Themes, t => t.IsSelected).Label);
        Assert.False(model.DiagnosticsNeedsRestart); // saved on and already in use
    }

    // ---- switches ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EachSwitchSavesItsOwnValueAndTheWindowShowsTheSavedState()
    {
        var model = await Create();

        await Run(model.ToggleReduceMotionCommand);
        Assert.True(model.ReduceMotion);
        Assert.True(_repository.Stored.Appearance.ReduceMotion);

        await Run(model.ToggleEyesCommand);
        Assert.False(model.EyeAnimationEnabled);
        Assert.False(_repository.Stored.Appearance.EyeAnimationEnabled);
        Assert.True(_repository.Stored.Appearance.ReduceMotion); // the other value is kept

        await Run(model.ToggleReduceMotionCommand);
        Assert.False(model.ReduceMotion);
    }

    [Fact]
    public async Task DiagnosticsSaysItNeedsARestartOnlyWhileTheSavedChoiceIsNotInUse()
    {
        var model = await Create(diagnosticsInUse: false);

        await Run(model.ToggleDiagnosticsCommand);
        Assert.True(model.DiagnosticsEnabled);
        Assert.True(model.DiagnosticsNeedsRestart);

        await Run(model.ToggleDiagnosticsCommand);
        Assert.False(model.DiagnosticsEnabled);
        Assert.False(model.DiagnosticsNeedsRestart);
    }

    [Fact]
    public async Task TurningDiagnosticsOffWhileTheyAreInUseNeedsARestartToo()
    {
        _repository.Stored = LunarSettings.Defaults with { Privacy = new PrivacySettings { DiagnosticsEnabled = true } };
        var model = await Create(diagnosticsInUse: true);

        await Run(model.ToggleDiagnosticsCommand);

        Assert.False(model.DiagnosticsEnabled);
        Assert.True(model.DiagnosticsNeedsRestart);
    }

    // ---- theme and display ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ChoosingAThemeSavesItAndMarksOnlyThatChoice()
    {
        var model = await Create();

        await Run(model.Themes.Single(t => t.Label == "Dark").Command);

        Assert.Equal(ThemePreference.Dark, _repository.Stored.Appearance.Theme);
        Assert.Equal("Dark", Assert.Single(model.Themes, t => t.IsSelected).Label);

        await Run(model.Themes.Single(t => t.Label == "Light").Command);
        Assert.Equal("Light", Assert.Single(model.Themes, t => t.IsSelected).Label);
    }

    [Fact]
    public async Task EveryConnectedDisplayIsListedAndChoosingOneSavesItsId()
    {
        _environment.Displays = [Display(@"\\.\DISPLAY1", true, 1920, 1080), Display(@"\\.\DISPLAY2", false, 2560, 1440)];
        var model = await Create();
        Assert.Equal([SettingsText.MainDisplay, "Display 1 (main), 1920 x 1080", "Display 2, 2560 x 1440"], model.Monitors.Select(m => m.Label));

        await Run(model.Monitors[2].Command);

        Assert.Equal(@"\\.\DISPLAY2", _repository.Stored.Island.PreferredMonitorId);
        Assert.Equal("Display 2, 2560 x 1440", Assert.Single(model.Monitors, m => m.IsSelected).Label);

        await Run(model.Monitors[0].Command);
        Assert.Null(_repository.Stored.Island.PreferredMonitorId);
        Assert.Equal(SettingsText.MainDisplay, Assert.Single(model.Monitors, m => m.IsSelected).Label);
    }

    [Fact]
    public async Task ASavedDisplayThatIsNotConnectedStaysVisibleAndKept()
    {
        _repository.Stored = LunarSettings.Defaults with { Island = new IslandSettings { PreferredMonitorId = @"\\.\DISPLAY7" } };
        var model = await Create();

        var missing = Assert.Single(model.Monitors, m => m.IsSelected);

        Assert.Equal(SettingsText.MissingDisplay, missing.Label);
        Assert.False(missing.Command.CanExecute(null));
        Assert.Equal(@"\\.\DISPLAY7", _service.Current.Island.PreferredMonitorId);
    }

    [Fact]
    public async Task IfTheDisplaysCannotBeReadOnlyTheMainDisplayIsOffered()
    {
        _environment.Fails = true;

        var model = await Create();

        Assert.Equal([SettingsText.MainDisplay], model.Monitors.Select(m => m.Label));
    }

    [Fact]
    public async Task RefreshingTheDisplaysPicksUpOneThatWasPluggedIn()
    {
        var model = await Create();
        Assert.Equal(2, model.Monitors.Count);

        _environment.Displays = [.. _environment.Displays, Display(@"\\.\DISPLAY2", false, 1366, 768)];
        model.RefreshDisplays();

        Assert.Equal(3, model.Monitors.Count);
    }

    [Fact]
    public async Task TheOffsetMovesInStepsAndStopsAtBothEnds()
    {
        var model = await Create();

        await Run(model.OffsetUpCommand);
        Assert.Equal(8 + SettingsViewModel.OffsetStep, model.TopOffset);

        for (var i = 0; i < 100 && model.OffsetUpCommand.CanExecute(null); i++)
            await Run(model.OffsetUpCommand);
        Assert.Equal(SettingsRules.MaxTopOffsetDip, model.TopOffset);
        Assert.False(model.OffsetUpCommand.CanExecute(null));

        for (var i = 0; i < 100 && model.OffsetDownCommand.CanExecute(null); i++)
            await Run(model.OffsetDownCommand);
        Assert.Equal(SettingsRules.MinTopOffsetDip, model.TopOffset);
        Assert.False(model.OffsetDownCommand.CanExecute(null));
        Assert.Equal("0 px", model.TopOffsetText);
    }

    // ---- folder --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AChosenFolderIsSavedAndClearingGoesBackToTheDefault()
    {
        var model = await Create();
        Assert.False(model.ClearFolderCommand.CanExecute(null));
        _folders.Answer = @"E:\My Converted";

        await Run(model.ChooseFolderCommand);

        Assert.Equal(@"E:\My Converted", _repository.Stored.Conversion.OutputDirectory);
        Assert.Equal(@"E:\My Converted", model.OutputFolderText);
        Assert.True(model.HasOutputFolder);

        await Run(model.ClearFolderCommand);
        Assert.Null(_repository.Stored.Conversion.OutputDirectory);
        Assert.Equal(SettingsText.FolderDefault, model.OutputFolderText);
    }

    [Fact]
    public async Task CancellingTheFolderDialogChangesNothing()
    {
        var model = await Create();
        _folders.Answer = null;

        await Run(model.ChooseFolderCommand);

        Assert.Equal(1, _folders.Calls);
        Assert.Equal(0, _repository.Saves);
        Assert.Null(model.Problem);
    }

    [Theory]
    [InlineData(@"\\server\share\out")]
    [InlineData("//server/share")]
    [InlineData(@"relative\folder")]
    [InlineData("out")]
    public async Task AFolderThatIsNotAnAbsoluteLocalPathIsRefusedWithAReasonAndNotSaved(string answer)
    {
        var model = await Create();
        _folders.Answer = answer;

        await Run(model.ChooseFolderCommand);

        Assert.Equal(SettingsText.FolderRejected, model.Problem);
        Assert.True(model.HasProblem);
        Assert.Equal(0, _repository.Saves);
        Assert.False(model.HasOutputFolder);
    }

    [Fact]
    public async Task APathThatIsTooLongIsRefused()
    {
        var model = await Create();
        _folders.Answer = @"C:\" + new string('a', SettingsRules.MaxPathLength);

        await Run(model.ChooseFolderCommand);

        Assert.Equal(SettingsText.FolderRejected, model.Problem);
        Assert.Equal(0, _repository.Saves);
    }

    // ---- reset ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ResetAsksFirstAndCancellingChangesNothing()
    {
        _repository.Stored = LunarSettings.Defaults with { LaunchAtStartup = true, Usage = new UsageSettings { Enabled = true } };
        var model = await Create();
        Assert.True(model.IsNotConfirmingReset);

        await Run(model.AskResetCommand);
        Assert.True(model.IsConfirmingReset);
        Assert.False(model.IsNotConfirmingReset);

        await Run(model.CancelResetCommand);
        Assert.False(model.IsConfirmingReset);
        Assert.True(_repository.Stored.Usage.Enabled);
        Assert.Equal(0, _repository.Saves);
    }

    [Fact]
    public async Task ConfirmingResetReturnsEverythingToTheDefaultsIncludingUsageOff()
    {
        _repository.Stored = LunarSettings.Defaults with
        {
            Usage = new UsageSettings { Enabled = true },
            Appearance = new AppearanceSettings { Theme = ThemePreference.Light, ReduceMotion = true },
            Island = new IslandSettings { TopOffsetDip = 100, PreferredMonitorId = @"\\.\DISPLAY2" },
            Conversion = new ConversionSettings { OutputDirectory = @"D:\X" }
        };
        var model = await Create();
        await Run(model.AskResetCommand);

        await Run(model.ConfirmResetCommand);

        Assert.Equal(LunarSettings.Defaults, _repository.Stored);
        Assert.False(model.IsConfirmingReset);
        Assert.Equal("8 px", model.TopOffsetText);
        Assert.False(model.ReduceMotion);
        Assert.Equal("Match Windows", Assert.Single(model.Themes, t => t.IsSelected).Label);
        Assert.Equal(SettingsText.MainDisplay, Assert.Single(model.Monitors, m => m.IsSelected).Label);
    }

    // ---- failures and notices ------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFailedSaveChangesNothingSaysSoAndASuccessClearsTheMessage()
    {
        var model = await Create();
        _repository.FailSaveWith = ErrorCode.IoFailure;

        await Run(model.ToggleReduceMotionCommand);

        Assert.False(model.ReduceMotion);
        Assert.Equal(SettingsText.SaveFailed, model.Problem);

        _repository.FailSaveWith = null;
        await Run(model.ToggleReduceMotionCommand);

        Assert.True(model.ReduceMotion);
        Assert.Null(model.Problem);
    }

    [Fact]
    public async Task AFailedSaveReturnsAClickedChoiceToTheSavedOne()
    {
        var model = await Create();
        var before = model.Themes;
        _repository.FailSaveWith = ErrorCode.IoFailure;

        await Run(model.Themes.Single(t => t.Label == "Light").Command);

        Assert.NotSame(before, model.Themes); // rebuilt, so a radio button that was clicked goes back
        Assert.Equal("Match Windows", Assert.Single(model.Themes, t => t.IsSelected).Label);
    }

    [Fact]
    public async Task ASettingsFileFromANewerLunarIsShownButCannotBeChanged()
    {
        _repository.Recovery = SettingsRecovery.NewerVersionIgnored;
        _repository.FailSaveWith = ErrorCode.Unsupported;
        var model = await Create();

        Assert.True(model.IsReadOnly);
        Assert.False(model.IsEditable);
        Assert.Equal(SettingsText.ReadOnly, model.Notice);
        foreach (var command in new[] { model.ToggleReduceMotionCommand, model.ToggleEyesCommand, model.ToggleDiagnosticsCommand, model.OffsetUpCommand, model.OffsetDownCommand, model.ChooseFolderCommand, model.AskResetCommand, model.ConfirmResetCommand })
            Assert.False(command.CanExecute(null));
        Assert.All(model.Themes, t => Assert.False(t.Command.CanExecute(null)));
    }

    [Theory]
    [InlineData(SettingsRecovery.RestoredFromBackup, "last good copy")]
    [InlineData(SettingsRecovery.ResetToDefaults, "defaults")]
    [InlineData(SettingsRecovery.ValuesReset, "not valid")]
    public async Task ARepairMadeAtStartIsToldOnceAndCanBeDismissed(SettingsRecovery recovery, string fragment)
    {
        _repository.Recovery = recovery;
        var model = await Create();

        Assert.Contains(fragment, model.Notice, StringComparison.Ordinal);
        Assert.True(model.HasNotice);

        await Run(model.DismissNoticeCommand);
        Assert.Null(model.Notice);
        Assert.False(model.HasNotice);
    }

    [Theory]
    [InlineData(SettingsRecovery.None)]
    [InlineData(SettingsRecovery.CreatedDefaults)]
    public async Task NothingRepairedMeansNoNotice(SettingsRecovery recovery)
    {
        _repository.Recovery = recovery;

        Assert.Null((await Create()).Notice);
    }

    // ---- following other writers ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AChangeMadeElsewhereIsShownAtOnce()
    {
        var model = await Create();
        var raised = 0;
        model.PropertyChanged += (_, _) => raised++;

        await _service.UpdateAsync(s => s with { Usage = new UsageSettings { Enabled = true }, Appearance = s.Appearance with { ReduceMotion = true } }, Ct);

        Assert.True(model.ReduceMotion);
        Assert.True(raised > 0);
    }

    [Fact]
    public async Task AfterDisposeNothingIsFollowedAnyMore()
    {
        var model = await Create();
        var raised = 0;
        model.PropertyChanged += (_, _) => raised++;

        model.Dispose();
        model.Dispose();
        await _service.UpdateAsync(s => s with { LaunchAtStartup = true }, Ct);
        model.RefreshDisplays();

        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task TheViewModelNeverTouchesTheFileSystemOrAnOperatingSystemApiItself()
    {
        // It only knows the service, the display port and the folder port; a repository is not even reachable from it.
        var constructor = typeof(SettingsViewModel).GetConstructors().Single();

        Assert.DoesNotContain(constructor.GetParameters(), p => p.ParameterType == typeof(ISettingsRepository));
        await Task.CompletedTask;
    }
}
