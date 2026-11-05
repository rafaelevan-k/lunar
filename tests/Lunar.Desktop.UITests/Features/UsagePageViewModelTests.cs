using System.Globalization;
using System.Windows.Input;
using Lunar.Application.Abstractions.Settings;
using Lunar.Application.Abstractions.Usage;
using Lunar.Application.Settings;
using Lunar.Application.Settings.Models;
using Lunar.Application.Usage;
using Lunar.Application.Usage.Models;
using Lunar.Desktop.Features.Usage;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Desktop.UITests.Features;

// FE-27, FE-29: the Usage page's view model against fakes of the two ports it uses. The monitor is the real one with a wait that never ends, so
// nothing is requested after the first reading; the real service, the real Claude Code sign-in and the real island are manual checks (NOT_RUN here).
public sealed class UsagePageViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    internal const string NotSignedIn = "Claude Code is not signed in on this PC. Open a terminal, run \"claude\" and sign in, then press Refresh.";

    internal sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal sealed class Source : IUsageSource
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Func<Result<UsageSnapshot>> Next { get; set; } = () => Result.Success(Reading(0.67, 0.31));

        /// <summary>When set, a request waits for it, so a test can look at the page while Claude is being asked.</summary>
        public TaskCompletionSource? Block { get; set; }

        public async Task<Result<UsageSnapshot>> FetchAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            if (Block is { } block)
                await block.Task.WaitAsync(ct);
            return Next();
        }
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
    private readonly Settings _settings = new();
    private readonly SettingsService _service;
    private readonly UsageMonitor _monitor;
    private readonly UsagePageViewModel _model;

    public UsagePageViewModelTests()
    {
        _monitor = new UsageMonitor(_source, new UsageMonitorOptions { Delay = (_, ct) => Task.Delay(Timeout.Infinite, ct), RefreshGap = TimeSpan.Zero });
        _service = new SettingsService(_settings);
        _model = new UsagePageViewModel(_monitor, _service, work => work(), log: null, new FixedTime(), CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        _model.Dispose();
        _service.Dispose();
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

    private async Task Start(bool enabled)
    {
        _settings.Current = LunarSettings.Defaults with { Usage = new UsageSettings { Enabled = enabled } };
        await _model.InitializeAsync(Ct);
    }

    private static Result<UsageSnapshot> Refused(string message) =>
        Result.Failure<UsageSnapshot>(new OperationError(ErrorCode.AccessDenied, message));

    [Fact]
    public async Task ByDefaultThePageIsOffAndNothingIsRequested()
    {
        await Start(enabled: false);

        Assert.Equal(UsagePageMode.Off, _model.Mode);
        Assert.True(_model.IsOff);
        Assert.False(_model.IsEnabled);
        Assert.False(_model.ShowLimits);
        Assert.False(_model.ShowLoginProblem);
        Assert.False(_monitor.IsRunning);
        Assert.Equal(0, _source.Calls);
        Assert.Contains("Claude Code sign-in", _model.Disclosure);
        Assert.Contains("read-only", _model.Disclosure);
        Assert.Contains("Anthropic", _model.Disclosure);
        Assert.True(_model.Disclosure.Length < 160, "short enough to read at a glance");
    }

    [Fact]
    public void BeforeTheSettingsAreReadThePageIsLoadingAndNoSwitchWorks()
    {
        Assert.Equal(UsagePageMode.Loading, _model.Mode);
        Assert.False(((ICommand)_model.ToggleEnabledCommand).CanExecute(null));
        Assert.False(((ICommand)_model.RefreshCommand).CanExecute(null));
    }

    [Fact]
    public async Task OnTheMonitorStartsAndTheLimitsAreShown()
    {
        await Start(enabled: true);
        await WaitFor(() => _model.ShowLimits);

        Assert.Equal(UsagePageMode.Active, _model.Mode);
        Assert.Equal("67% used", _model.FiveHour.PercentText);
        Assert.Equal(67, _model.FiveHour.Percent);
        Assert.Equal("Resets in 2 h 5 min (" + Now.AddMinutes(125).ToLocalTime().ToString("t", CultureInfo.InvariantCulture) + ")", _model.FiveHour.ResetText);
        Assert.Equal("31% used", _model.Weekly.PercentText);
        Assert.StartsWith("Updated ", _model.StatusText);
        Assert.False(_model.StatusIsProblem);
        Assert.False(_model.ShowLoginProblem);
        Assert.True(_model.ShowStatusLine);
        Assert.Contains("5-hour limit: 67% used", _model.FiveHour.AutomationName);
    }

    [Fact]
    public async Task TurningItOnSavesTheChoiceAndStartsTheMonitor()
    {
        await Start(enabled: false);

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
        await Start(enabled: false);
        // Another writer (the Settings window) changed other things first; turning Usage on goes on top of them.
        Assert.True((await _service.UpdateAsync(s => s with { LaunchAtStartup = true, Appearance = new AppearanceSettings { ReduceMotion = true } }, Ct)).IsSuccess);

        ((ICommand)_model.ToggleEnabledCommand).Execute(null);
        await WaitFor(() => _model.IsEnabled);

        Assert.True(_settings.Current.LaunchAtStartup);
        Assert.True(_settings.Current.Appearance.ReduceMotion);
    }

    [Fact]
    public async Task AResetMadeInTheSettingsWindowTurnsThePageOffAndStopsTheMonitor()
    {
        await Start(enabled: true);
        await WaitFor(() => _model.ShowLimits);

        Assert.True((await _service.ResetAsync(Ct)).IsSuccess);

        Assert.True(_model.IsOff);
        Assert.False(_model.IsEnabled);
        Assert.False(_monitor.IsRunning);
    }

    [Fact]
    public async Task APageThatIsAlreadyOffIgnoresAnUnrelatedChange()
    {
        await Start(enabled: false);
        var raised = 0;
        _model.PropertyChanged += (_, _) => raised++;

        Assert.True((await _service.UpdateAsync(s => s with { LaunchAtStartup = true }, Ct)).IsSuccess);

        Assert.Equal(0, raised);
        Assert.True(_model.IsOff);
    }

    [Fact]
    public async Task IfTheChoiceCannotBeSavedNothingChangesAndTheUserIsTold()
    {
        await Start(enabled: false);
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
    public async Task TurningItOffSavesTheChoiceAndStopsTheMonitor()
    {
        await Start(enabled: true);
        await WaitFor(() => _model.ShowLimits);

        ((ICommand)_model.ToggleEnabledCommand).Execute(null);
        await WaitFor(() => _model.IsOff);

        Assert.False(_settings.Current.Usage.Enabled);
        Assert.False(_monitor.IsRunning);
        Assert.False(_model.ShowLimits);
    }

    [Fact]
    public async Task AnUnreadableSettingsFileMeansOffNotOn()
    {
        _settings.FailLoad = true;

        await _model.InitializeAsync(Ct);

        Assert.Equal(UsagePageMode.Off, _model.Mode);
        Assert.False(_monitor.IsRunning);
        Assert.Equal(0, _source.Calls);
    }

    [Fact]
    public async Task NoClaudeCodeSignInIsSaidInItsOwnCardAndNotTwice()
    {
        _source.Next = () => Refused(NotSignedIn);
        await Start(enabled: true);
        await WaitFor(() => _model.ShowLoginProblem);

        Assert.Equal(NotSignedIn, _model.LoginProblemText);
        Assert.True(_model.StatusIsProblem);
        Assert.False(_model.ShowLimits);
        Assert.False(_model.ShowStatusLine); // the card says it; the slim line would repeat it
    }

    [Fact]
    public async Task AfterSigningInToClaudeCodeRefreshShowsTheLimits()
    {
        _source.Next = () => Refused(NotSignedIn);
        await Start(enabled: true);
        await WaitFor(() => _model.ShowLoginProblem);

        _source.Next = () => Result.Success(Reading(0.5, 0.2));
        ((ICommand)_model.RefreshCommand).Execute(null);
        await WaitFor(() => _model.ShowLimits);

        Assert.False(_model.ShowLoginProblem);
        Assert.Equal("50% used", _model.FiveHour.PercentText);
        Assert.Null(_model.LoginProblemText);
    }

    [Fact]
    public async Task OpeningTheIslandAfterSigningInRecoversWithoutPressingRefresh()
    {
        // The monitor tries again when the island opens (ADR-0015); here the real monitor does it with a zero gap.
        _source.Next = () => Refused(NotSignedIn);
        await Start(enabled: true);
        await WaitFor(() => _model.ShowLoginProblem);

        _source.Next = () => Result.Success(Reading(0.4, 0.1));
        _model.SetIslandOpen(true);
        await WaitFor(() => _model.ShowLimits);

        Assert.Equal("40% used", _model.FiveHour.PercentText);
    }

    [Fact]
    public async Task WhileClaudeIsBeingAskedThePageSaysSo()
    {
        _source.Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Start(enabled: true);

        await WaitFor(() => _model.StatusText == "Reading your limits…");
        Assert.False(_model.StatusIsProblem);
        Assert.False(_model.ShowLimits);

        _source.Block.SetResult();
        await WaitFor(() => _model.ShowLimits);
    }

    [Fact]
    public async Task WhenTheServiceIsUnavailableTheLastReadingStaysMarkedByItsTime()
    {
        await Start(enabled: true);
        await WaitFor(() => _model.ShowLimits);
        _source.Next = () => Result.Failure<UsageSnapshot>(new OperationError(ErrorCode.DeviceUnavailable, "Claude cannot be reached.", retryable: true));

        ((ICommand)_model.RefreshCommand).Execute(null);
        await WaitFor(() => _model.StatusIsProblem);

        Assert.True(_model.ShowLimits);
        Assert.Equal("67% used", _model.FiveHour.PercentText);
        Assert.StartsWith("Claude cannot be reached. Showing the updated ", _model.StatusText);
        Assert.False(_model.ShowLoginProblem); // a network problem is not a sign-in problem
        Assert.True(_model.ShowStatusLine);
    }

    [Fact]
    public async Task AReadingWithNoFigureForALimitSaysSoInsteadOfInventingOne()
    {
        _source.Next = () => Result.Success(new UsageSnapshot(new UsageWindow(UsageWindowKind.FiveHour, null, null), null, Now));
        await Start(enabled: true);
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
        await Start(enabled: true);
        await WaitFor(() => _model.ShowLimits);
        var raised = 0;
        _model.PropertyChanged += (_, _) => raised++;

        _model.SetIslandOpen(true);

        Assert.True(raised > 0);
    }

    [Fact]
    public async Task AfterDisposeAMonitorChangeIsIgnored()
    {
        await Start(enabled: true);
        await WaitFor(() => _model.ShowLimits);
        var raised = 0;
        _model.PropertyChanged += (_, _) => raised++;

        _model.Dispose();
        _monitor.Stop();

        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task ThePageNeverHoldsOrShowsAnyTokenBecauseItNeverSeesOne()
    {
        // The view model has no way to receive a credential: its constructor takes only the monitor and the settings.
        var constructors = typeof(UsagePageViewModel).GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.All(constructors, c => Assert.DoesNotContain(c.GetParameters(), p => p.ParameterType == typeof(IUsageCredentialSource) || p.ParameterType.Name.Contains("Token", StringComparison.Ordinal)));
        await Start(enabled: true);
        await WaitFor(() => _model.ShowLimits);
        Assert.DoesNotContain("sk-ant", string.Join("|", _model.StatusText, _model.Notice, _model.Disclosure, _model.FiveHour.AutomationName, _model.Weekly.AutomationName), StringComparison.Ordinal);
    }
}
