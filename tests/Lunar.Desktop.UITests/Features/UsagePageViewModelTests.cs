using System.Globalization;
using System.Windows.Input;
using Lunar.Application.Abstractions.Settings;
using Lunar.Application.Abstractions.Usage;
using Lunar.Application.Settings.Models;
using Lunar.Application.Usage;
using Lunar.Application.Usage.Models;
using Lunar.Desktop.Features.Usage;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Desktop.UITests.Features;

// FE-27: the Usage page's view model against fakes of the three ports it uses. The monitor is the real one with a wait that never ends, so
// nothing is requested after the first reading; the real service, a real token and the real island are manual checks (NOT_RUN here).
public sealed class UsagePageViewModelTests : IDisposable
{
    internal const string Secret = "sk-ant-oat01-THIS-IS-A-TEST-TOKEN-0123456789";
    internal static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    internal sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal sealed class Source : IUsageSource
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Func<Result<UsageSnapshot>> Next { get; set; } = () => Result.Success(Reading(0.67, 0.31));

        public Task<Result<UsageSnapshot>> FetchAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(Next());
        }
    }

    internal sealed class Tokens : IUsageTokenStore
    {
        public bool Has { get; set; }

        public Func<string, Result> OnSave { get; set; } = _ => Result.Success();

        public string? LastSaved { get; private set; }

        public int Removes { get; private set; }

        public Task<Result<bool>> HasTokenAsync(CancellationToken ct) => Task.FromResult(Result.Success(Has));

        public Task<Result> SaveAsync(string pastedToken, CancellationToken ct)
        {
            LastSaved = pastedToken;
            var result = OnSave(pastedToken);
            if (result.IsSuccess)
                Has = true;
            return Task.FromResult(result);
        }

        public Task<Result> RemoveAsync(CancellationToken ct)
        {
            Removes++;
            Has = false;
            return Task.FromResult(Result.Success());
        }

        public Task<Result<UsageCredential>> GetAsync(CancellationToken ct) =>
            Task.FromResult(Has
                ? Result.Success(new UsageCredential(Secret))
                : Result.Failure<UsageCredential>(new OperationError(ErrorCode.AccessDenied, "Paste your token again.")));
    }

    internal sealed class Settings : ISettingsRepository
    {
        public LunarSettings Current { get; set; } = LunarSettings.Defaults;

        public bool FailSave { get; set; }

        public bool FailLoad { get; set; }

        public int Saves { get; private set; }

        public Task<Result<SettingsLoadResult>> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(FailLoad
                ? Result.Failure<SettingsLoadResult>(new OperationError(ErrorCode.IoFailure, "Cannot read."))
                : Result.Success(new SettingsLoadResult(Current, SettingsRecovery.None)));

        public Task<Result> SaveAsync(LunarSettings settings, CancellationToken cancellationToken)
        {
            if (FailSave)
                return Task.FromResult(Result.Failure(new OperationError(ErrorCode.IoFailure, "Cannot write.")));
            Saves++;
            Current = settings;
            return Task.FromResult(Result.Success());
        }
    }

    private readonly Source _source = new();
    private readonly Tokens _tokens = new();
    private readonly Settings _settings = new();
    private readonly UsageMonitor _monitor;
    private readonly UsagePageViewModel _model;

    public UsagePageViewModelTests()
    {
        _monitor = new UsageMonitor(_source, new UsageMonitorOptions { Delay = (_, ct) => Task.Delay(Timeout.Infinite, ct), RefreshGap = TimeSpan.Zero });
        _model = new UsagePageViewModel(_monitor, _tokens, _settings, work => work(), log: null, new FixedTime(), CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        _model.Dispose();
        _monitor.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static UsageSnapshot Reading(double fiveHour, double weekly) => new(
        new UsageWindow(UsageWindowKind.FiveHour, fiveHour, Now.AddMinutes(125)),
        new UsageWindow(UsageWindowKind.Weekly, weekly, Now.AddDays(3)),
        Now);

    private static async Task WaitFor(Func<bool> condition)
    {
        var end = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < end)
            await Task.Delay(5, Ct);
        Assert.True(condition(), "The expected state did not arrive in time.");
    }

    private async Task Start(bool enabled, bool token)
    {
        _settings.Current = LunarSettings.Defaults with { Usage = new UsageSettings { Enabled = enabled } };
        _tokens.Has = token;
        await _model.InitializeAsync(Ct);
    }

    [Fact]
    public async Task ByDefaultThePageIsOffAndNothingIsRequested()
    {
        await Start(enabled: false, token: true);

        Assert.Equal(UsagePageMode.Off, _model.Mode);
        Assert.True(_model.IsOff);
        Assert.False(_model.IsEnabled);
        Assert.False(_model.ShowLimits);
        Assert.False(_model.ShowTokenEntry);
        Assert.False(_monitor.IsRunning);
        Assert.Equal(0, _source.Calls);
        Assert.Contains("contact Anthropic", _model.Disclosure);
        Assert.Contains("only to Anthropic", _model.Disclosure);
    }

    [Fact]
    public void BeforeTheSettingsAreReadThePageIsLoadingAndNoSwitchWorks()
    {
        Assert.Equal(UsagePageMode.Loading, _model.Mode);
        Assert.False(((ICommand)_model.ToggleEnabledCommand).CanExecute(null));
        Assert.False(((ICommand)_model.RefreshCommand).CanExecute(null));
    }

    [Fact]
    public async Task OnWithATokenTheMonitorStartsAndTheLimitsAreShown()
    {
        await Start(enabled: true, token: true);
        await WaitFor(() => _model.ShowLimits);

        Assert.Equal(UsagePageMode.Active, _model.Mode);
        Assert.Equal("67% used", _model.FiveHour.PercentText);
        Assert.Equal(67, _model.FiveHour.Percent);
        Assert.Equal("Resets in 2 h 5 min (" + Now.AddMinutes(125).ToLocalTime().ToString("t", CultureInfo.InvariantCulture) + ")", _model.FiveHour.ResetText);
        Assert.Equal("31% used", _model.Weekly.PercentText);
        Assert.StartsWith("Updated ", _model.StatusText);
        Assert.False(_model.StatusIsProblem);
        Assert.False(_model.ShowTokenEntry);
        Assert.Contains("5-hour limit: 67% used", _model.FiveHour.AutomationName);
    }

    [Fact]
    public async Task OnWithoutATokenThePageAsksForOneAndRequestsNothing()
    {
        await Start(enabled: true, token: false);

        Assert.Equal(UsagePageMode.NeedsToken, _model.Mode);
        Assert.True(_model.ShowTokenEntry);
        Assert.False(_model.ShowLimits);
        Assert.False(_monitor.IsRunning);
        Assert.Equal(0, _source.Calls);
        Assert.Contains("claude setup-token", _model.TokenHelp);
    }

    [Fact]
    public async Task TurningItOnSavesTheChoiceAndStartsTheMonitor()
    {
        await Start(enabled: false, token: true);

        ((ICommand)_model.ToggleEnabledCommand).Execute(null);
        await WaitFor(() => _model.ShowLimits);

        Assert.True(_model.IsEnabled);
        Assert.True(_settings.Current.Usage.Enabled);
        Assert.Equal(1, _settings.Saves);
        Assert.True(_monitor.IsRunning);
    }

    [Fact]
    public async Task TurningItOnKeepsTheOtherSettings()
    {
        await Start(enabled: false, token: true);
        _settings.Current = _settings.Current with { LaunchAtStartup = true, Appearance = new AppearanceSettings { ReduceMotion = true } };

        ((ICommand)_model.ToggleEnabledCommand).Execute(null);
        await WaitFor(() => _model.IsEnabled);

        Assert.True(_settings.Current.LaunchAtStartup);
        Assert.True(_settings.Current.Appearance.ReduceMotion);
    }

    [Fact]
    public async Task TurningItOnWithoutATokenAsksForOneAndStartsNothing()
    {
        await Start(enabled: false, token: false);

        ((ICommand)_model.ToggleEnabledCommand).Execute(null);
        await WaitFor(() => _model.IsEnabled);

        Assert.Equal(UsagePageMode.NeedsToken, _model.Mode);
        Assert.False(_monitor.IsRunning);
        Assert.Equal(0, _source.Calls);
    }

    [Fact]
    public async Task IfTheChoiceCannotBeSavedNothingChangesAndTheUserIsTold()
    {
        await Start(enabled: false, token: true);
        _settings.FailSave = true;

        ((ICommand)_model.ToggleEnabledCommand).Execute(null);
        await WaitFor(() => _model.Notice is not null);

        Assert.False(_model.IsEnabled);
        Assert.Equal(UsagePageMode.Off, _model.Mode);
        Assert.False(_monitor.IsRunning);
        Assert.Equal(0, _source.Calls);
        Assert.Equal("Lunar could not save this choice. Nothing changed.", _model.Notice);
    }

    [Fact]
    public async Task TurningItOffSavesTheChoiceStopsTheMonitorAndKeepsTheToken()
    {
        await Start(enabled: true, token: true);
        await WaitFor(() => _model.ShowLimits);

        ((ICommand)_model.ToggleEnabledCommand).Execute(null);
        await WaitFor(() => _model.IsOff);

        Assert.False(_settings.Current.Usage.Enabled);
        Assert.False(_monitor.IsRunning);
        Assert.False(_model.ShowLimits);
        Assert.True(_model.HasToken);
        Assert.Equal(0, _tokens.Removes);
    }

    [Fact]
    public async Task AnUnreadableSettingsFileMeansOffNotOn()
    {
        _settings.FailLoad = true;
        _tokens.Has = true;

        await _model.InitializeAsync(Ct);

        Assert.Equal(UsagePageMode.Off, _model.Mode);
        Assert.False(_monitor.IsRunning);
        Assert.Equal(0, _source.Calls);
    }

    [Fact]
    public async Task AGoodTokenIsHandedToTheStoreStartsTheMonitorAndIsNeverShown()
    {
        await Start(enabled: true, token: false);

        var saved = await _model.SaveTokenAsync(Secret, Ct);
        await WaitFor(() => _model.ShowLimits);

        Assert.True(saved);
        Assert.Equal(Secret, _tokens.LastSaved);
        Assert.True(_model.HasToken);
        Assert.Equal(UsagePageMode.Active, _model.Mode);
        Assert.False(_model.ShowTokenEntry);
        Assert.Equal("Token saved.", _model.Notice);
        Assert.True(_monitor.IsRunning);
        Assert.DoesNotContain(Secret, EverythingTheModelShows());
    }

    [Fact]
    public async Task ARejectedTokenSaysWhyAndChangesNothingElse()
    {
        await Start(enabled: true, token: false);
        _tokens.OnSave = _ => Result.Failure(new OperationError(ErrorCode.InvalidInput, "That does not look like a Claude token."));

        var saved = await _model.SaveTokenAsync("not a token", Ct);

        Assert.False(saved);
        Assert.Equal("That does not look like a Claude token.", _model.Notice);
        Assert.False(_model.HasToken);
        Assert.Equal(UsagePageMode.NeedsToken, _model.Mode);
        Assert.False(_monitor.IsRunning);
        Assert.Equal(0, _source.Calls);
        Assert.DoesNotContain("not a token", EverythingTheModelShows());
    }

    [Fact]
    public async Task AStoreThatThrowsIsReportedInFixedWordsWithoutTheTokenOrTheExceptionText()
    {
        await Start(enabled: true, token: false);
        _tokens.OnSave = _ => throw new InvalidOperationException("boom " + Secret);

        var saved = await _model.SaveTokenAsync(Secret, Ct);

        Assert.False(saved);
        Assert.Equal("Lunar could not save the token.", _model.Notice);
        Assert.DoesNotContain("boom", EverythingTheModelShows());
        Assert.DoesNotContain(Secret, EverythingTheModelShows());
    }

    [Fact]
    public async Task ATokenIsNotAcceptedWhileThePageIsOff()
    {
        await Start(enabled: false, token: false);

        var saved = await _model.SaveTokenAsync(Secret, Ct);

        Assert.False(saved);
        Assert.Null(_tokens.LastSaved);
    }

    [Fact]
    public async Task RemovingTheTokenForgetsItStopsTheMonitorAndAsksForANewOne()
    {
        await Start(enabled: true, token: true);
        await WaitFor(() => _model.ShowLimits);

        ((ICommand)_model.RemoveTokenCommand).Execute(null);
        await WaitFor(() => !_model.HasToken);

        Assert.Equal(1, _tokens.Removes);
        Assert.Equal(UsagePageMode.NeedsToken, _model.Mode);
        Assert.False(_model.ShowLimits);
        Assert.False(_monitor.IsRunning);
        Assert.Equal("Token removed.", _model.Notice);
    }

    [Fact]
    public async Task ARefusedTokenShowsTheFieldAndTheMonitorsWordsWithAProblemMark()
    {
        _source.Next = () => Result.Failure<UsageSnapshot>(new OperationError(ErrorCode.AccessDenied, "Claude did not accept the token. Paste it again."));
        await Start(enabled: true, token: true);
        await WaitFor(() => _model.StatusIsProblem);

        Assert.Equal("Claude did not accept the token. Paste it again.", _model.StatusText);
        Assert.True(_model.ShowTokenEntry);
        Assert.False(_model.ShowLimits);
    }

    [Fact]
    public async Task WhenTheServiceIsUnavailableTheLastReadingStaysMarkedByItsTime()
    {
        await Start(enabled: true, token: true);
        await WaitFor(() => _model.ShowLimits);
        _source.Next = () => Result.Failure<UsageSnapshot>(new OperationError(ErrorCode.DeviceUnavailable, "Claude cannot be reached.", retryable: true));

        ((ICommand)_model.RefreshCommand).Execute(null);
        await WaitFor(() => _model.StatusIsProblem);

        Assert.True(_model.ShowLimits);
        Assert.Equal("67% used", _model.FiveHour.PercentText);
        Assert.StartsWith("Claude cannot be reached. Showing the updated ", _model.StatusText);
    }

    [Fact]
    public async Task ChangeTokenOpensTheFieldAndCancelClosesIt()
    {
        await Start(enabled: true, token: true);
        await WaitFor(() => _model.ShowLimits);
        Assert.False(_model.ShowTokenEntry);

        ((ICommand)_model.ChangeTokenCommand).Execute(null);
        Assert.True(_model.ShowTokenEntry);
        Assert.True(_model.ShowLimits); // the reading stays while the field is open

        ((ICommand)_model.CancelTokenEntryCommand).Execute(null);
        Assert.False(_model.ShowTokenEntry);
    }

    [Fact]
    public async Task AReadingWithNoFigureForALimitSaysSoInsteadOfInventingOne()
    {
        _source.Next = () => Result.Success(new UsageSnapshot(new UsageWindow(UsageWindowKind.FiveHour, null, null), null, Now));
        await Start(enabled: true, token: true);
        await WaitFor(() => _model.ShowLimits);

        Assert.Equal("No figure reported", _model.FiveHour.PercentText);
        Assert.False(_model.FiveHour.HasFigure);
        Assert.Equal(0, _model.FiveHour.Percent);
        Assert.Equal("Reset time not reported", _model.FiveHour.ResetText);
        Assert.Equal("No figure reported", _model.Weekly.PercentText);
    }

    [Fact]
    public async Task OpeningTheIslandRaisesChangeSoTheResetTextIsRecomputed()
    {
        await Start(enabled: true, token: true);
        await WaitFor(() => _model.ShowLimits);
        var raised = 0;
        _model.PropertyChanged += (_, _) => raised++;

        _model.SetIslandOpen(true);

        Assert.True(raised > 0);
    }

    [Fact]
    public async Task AfterDisposeAMonitorChangeIsIgnored()
    {
        await Start(enabled: true, token: true);
        await WaitFor(() => _model.ShowLimits);
        var raised = 0;
        _model.PropertyChanged += (_, _) => raised++;

        _model.Dispose();
        _monitor.Stop();

        Assert.Equal(0, raised);
    }

    // Every text and flag the page can show, so a test can prove a secret is nowhere in it.
    private string EverythingTheModelShows() => string.Join(
        "|",
        _model.Notice, _model.StatusText, _model.Disclosure, _model.TokenHelp,
        _model.FiveHour.AutomationName, _model.FiveHour.ResetText, _model.Weekly.AutomationName, _model.Weekly.ResetText);
}
