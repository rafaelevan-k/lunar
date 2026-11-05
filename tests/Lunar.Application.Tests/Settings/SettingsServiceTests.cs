using Lunar.Application.Abstractions.Settings;
using Lunar.Application.Settings;
using Lunar.Application.Settings.Models;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Application.Tests.Settings;

public sealed class SettingsServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeRepository : ISettingsRepository
    {
        public LunarSettings Stored { get; set; } = LunarSettings.Defaults;

        public SettingsRecovery Recovery { get; set; } = SettingsRecovery.None;

        public bool FailLoad { get; set; }

        public ErrorCode? FailSaveWith { get; set; }

        public int Loads { get; private set; }

        public int Saves { get; private set; }

        /// <summary>Held by a test to keep a save open while another update queues behind it.</summary>
        public TaskCompletionSource? HoldSave { get; set; }

        public Task<Result<SettingsLoadResult>> LoadAsync(CancellationToken cancellationToken)
        {
            Loads++;
            return Task.FromResult(FailLoad
                ? Result.Failure<SettingsLoadResult>(new OperationError(ErrorCode.IoFailure, "Cannot read."))
                : Result.Success(new SettingsLoadResult(Stored, Recovery)));
        }

        public async Task<Result> SaveAsync(LunarSettings settings, CancellationToken cancellationToken)
        {
            if (HoldSave is { } hold)
                await hold.Task.WaitAsync(cancellationToken);
            if (FailSaveWith is { } code)
                return Result.Failure(new OperationError(code, "Cannot write."));
            Saves++;
            Stored = settings;
            return Result.Success();
        }
    }

    [Fact]
    public async Task BeforeTheFirstLoadTheDefaultsAreInUse()
    {
        using var service = new SettingsService(new FakeRepository());

        Assert.Equal(LunarSettings.Defaults, service.Current);
        Assert.False(service.IsReadOnly);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task LoadReadsTheFileOnceAndKeepsHowItWasObtained()
    {
        var repository = new FakeRepository
        {
            Stored = LunarSettings.Defaults with { Appearance = new AppearanceSettings { Theme = ThemePreference.Light } },
            Recovery = SettingsRecovery.ValuesReset
        };
        using var service = new SettingsService(repository);

        var first = await service.LoadAsync(Ct);
        var second = await service.LoadAsync(Ct);

        Assert.True(first.IsSuccess);
        Assert.Equal(ThemePreference.Light, service.Current.Appearance.Theme);
        Assert.Equal(SettingsRecovery.ValuesReset, service.Recovery);
        Assert.Equal(SettingsRecovery.ValuesReset, second.Value.Recovery);
        Assert.Equal(1, repository.Loads);
    }

    [Fact]
    public async Task AnUnreadableFileKeepsTheDefaultsAndTheNextCallTriesAgain()
    {
        var repository = new FakeRepository { FailLoad = true };
        using var service = new SettingsService(repository);

        var failed = await service.LoadAsync(Ct);
        repository.FailLoad = false;
        repository.Stored = LunarSettings.Defaults with { LaunchAtStartup = true };
        var retried = await service.LoadAsync(Ct);

        Assert.True(failed.IsFailure);
        Assert.True(retried.IsSuccess);
        Assert.True(service.Current.LaunchAtStartup);
        Assert.Equal(2, repository.Loads);
    }

    [Fact]
    public async Task AnUpdateToAnUnreadableFileFailsAndWritesNothing()
    {
        var repository = new FakeRepository { FailLoad = true };
        using var service = new SettingsService(repository);

        var result = await service.UpdateAsync(s => s with { LaunchAtStartup = true }, Ct);

        Assert.True(result.IsFailure);
        Assert.Equal(0, repository.Saves);
        Assert.False(service.Current.LaunchAtStartup);
    }

    [Fact]
    public async Task AnUpdateIsAppliedToTheStoredSettingsAndSavedThenAnnounced()
    {
        var repository = new FakeRepository { Stored = LunarSettings.Defaults with { LaunchAtStartup = true } };
        using var service = new SettingsService(repository);
        SettingsChangedEventArgs? seen = null;
        var savedWhenAnnounced = 0;
        service.Changed += (_, e) =>
        {
            seen = e;
            savedWhenAnnounced = repository.Saves;
        };

        var result = await service.UpdateAsync(s => s with { Appearance = s.Appearance with { ReduceMotion = true } }, Ct);

        Assert.True(result.IsSuccess);
        Assert.True(service.Current.Appearance.ReduceMotion);
        Assert.True(service.Current.LaunchAtStartup); // another value that was already saved is kept
        Assert.Equal(1, savedWhenAnnounced); // announced only after it was on disk
        Assert.NotNull(seen);
        Assert.False(seen.Previous.Appearance.ReduceMotion);
        Assert.True(seen.Current.Appearance.ReduceMotion);
        Assert.Equal(service.Current, repository.Stored);
    }

    [Fact]
    public async Task AFailedSaveChangesNothingAndAnnouncesNothing()
    {
        var repository = new FakeRepository { FailSaveWith = ErrorCode.IoFailure };
        using var service = new SettingsService(repository);
        var announced = 0;
        service.Changed += (_, _) => announced++;

        var result = await service.UpdateAsync(s => s with { LaunchAtStartup = true }, Ct);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCode.IoFailure, result.Error!.Code);
        Assert.False(service.Current.LaunchAtStartup);
        Assert.Equal(0, announced);
    }

    [Fact]
    public async Task AChangeThatChangesNothingWritesAndAnnouncesNothing()
    {
        var repository = new FakeRepository();
        using var service = new SettingsService(repository);
        var announced = 0;
        service.Changed += (_, _) => announced++;

        var result = await service.UpdateAsync(s => s, Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, repository.Saves);
        Assert.Equal(0, announced);
    }

    [Fact]
    public async Task AnInvalidValueIsRepairedBeforeItIsSavedAndShown()
    {
        var repository = new FakeRepository();
        using var service = new SettingsService(repository);

        var result = await service.UpdateAsync(s => s with { Island = s.Island with { TopOffsetDip = 5000, PreferredMonitorId = "  \u0007 " } }, Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(SettingsRules.DefaultTopOffsetDip, service.Current.Island.TopOffsetDip);
        Assert.Null(service.Current.Island.PreferredMonitorId);
        Assert.Equal(service.Current, repository.Stored);
    }

    [Fact]
    public async Task TwoUpdatesAtTheSameTimeBothLandOnTopOfEachOther()
    {
        var repository = new FakeRepository();
        using var service = new SettingsService(repository);

        await Task.WhenAll(
            service.UpdateAsync(s => s with { Appearance = s.Appearance with { ReduceMotion = true } }, Ct),
            service.UpdateAsync(s => s with { Usage = new UsageSettings { Enabled = true } }, Ct),
            service.UpdateAsync(s => s with { LaunchAtStartup = true }, Ct));

        Assert.True(service.Current.Appearance.ReduceMotion);
        Assert.True(service.Current.Usage.Enabled);
        Assert.True(service.Current.LaunchAtStartup);
        Assert.Equal(service.Current, repository.Stored);
        Assert.Equal(3, repository.Saves);
    }

    [Fact]
    public async Task ChangesAreAnnouncedInTheOrderTheyWereMade()
    {
        var repository = new FakeRepository { HoldSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var service = new SettingsService(repository);
        var offsets = new List<int>();
        service.Changed += (_, e) => offsets.Add(e.Current.Island.TopOffsetDip);

        var first = service.UpdateAsync(s => s with { Island = s.Island with { TopOffsetDip = 20 } }, Ct);
        var second = service.UpdateAsync(s => s with { Island = s.Island with { TopOffsetDip = 40 } }, Ct);
        repository.HoldSave.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal([20, 40], offsets);
    }

    [Fact]
    public async Task ASettingsFileFromANewerLunarCannotBeChanged()
    {
        var repository = new FakeRepository { Recovery = SettingsRecovery.NewerVersionIgnored, FailSaveWith = ErrorCode.Unsupported };
        using var service = new SettingsService(repository);
        await service.LoadAsync(Ct);

        var result = await service.UpdateAsync(s => s with { LaunchAtStartup = true }, Ct);

        Assert.True(service.IsReadOnly);
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCode.Unsupported, result.Error!.Code);
        Assert.False(service.Current.LaunchAtStartup);
    }

    [Fact]
    public async Task ResetReturnsEverythingToTheDefaultsAndAnnouncesOnce()
    {
        var repository = new FakeRepository
        {
            Stored = LunarSettings.Defaults with
            {
                LaunchAtStartup = true,
                Usage = new UsageSettings { Enabled = true },
                Conversion = new ConversionSettings { OutputDirectory = @"C:\Out" },
                Island = new IslandSettings { PreferredMonitorId = @"\\.\DISPLAY2", TopOffsetDip = 60 }
            }
        };
        using var service = new SettingsService(repository);
        var announced = 0;
        service.Changed += (_, _) => announced++;

        var result = await service.ResetAsync(Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(LunarSettings.Defaults, service.Current);
        Assert.Equal(LunarSettings.Defaults, repository.Stored);
        Assert.Equal(1, announced);
    }

    [Fact]
    public async Task AnAlreadyCancelledCallIsReportedAsCancelledAndWritesNothing()
    {
        var repository = new FakeRepository();
        using var service = new SettingsService(repository);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var update = await service.UpdateAsync(s => s with { LaunchAtStartup = true }, cancelled.Token);
        var load = await service.LoadAsync(cancelled.Token);

        Assert.True(update.IsCancelled);
        Assert.True(load.IsCancelled);
        Assert.Equal(0, repository.Saves);
    }

    [Fact]
    public async Task AnUpdateWaitingBehindAnotherCanBeCancelledWithoutStoppingThatOther()
    {
        var repository = new FakeRepository { HoldSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var service = new SettingsService(repository);
        using var cancel = new CancellationTokenSource();

        var first = service.UpdateAsync(s => s with { LaunchAtStartup = true }, Ct);
        var second = service.UpdateAsync(s => s with { Usage = new UsageSettings { Enabled = true } }, cancel.Token);
        cancel.Cancel();
        // Waited for BEFORE the first update is let go: a cancelled wait is only noticed on another thread, so if the first update finished
        // meanwhile the waiting one could legitimately win the lock and succeed. Here the first still holds it.
        Assert.True((await second).IsCancelled);
        repository.HoldSave.SetResult();

        Assert.True((await first).IsSuccess);
        Assert.True(service.Current.LaunchAtStartup);
        Assert.False(service.Current.Usage.Enabled);
    }

    [Fact]
    public async Task ANullResultFromTheChangeFallsBackToTheDefaults()
    {
        var repository = new FakeRepository { Stored = LunarSettings.Defaults with { LaunchAtStartup = true } };
        using var service = new SettingsService(repository);

        var result = await service.UpdateAsync(_ => null!, Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(LunarSettings.Defaults, service.Current);
    }
}
