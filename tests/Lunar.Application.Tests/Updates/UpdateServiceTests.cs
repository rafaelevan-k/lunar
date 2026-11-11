using Lunar.Application.Abstractions.Settings;
using Lunar.Application.Abstractions.Updates;
using Lunar.Application.Settings;
using Lunar.Application.Settings.Models;
using Lunar.Application.Updates;
using Lunar.Domain.Shared;
using Lunar.Domain.Updates;
using Xunit;

namespace Lunar.Application.Tests.Updates;

// BE-44, ADR-0025: the update use case over a stand-in feed and installer. The real GitHub feed and installer have their own tests.
public sealed class UpdateServiceTests : IDisposable
{
    private static readonly AppVersion Running = new(1, 0, 5);
    private static readonly ReleaseInfo Newer = Release(1, 0, 6);

    private readonly FakeFeed _feed = new();
    private readonly FakeInstaller _installer = new();
    private readonly ManualTime _time = new();
    private readonly SettingsService _settings = new(new FakeRepository());
    private readonly UpdateService _service;

    public UpdateServiceTests() => _service = new UpdateService(_feed, _installer, _settings, Running, null, _time);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _service.Dispose();
        _settings.Dispose();
    }

    private static ReleaseInfo Release(int major, int minor, int patch) =>
        new(new AppVersion(major, minor, patch), new Uri("https://github.com/rafaelevan-k/lunar/releases/download/v" + major + "." + minor + "." + patch + "/Lunar.msi"), new string('a', 64), 1000);

    [Fact]
    public async Task ANewerReleaseIsOfferedAndNothingIsDownloadedYet()
    {
        _feed.Next = Result.Success<ReleaseInfo?>(Newer);

        Assert.True((await _service.CheckAsync(Ct)).IsSuccess);

        Assert.Equal(UpdatePhase.Available, _service.Current.Phase);
        Assert.Same(Newer, _service.Current.Release);
        Assert.True(_service.Current.ShowPrompt);
        Assert.Equal(0, _installer.Downloads);
    }

    [Theory]
    [InlineData(1, 0, 5)] // the same
    [InlineData(1, 0, 4)] // older
    public async Task TheSameOrAnOlderReleaseIsNotOffered(int major, int minor, int patch)
    {
        _feed.Next = Result.Success<ReleaseInfo?>(Release(major, minor, patch));

        await _service.CheckAsync(Ct);

        Assert.Equal(UpdatePhase.Idle, _service.Current.Phase);
        Assert.False(_service.Current.ShowPrompt);
    }

    [Fact]
    public async Task NoUsableReleaseIsNothingNew()
    {
        _feed.Next = Result.Success<ReleaseInfo?>(null);
        await _service.CheckAsync(Ct);
        Assert.Equal(UpdatePhase.Idle, _service.Current.Phase);
    }

    [Fact]
    public async Task AFailedCheckIsQuietAndChangesNothingTheUserSees()
    {
        _feed.Next = Result.Success<ReleaseInfo?>(Newer);
        await _service.CheckAsync(Ct);
        _feed.Next = Result.Failure<ReleaseInfo?>(new OperationError(ErrorCode.DeviceUnavailable, "GitHub could not be reached.", retryable: true));

        Assert.True((await _service.CheckAsync(Ct)).IsSuccess);

        Assert.Equal(UpdatePhase.Available, _service.Current.Phase);
        Assert.Null(_service.Current.Error);
    }

    [Fact]
    public async Task WithTheSwitchOffNothingIsRequestedAtAll()
    {
        await _settings.UpdateAsync(s => s with { Updates = new UpdateSettings { Check = false } }, Ct);
        _feed.Next = Result.Success<ReleaseInfo?>(Newer);

        await _service.CheckAsync(Ct);
        _service.Start();
        _time.Fire();
        await _service.PendingCheck;

        Assert.Equal(0, _feed.Calls);
        Assert.Equal(UpdatePhase.Idle, _service.Current.Phase);
    }

    [Fact]
    public async Task TurningTheSwitchOffTakesTheOfferAway()
    {
        _feed.Next = Result.Success<ReleaseInfo?>(Newer);
        await _service.CheckAsync(Ct);

        await _settings.UpdateAsync(s => s with { Updates = new UpdateSettings { Check = false } }, Ct);

        Assert.Equal(UpdatePhase.Idle, _service.Current.Phase);
        Assert.False(_service.Current.ShowPrompt);
    }

    [Fact]
    public async Task TheScheduleWaitsAfterStartThenChecksDailyAndCleansUpFirst()
    {
        _feed.Next = Result.Success<ReleaseInfo?>(Newer);

        _service.Start();

        Assert.Equal(1, _installer.Cleanups);
        Assert.Equal(UpdateRules.FirstCheckDelay, _time.DueTime);
        Assert.Equal(TimeSpan.FromHours(24), _time.Period);
        Assert.Equal(0, _feed.Calls); // nothing at the moment of start

        _time.Fire();
        await _service.PendingCheck;

        Assert.Equal(1, _feed.Calls);
        Assert.Equal(UpdatePhase.Available, _service.Current.Phase);
    }

    [Fact]
    public async Task LaterHidesThePromptUntilTheNextCheckFindsTheReleaseAgain()
    {
        _feed.Next = Result.Success<ReleaseInfo?>(Newer);
        await _service.CheckAsync(Ct);

        _service.Postpone();

        Assert.Equal(UpdatePhase.Available, _service.Current.Phase);
        Assert.False(_service.Current.ShowPrompt);

        await _service.CheckAsync(Ct); // the next daily check

        Assert.True(_service.Current.ShowPrompt);
    }

    [Fact]
    public async Task InstallNowDownloadsChecksAndHandsOverToWindowsInstaller()
    {
        _feed.Next = Result.Success<ReleaseInfo?>(Newer);
        await _service.CheckAsync(Ct);
        var seen = new List<UpdateSnapshot>();
        using var subscription = _service.Changes.Subscribe(new Collect(seen));

        Assert.True((await _service.InstallAsync(Ct)).IsSuccess);

        Assert.Equal(UpdatePhase.Installing, _service.Current.Phase);
        Assert.Same(Newer, _installer.Downloaded);
        Assert.Equal(1, _installer.Starts);
        Assert.Contains(seen, s => s.Phase == UpdatePhase.Downloading && s.Progress is > 0 and < 1);
        Assert.Equal(seen.Select(s => s.Progress).Where(p => p > 0).Distinct().Count(), seen.Count(s => s.Phase == UpdatePhase.Downloading && s.Progress > 0)); // whole percents, no repeats
    }

    [Fact]
    public async Task AFailedDownloadIsSaidAndNothingIsStarted()
    {
        _feed.Next = Result.Success<ReleaseInfo?>(Newer);
        await _service.CheckAsync(Ct);
        _installer.DownloadResult = Result.Failure<VerifiedInstaller>(new OperationError(ErrorCode.InvalidInput, "The downloaded file did not match the release, so it was deleted and nothing was installed.", retryable: true));

        var result = await _service.InstallAsync(Ct);

        Assert.True(result.IsFailure);
        Assert.Equal(UpdatePhase.Failed, _service.Current.Phase);
        Assert.Equal(ErrorCode.InvalidInput, _service.Current.Error!.Code);
        Assert.True(_service.Current.ShowPrompt); // with Try again
        Assert.Equal(0, _installer.Starts);

        _installer.DownloadResult = null;
        Assert.True((await _service.InstallAsync(Ct)).IsSuccess); // Try again
        Assert.Equal(1, _installer.Starts);
    }

    [Fact]
    public async Task AnInstallerThatCannotStartIsAFailure()
    {
        _feed.Next = Result.Success<ReleaseInfo?>(Newer);
        await _service.CheckAsync(Ct);
        _installer.StartResult = Result.Failure(new OperationError(ErrorCode.DependencyMissing, "Windows Installer could not be started.", retryable: true));

        Assert.True((await _service.InstallAsync(Ct)).IsFailure);
        Assert.Equal(UpdatePhase.Failed, _service.Current.Phase);
    }

    [Fact]
    public async Task CancellingTheDownloadGoesBackToTheOffer()
    {
        _feed.Next = Result.Success<ReleaseInfo?>(Newer);
        await _service.CheckAsync(Ct);
        _installer.DownloadResult = Result.Cancelled<VerifiedInstaller>();

        Assert.True((await _service.InstallAsync(Ct)).IsCancelled);

        Assert.Equal(UpdatePhase.Available, _service.Current.Phase);
        Assert.True(_service.Current.ShowPrompt);
        Assert.Equal(0, _installer.Starts);
    }

    [Fact]
    public async Task InstallWithoutANewerReleaseIsRefused()
    {
        var result = await _service.InstallAsync(Ct);
        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.Equal(0, _installer.Downloads);
    }

    [Fact]
    public async Task NoCheckReplacesAnInstallUnderWay()
    {
        _feed.Next = Result.Success<ReleaseInfo?>(Newer);
        await _service.CheckAsync(Ct);
        await _service.InstallAsync(Ct);
        _feed.Next = Result.Success<ReleaseInfo?>(null);

        await _service.CheckAsync(Ct);

        Assert.Equal(UpdatePhase.Installing, _service.Current.Phase);
    }

    [Fact]
    public void TheSwitchIsOnByDefault() => Assert.True(LunarSettings.Defaults.Updates.Check);

    private sealed class Collect(List<UpdateSnapshot> into) : IObserver<UpdateSnapshot>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(UpdateSnapshot value) => into.Add(value);
    }

    private sealed class FakeFeed : IUpdateFeed
    {
        public Result<ReleaseInfo?> Next { get; set; } = Result.Success<ReleaseInfo?>(null);

        public int Calls { get; private set; }

        public Task<Result<ReleaseInfo?>> GetLatestAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Next);
        }
    }

    private sealed class FakeInstaller : IUpdateInstaller
    {
        public Result<VerifiedInstaller>? DownloadResult { get; set; }

        public Result? StartResult { get; set; }

        public int Downloads { get; private set; }

        public int Starts { get; private set; }

        public int Cleanups { get; private set; }

        public ReleaseInfo? Downloaded { get; private set; }

        public Task<Result<VerifiedInstaller>> DownloadAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken ct)
        {
            Downloads++;
            Downloaded = release;
            foreach (var step in new[] { 0.001, 0.004, 0.25, 0.251, 0.5, 1.0 })
                progress?.Report(step);
            return Task.FromResult(DownloadResult ?? Result.Success(new VerifiedInstaller(@"C:\Lunar\Updates\x\Lunar.msi", release.Version)));
        }

        public Result StartInstall(VerifiedInstaller installer)
        {
            if (StartResult is { IsFailure: true })
                return StartResult;
            Starts++;
            return Result.Success();
        }

        public void RemoveLeftovers() => Cleanups++;
    }

    private sealed class FakeRepository : ISettingsRepository
    {
        public Task<Result<SettingsLoadResult>> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success(new SettingsLoadResult(LunarSettings.Defaults, SettingsRecovery.None)));

        public Task<Result> SaveAsync(LunarSettings settings, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
    }

    private sealed class ManualTime : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;

        public TimeSpan DueTime { get; private set; }

        public TimeSpan Period { get; private set; }

        public void Fire() => _callback?.Invoke(_state);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            DueTime = dueTime;
            Period = period;
            return new Stub();
        }

        private sealed class Stub : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
