using Lunar.Application.Abstractions.Settings;
using Lunar.Application.Abstractions.System;
using Lunar.Application.Focus;
using Lunar.Application.Settings;
using Lunar.Application.Settings.Models;
using Lunar.Application.Tests.Fakes;
using Lunar.Domain.Focus;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Application.Tests.Focus;

public sealed class PomodoroServiceTests : IDisposable
{
    private const string Off = "off";
    private const string Priority = "priority";
    private const string Alarms = "alarms";

    private readonly FakeFocus _focus = new();
    private readonly FakeMarker _marker = new();
    private readonly ManualTime _time = new();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 11, 9, 23, 50, 0, TimeSpan.FromHours(7)));
    private readonly SettingsService _settings = new(new FakeRepository());
    private readonly PomodoroService _service;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public PomodoroServiceTests() => _service = new PomodoroService(_focus, _marker, _settings, _clock, null, _time);

    public void Dispose()
    {
        _service.Dispose();
        _settings.Dispose();
    }

    private void Advance(TimeSpan by) => _clock.LocalNow += by;

    [Fact]
    public async Task StartingCountsDownFromTheClockAndTurnsDoNotDisturbOn()
    {
        Assert.True((await _service.StartAsync(25, Ct)).IsSuccess);

        Assert.Equal(PomodoroPhase.Running, _service.Current.Phase);
        Assert.Equal(FocusModeStatus.On, _service.Current.FocusStatus);
        Assert.Equal(TimeSpan.FromMinutes(25), _service.Remaining);
        Assert.Equal(Alarms, _focus.Profile);
        Assert.Equal(Off, _marker.Token);

        Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(TimeSpan.FromMinutes(15), _service.Remaining);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(181)]
    [InlineData(-5)]
    public async Task MinutesOutsideTheRangeAreRefused(int minutes)
    {
        Assert.Equal(ErrorCode.InvalidInput, (await _service.StartAsync(minutes, Ct)).Error!.Code);
        Assert.Equal(PomodoroPhase.Idle, _service.Current.Phase);
    }

    [Fact]
    public async Task TheLastMinutesAreRemembered()
    {
        Assert.Equal(FocusTimerRules.DefaultMinutes, _service.LastMinutes);

        await _service.StartAsync(45, Ct);

        Assert.Equal(45, _service.LastMinutes);
    }

    [Fact]
    public async Task ASecondStartChangesNothing()
    {
        await _service.StartAsync(25, Ct);
        Advance(TimeSpan.FromMinutes(1));

        await _service.StartAsync(60, Ct);

        Assert.Equal(TimeSpan.FromMinutes(24), _service.Remaining);
        Assert.Equal(1, _focus.TurnOns);
    }

    [Fact]
    public async Task ALateTickAndASleepCorrectThemselves()
    {
        await _service.StartAsync(25, Ct);

        Advance(TimeSpan.FromHours(2)); // the machine slept
        _time.Fire();
        await _service.ShutdownAsync();

        Assert.Equal(PomodoroPhase.Finished, _service.Current.Phase);
        Assert.Equal(TimeSpan.Zero, _service.Remaining);
    }

    [Fact]
    public async Task ASessionCrossesMidnight()
    {
        await _service.StartAsync(30, Ct);

        Advance(TimeSpan.FromMinutes(20));

        Assert.Equal(TimeSpan.FromMinutes(10), _service.Remaining);
    }

    [Fact]
    public async Task NothingFinishesEarly()
    {
        await _service.StartAsync(25, Ct);
        Advance(TimeSpan.FromMinutes(24));

        _time.Fire();
        await _service.PauseAsync(Ct);

        Assert.Equal(PomodoroPhase.Paused, _service.Current.Phase);
    }

    [Fact]
    public async Task PauseHoldsTheTimeAndKeepsDoNotDisturb()
    {
        await _service.StartAsync(25, Ct);
        Advance(TimeSpan.FromMinutes(5));

        await _service.PauseAsync(Ct);
        Advance(TimeSpan.FromMinutes(30));

        Assert.Equal(TimeSpan.FromMinutes(20), _service.Remaining);
        Assert.Equal(Alarms, _focus.Profile);
        Assert.False(_time.Running);

        await _service.ResumeAsync(Ct);
        Assert.Equal(TimeSpan.FromMinutes(20), _service.Remaining);
        Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(TimeSpan.FromMinutes(19), _service.Remaining);
        Assert.True(_time.Running);
    }

    [Fact]
    public async Task FinishingPutsDoNotDisturbBackAndStopsTicking()
    {
        await _service.StartAsync(1, Ct);
        Advance(TimeSpan.FromMinutes(1));

        _time.Fire();
        await _service.ShutdownAsync();

        Assert.Equal(PomodoroPhase.Finished, _service.Current.Phase);
        Assert.Equal(FocusModeStatus.NotRequested, _service.Current.FocusStatus);
        Assert.Equal(Off, _focus.Profile);
        Assert.Null(_marker.Token);
        Assert.False(_time.Running);
    }

    [Fact]
    public async Task ANewSessionCanFollowAFinishedOne()
    {
        await _service.StartAsync(1, Ct);
        Advance(TimeSpan.FromMinutes(1));
        _time.Fire();
        await _service.ShutdownAsync();

        await _service.StartAsync(5, Ct);

        Assert.Equal(PomodoroPhase.Running, _service.Current.Phase);
    }

    [Fact]
    public async Task StoppingEarlyPutsItBack()
    {
        await _service.StartAsync(25, Ct);

        await _service.StopAsync();

        Assert.Equal(PomodoroPhase.Idle, _service.Current.Phase);
        Assert.Equal(Off, _focus.Profile);
    }

    [Fact]
    public async Task ExitPutsItBack()
    {
        await _service.StartAsync(25, Ct);

        await _service.ShutdownAsync();

        Assert.Equal(Off, _focus.Profile);
        Assert.Null(_marker.Token);
    }

    [Fact]
    public async Task AUserWhoAlreadyHadItOnKeepsIt()
    {
        _focus.Profile = Priority;

        await _service.StartAsync(25, Ct);

        Assert.Equal(FocusModeStatus.AlreadyOn, _service.Current.FocusStatus);
        Assert.Equal(0, _focus.TurnOns);
        Assert.Null(_marker.Token);

        await _service.StopAsync();

        Assert.Equal(Priority, _focus.Profile);
        Assert.Equal(0, _focus.Restores);
    }

    [Fact]
    public async Task AChangeMadeByHandIsRespected()
    {
        await _service.StartAsync(25, Ct);
        _focus.Profile = Off; // the user turned it off during the session

        await _service.StopAsync();

        Assert.Equal(Off, _focus.Profile);
        Assert.Equal(0, _focus.Restores);
        Assert.Null(_marker.Token);
    }

    [Fact]
    public async Task ARefusedTurnOnLeavesTheTimerRunningAndIsTold()
    {
        _focus.TurnOnError = new OperationError(ErrorCode.Unsupported, "no");

        Assert.True((await _service.StartAsync(25, Ct)).IsSuccess);

        Assert.Equal(PomodoroPhase.Running, _service.Current.Phase);
        Assert.Equal(FocusModeStatus.Unavailable, _service.Current.FocusStatus);
        Assert.Equal(ErrorCode.Unsupported, _service.Current.FocusError!.Code);
        Assert.Null(_marker.Token);

        await _service.StopAsync();
        Assert.Equal(0, _focus.Restores);
    }

    [Fact]
    public async Task AFailedRestoreKeepsTheMarkerForTheNextStart()
    {
        await _service.StartAsync(25, Ct);
        _focus.RestoreError = new OperationError(ErrorCode.Timeout, "no");

        await _service.StopAsync();

        Assert.Equal(Off, _marker.Token);
    }

    [Fact]
    public async Task AMarkerLeftByACrashIsRepairedAtTheNextStart()
    {
        _focus.Profile = Alarms;
        _marker.Token = Off;

        Assert.True((await _service.RecoverAsync(Ct)).IsSuccess);

        Assert.Equal(Off, _focus.Profile);
        Assert.Null(_marker.Token);
    }

    [Fact]
    public async Task NothingToRepairTouchesNothing()
    {
        Assert.True((await _service.RecoverAsync(Ct)).IsSuccess);
        Assert.Equal(0, _focus.Restores);
    }

    [Fact]
    public async Task IsSessionActiveFollowsThePhase()
    {
        Assert.False(_service.IsSessionActive);
        await _service.StartAsync(25, Ct);
        Assert.True(_service.IsSessionActive);
        await _service.PauseAsync(Ct);
        Assert.True(_service.IsSessionActive);
        await _service.StopAsync();
        Assert.False(_service.IsSessionActive);
    }

    private sealed class FakeRepository : ISettingsRepository
    {
        public Task<Result<SettingsLoadResult>> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success(new SettingsLoadResult(LunarSettings.Defaults, SettingsRecovery.None)));

        public Task<Result> SaveAsync(LunarSettings settings, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
    }

    private sealed class FakeFocus : IFocusModeControl
    {
        public string Profile { get; set; } = Off;

        public int TurnOns { get; private set; }

        public int Restores { get; private set; }

        public OperationError? TurnOnError { get; set; }

        public OperationError? RestoreError { get; set; }

        public Task<Result<FocusModeState>> ReadAsync(CancellationToken ct) => Task.FromResult(Result.Success(new FocusModeState(Profile != Off, Profile)));

        public Task<Result> TurnOnAsync(CancellationToken ct)
        {
            if (TurnOnError is not null)
                return Task.FromResult(Result.Failure(TurnOnError));
            TurnOns++;
            Profile = Alarms;
            return Task.FromResult(Result.Success());
        }

        public Task<Result> RestoreAsync(string restoreToken, CancellationToken ct)
        {
            if (RestoreError is not null)
                return Task.FromResult(Result.Failure(RestoreError));
            Restores++;
            Profile = restoreToken;
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class FakeMarker : IFocusSessionMarker
    {
        public string? Token { get; set; }

        public Task<Result<string?>> ReadAsync(CancellationToken ct) => Task.FromResult(Result.Success(Token));

        public Task<Result> SaveAsync(string restoreToken, CancellationToken ct)
        {
            Token = restoreToken;
            return Task.FromResult(Result.Success());
        }

        public Task<Result> ClearAsync(CancellationToken ct)
        {
            Token = null;
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        private readonly List<Tick> _timers = [];

        public bool Running => _timers.Any(t => !t.IsDisposed);

        public void Fire()
        {
            foreach (var timer in _timers.Where(t => !t.IsDisposed).ToList())
                timer.Fire();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Tick(callback, state);
            _timers.Add(timer);
            return timer;
        }

        private sealed class Tick(TimerCallback callback, object? state) : ITimer
        {
            public bool IsDisposed { get; private set; }

            public void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() => IsDisposed = true;

            public ValueTask DisposeAsync()
            {
                IsDisposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }
}
