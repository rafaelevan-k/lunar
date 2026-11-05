using System.Threading.Channels;
using Lunar.Application.Abstractions.Usage;
using Lunar.Application.Usage;
using Lunar.Application.Usage.Models;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Application.Tests.Usage;

// BE-32: the usage monitor. A scripted source, a clock that only moves when a test moves it, and a wait the test releases by hand, so every
// schedule and every announcement is checked without real time passing.
public sealed class UsageMonitorTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = T0;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Source : IUsageSource
    {
        private readonly Queue<Func<Result<UsageSnapshot>>> _script = new();
        private Func<Result<UsageSnapshot>>? _last;
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public TaskCompletionSource? Block { get; set; }

        public void Then(Result<UsageSnapshot> result) => _script.Enqueue(() => result);

        public void ThenThrow(Exception exception) => _script.Enqueue(() => throw exception);

        public async Task<Result<UsageSnapshot>> FetchAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            if (Block is { } block)
                await block.Task.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (_script.Count > 0)
                _last = _script.Dequeue();
            return _last!();
        }
    }

    private readonly FakeTime _time = new();
    private readonly Source _source = new();
    private readonly Channel<Wait> _waits = Channel.CreateUnbounded<Wait>();
    private readonly List<UsageState> _states = [];
    private readonly List<UsageLimitReset> _resets = [];
    private readonly UsageMonitor _monitor;

    // One wait the loop is sitting in: how long it asked for, and the way to let it pass.
    private sealed record Wait(TimeSpan Time, TaskCompletionSource Release);

    public UsageMonitorTests()
    {
        var options = new UsageMonitorOptions
        {
            Delay = async (time, ct) =>
            {
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await _waits.Writer.WriteAsync(new Wait(time, release), CancellationToken.None);
                await using var registration = ct.Register(() => release.TrySetCanceled(ct));
                await release.Task;
            }
        };
        _monitor = new UsageMonitor(_source, options, _time);
        _monitor.Changes.Subscribe(new Collect<UsageState>(_states));
        _monitor.Resets.Subscribe(new Collect<UsageLimitReset>(_resets));
    }

    public void Dispose() => _monitor.Dispose();

    private sealed class Collect<T>(List<T> into) : IObserver<T>
    {
        public void OnNext(T value)
        {
            lock (into)
                into.Add(value);
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static UsageSnapshot Snap(DateTimeOffset at, double? five, DateTimeOffset? fiveReset, double? week = null, DateTimeOffset? weekReset = null) =>
        new(
            five is null && fiveReset is null ? null : new UsageWindow(UsageWindowKind.FiveHour, five, fiveReset),
            week is null && weekReset is null ? null : new UsageWindow(UsageWindowKind.Weekly, week, weekReset),
            at);

    private static Result<UsageSnapshot> Ok(UsageSnapshot snapshot) => Result.Success(snapshot);

    private static Result<UsageSnapshot> Fail(ErrorCode code, bool retryable = true) =>
        Result.Failure<UsageSnapshot>(new OperationError(code, "A plain sentence.", retryable));

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for the monitor");
            await Task.Delay(2, Ct);
        }
    }

    // The next wait the loop asks for (it asks only when it has nothing to do until then).
    private async Task<Wait> NextWait()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return await _waits.Reader.ReadAsync(timeout.Token);
    }

    // Lets the wait pass: the clock moves by it and the loop looks again. Returns the wait that follows that look.
    private async Task<Wait> Pass(Wait wait, Result<UsageSnapshot>? next = null)
    {
        if (next is { } result)
            _source.Then(result);
        if (wait.Time > TimeSpan.Zero)
            _time.Advance(wait.Time);
        wait.Release.TrySetResult();
        return await NextWait();
    }

    private async Task<Wait> StartWith(Result<UsageSnapshot> first)
    {
        _source.Then(first);
        _monitor.Start();
        return await NextWait();
    }

    // ---- start, stop, pace ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task NothingIsRequestedUntilItIsStarted()
    {
        Assert.Equal(UsageStatus.Off, _monitor.Current.Status);
        Assert.False(_monitor.IsRunning);

        await _monitor.RefreshAsync(Ct);
        _monitor.SetIslandOpen(true);
        await Task.Delay(30, Ct);

        Assert.Equal(0, _source.Calls);
        Assert.Equal(UsageStatus.Off, _monitor.Current.Status);
    }

    [Fact]
    public async Task StartingLooksAtOnceThenWaitsTheClosedInterval()
    {
        var reading = Snap(T0, 0.42, T0.AddHours(3));

        var wait = await StartWith(Ok(reading));

        Assert.Equal(1, _source.Calls);
        Assert.Equal(TimeSpan.FromMinutes(15), wait.Time);
        Assert.Same(reading, _monitor.Current.Snapshot);
        lock (_states)
            Assert.Equal([UsageStatus.Loading, UsageStatus.Ready], _states.Select(s => s.Status));
    }

    [Fact]
    public async Task WithTheIslandOpenItLooksEveryFiveMinutes()
    {
        _monitor.SetIslandOpen(true);

        var wait = await StartWith(Ok(Snap(T0, 0.1, T0.AddHours(3))));

        Assert.Equal(TimeSpan.FromMinutes(5), wait.Time);
    }

    [Fact]
    public async Task OpeningTheIslandSpeedsUpTheNextLookWithoutLookingBeforeItsTime()
    {
        var closed = await StartWith(Ok(Snap(T0, 0.1, T0.AddHours(3))));
        Assert.Equal(TimeSpan.FromMinutes(15), closed.Time);

        _time.Advance(TimeSpan.FromMinutes(4));
        _monitor.SetIslandOpen(true);
        var open = await NextWait();

        Assert.Equal(TimeSpan.FromMinutes(1), open.Time); // five minutes after the last look, which was four minutes ago
        Assert.Equal(1, _source.Calls);
    }

    [Fact]
    public async Task OpeningTheIslandWhenALookIsOverdueLooksAtOnce()
    {
        await StartWith(Ok(Snap(T0, 0.1, T0.AddHours(3))));

        _time.Advance(TimeSpan.FromMinutes(8));
        _monitor.SetIslandOpen(true);
        var next = await NextWait();

        Assert.Equal(2, _source.Calls);
        Assert.Equal(TimeSpan.FromMinutes(5), next.Time);
    }

    [Fact]
    public async Task ClosingTheIslandSlowsTheNextLookDown()
    {
        _monitor.SetIslandOpen(true);
        await StartWith(Ok(Snap(T0, 0.1, T0.AddHours(3))));

        _monitor.SetIslandOpen(false);
        var next = await NextWait();

        Assert.Equal(TimeSpan.FromMinutes(15), next.Time);
        Assert.Equal(1, _source.Calls);
    }

    [Fact]
    public async Task StopReturnsToOffAndAnAnswerThatArrivesLaterIsDropped()
    {
        _source.Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _source.Then(Ok(Snap(T0, 0.9, T0.AddHours(1))));
        _monitor.Start();
        await Until(() => _source.Calls == 1);

        _monitor.Stop();
        _source.Block.TrySetResult();
        await Task.Delay(40, Ct);

        Assert.Equal(UsageState.Off, _monitor.Current);
        Assert.False(_monitor.IsRunning);
        lock (_states)
            Assert.DoesNotContain(_states, s => s.Status == UsageStatus.Ready);
        Assert.Equal(1, _source.Calls);
    }

    [Fact]
    public async Task StartingAgainAfterStopForgetsTheEarlierReadingSoNoFalseResetIsAnnounced()
    {
        await StartWith(Ok(Snap(T0, 0.9, T0.AddHours(1))));
        _monitor.Stop();
        _time.Advance(TimeSpan.FromHours(2));

        _source.Then(Ok(Snap(_time.GetUtcNow(), 0.05, _time.GetUtcNow().AddHours(5))));
        _monitor.Start();
        await NextWait();

        Assert.Empty(_resets);
        Assert.Equal(UsageStatus.Ready, _monitor.Current.Status);
    }

    // ---- failures ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task FailuresThatMayPassBackOffAndAGoodReadingStartsOverKeepingTheOldNumbersMeanwhile()
    {
        var good = Snap(T0, 0.3, T0.AddHours(3));
        var w0 = await StartWith(Ok(good));
        Assert.Equal(TimeSpan.FromMinutes(15), w0.Time);

        var w1 = await Pass(w0, Fail(ErrorCode.DeviceUnavailable));
        Assert.Equal(UsageStatus.Unavailable, _monitor.Current.Status);
        Assert.Same(good, _monitor.Current.Snapshot); // the last good numbers stay, with their own time
        Assert.Equal("A plain sentence.", _monitor.Current.Message);

        var w2 = await Pass(w1);
        var w3 = await Pass(w2);
        Assert.Equal([TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(60)], new[] { w1.Time, w2.Time, w3.Time });

        var w4 = await Pass(w3, Ok(Snap(_time.GetUtcNow(), 0.35, T0.AddHours(3))));
        Assert.Equal(UsageStatus.Ready, _monitor.Current.Status);
        Assert.Equal(TimeSpan.FromMinutes(15), w4.Time);
    }

    [Fact]
    public async Task WithTheIslandOpenTheBackOffStartsFromFiveMinutes()
    {
        _monitor.SetIslandOpen(true);
        var w0 = await StartWith(Ok(Snap(T0, 0.3, T0.AddHours(9))));
        var w1 = await Pass(w0, Fail(ErrorCode.Timeout));
        var w2 = await Pass(w1);
        var w3 = await Pass(w2);
        var w4 = await Pass(w3);

        Assert.Equal([5, 10, 20, 40, 60], new[] { w0, w1, w2, w3, w4 }.Select(w => (int)w.Time.TotalMinutes));
    }

    [Fact]
    public async Task AnUnreadableReplyIsShownAsUnreadableAndBacksOffToo()
    {
        var wait = await StartWith(Fail(ErrorCode.Unexpected, retryable: false));

        Assert.Equal(UsageStatus.Unreadable, _monitor.Current.Status);
        Assert.Null(_monitor.Current.Snapshot);
        Assert.Equal(TimeSpan.FromMinutes(30), wait.Time);
    }

    [Fact]
    public async Task ASourceThatThrowsDoesNotKillTheLoopAndLeaksNoExceptionText()
    {
        _source.ThenThrow(new InvalidOperationException("secret-detail sk-ant-oat01-LEAK"));
        _monitor.Start();

        var wait = await NextWait();

        Assert.Equal(UsageStatus.Unreadable, _monitor.Current.Status);
        Assert.DoesNotContain("secret", _monitor.Current.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LEAK", _monitor.Current.Message!, StringComparison.Ordinal);
        Assert.True(wait.Time > TimeSpan.Zero);
        Assert.True(_monitor.IsRunning);
    }

    [Fact]
    public async Task ARefusalPausesPollingUntilTheUserRefreshesAndANewTokenWorks()
    {
        var w0 = await StartWith(Ok(Snap(T0, 0.8, T0.AddHours(1))));

        var paused = await Pass(w0, Fail(ErrorCode.AccessDenied, retryable: false));

        Assert.Equal(Timeout.InfiniteTimeSpan, paused.Time); // nothing more is asked of the service
        Assert.Equal(UsageStatus.SignedOut, _monitor.Current.Status);
        Assert.Null(_monitor.Current.Snapshot);
        Assert.Equal(2, _source.Calls);

        _source.Then(Ok(Snap(_time.GetUtcNow(), 0.01, _time.GetUtcNow().AddHours(5))));
        var after = await _monitor.RefreshAsync(Ct);

        Assert.Equal(UsageStatus.Ready, after.Status);
        Assert.Equal(3, _source.Calls);
        Assert.Empty(_resets); // possibly another account: the old reading is not compared
    }

    [Fact]
    public async Task ARefusedRefreshStaysPausedAndAsksOnlyWhenTheUserAsksAgain()
    {
        var paused = await StartWith(Fail(ErrorCode.AccessDenied, retryable: false));
        Assert.Equal(Timeout.InfiniteTimeSpan, paused.Time);

        await _monitor.RefreshAsync(Ct);
        await _monitor.RefreshAsync(Ct);
        await Task.Delay(30, Ct);

        Assert.Equal(3, _source.Calls); // one for the start and one per user request, none by itself
        Assert.Equal(UsageStatus.SignedOut, _monitor.Current.Status);
    }

    [Fact]
    public async Task APausedMonitorTriesAgainWhenTheIslandOpensAndTheLastTryIsOldEnough()
    {
        // ADR-0015: the user signs in to Claude Code meanwhile; opening the island is enough to recover.
        var paused = await StartWith(Fail(ErrorCode.AccessDenied, retryable: false));
        Assert.Equal(Timeout.InfiniteTimeSpan, paused.Time);
        Assert.Equal(1, _source.Calls);

        _time.Advance(TimeSpan.FromSeconds(45));
        _source.Then(Ok(Snap(_time.GetUtcNow(), 0.2, _time.GetUtcNow().AddHours(4))));
        _monitor.SetIslandOpen(true);
        await Until(() => _monitor.Current.Status == UsageStatus.Ready);

        Assert.Equal(2, _source.Calls);
    }

    [Fact]
    public async Task APausedMonitorDoesNotTryAgainWhenTheIslandOpensTooSoonAfterTheLastTry()
    {
        var paused = await StartWith(Fail(ErrorCode.AccessDenied, retryable: false));
        Assert.Equal(Timeout.InfiniteTimeSpan, paused.Time);

        _time.Advance(TimeSpan.FromSeconds(10)); // under the 30 second gap
        _monitor.SetIslandOpen(true);
        _monitor.SetIslandOpen(false);
        _monitor.SetIslandOpen(true);
        await Task.Delay(40, Ct);

        Assert.Equal(1, _source.Calls);
        Assert.Equal(UsageStatus.SignedOut, _monitor.Current.Status);
    }

    [Fact]
    public async Task AMonitorThatIsStillRefusedStaysPausedAfterTheTryOnOpening()
    {
        var paused = await StartWith(Fail(ErrorCode.AccessDenied, retryable: false));
        Assert.Equal(Timeout.InfiniteTimeSpan, paused.Time);

        _time.Advance(TimeSpan.FromMinutes(1));
        _monitor.SetIslandOpen(true);
        await Until(() => _source.Calls == 2);
        var again = await NextWait();

        Assert.Equal(Timeout.InfiniteTimeSpan, again.Time); // refused again: paused again, no polling by itself
        Assert.Equal(UsageStatus.SignedOut, _monitor.Current.Status);
    }

    // ---- refresh ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARefreshRightAfterAGoodReadingAnswersFromItWithoutARequest()
    {
        await StartWith(Ok(Snap(T0, 0.1, T0.AddHours(3))));
        _time.Advance(TimeSpan.FromSeconds(10));

        var state = await _monitor.RefreshAsync(Ct);

        Assert.Equal(1, _source.Calls);
        Assert.Equal(UsageStatus.Ready, state.Status);
    }

    [Fact]
    public async Task ARefreshAfterTheGapLooksAgain()
    {
        await StartWith(Ok(Snap(T0, 0.1, T0.AddHours(3))));
        _time.Advance(TimeSpan.FromSeconds(31));
        _source.Then(Ok(Snap(_time.GetUtcNow(), 0.2, T0.AddHours(3))));

        var state = await _monitor.RefreshAsync(Ct);

        Assert.Equal(2, _source.Calls);
        Assert.Equal(20, state.Snapshot!.FiveHour!.UsedPercent);
    }

    [Fact]
    public async Task RefreshesThatComeWhileARequestIsRunningShareThatOneRequest()
    {
        await StartWith(Ok(Snap(T0, 0.1, T0.AddHours(3))));
        _time.Advance(TimeSpan.FromMinutes(2));
        _source.Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _source.Then(Ok(Snap(_time.GetUtcNow(), 0.2, T0.AddHours(3))));

        var first = _monitor.RefreshAsync(Ct);
        await Until(() => _source.Calls == 2);
        var second = _monitor.RefreshAsync(Ct);
        var third = _monitor.RefreshAsync(Ct);
        await Task.Delay(30, Ct);
        _source.Block.SetResult();
        var states = await Task.WhenAll(first, second, third);

        Assert.Equal(2, _source.Calls);
        Assert.All(states, s => Assert.Equal(20, s.Snapshot!.FiveHour!.UsedPercent));
    }

    [Fact]
    public async Task ACancelledRefreshReturnsTheEarlierReadingAndDoesNotBreakTheMonitor()
    {
        await StartWith(Ok(Snap(T0, 0.1, T0.AddHours(3))));
        _time.Advance(TimeSpan.FromMinutes(2));
        _source.Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();

        var pending = _monitor.RefreshAsync(cancel.Token);
        await Until(() => _source.Calls == 2);
        await cancel.CancelAsync();
        var state = await pending;

        Assert.True(_monitor.IsRunning);
        Assert.Equal(UsageStatus.Ready, state.Status);
        Assert.Equal(10, state.Snapshot!.FiveHour!.UsedPercent);
    }

    // ---- timing around a known reset ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ItLooksJustAfterAKnownResetTimeInsteadOfWaitingTheWholeInterval()
    {
        var wait = await StartWith(Ok(Snap(T0, 0.9, T0.AddMinutes(7))));

        Assert.Equal(TimeSpan.FromMinutes(7) + TimeSpan.FromSeconds(5), wait.Time);
    }

    [Fact]
    public async Task ThatExtraLookIsNeverCloserThanTheMinimumDelay()
    {
        var wait = await StartWith(Ok(Snap(T0, 0.9, T0.AddSeconds(1))));

        Assert.Equal(TimeSpan.FromSeconds(10), wait.Time);
    }

    [Fact]
    public async Task AResetTimeThatHasAlreadyPassedDoesNotCauseAHotLoop()
    {
        var wait = await StartWith(Ok(Snap(T0, 0.9, T0.AddMinutes(-5))));

        Assert.Equal(TimeSpan.FromMinutes(15), wait.Time);
        Assert.Equal(1, _source.Calls);
    }

    // ---- reset detection ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ALimitInUseWhoseResetTimeMovedOnIsAnnouncedOnceWithWhatItWas()
    {
        var expected = T0.AddMinutes(10);
        var w0 = await StartWith(Ok(Snap(T0, 0.92, expected)));
        Assert.Equal(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(5), w0.Time);

        var w1 = await Pass(w0, Ok(Snap(expected.AddSeconds(5), 0.03, expected.AddHours(5))));

        var reset = Assert.Single(_resets);
        Assert.Equal(UsageWindowKind.FiveHour, reset.Kind);
        Assert.Equal(expected, reset.ResetAt);
        Assert.Equal(expected.AddSeconds(5), reset.DetectedAt);
        Assert.Equal(92, reset.PreviousUsedPercent);

        await Pass(w1, Ok(Snap(_time.GetUtcNow(), 0.04, expected.AddHours(5))));
        Assert.Single(_resets); // the looks that follow do not announce it again
    }

    [Fact]
    public async Task AnEarlyResetIsAnnouncedWhenTheUsedShareDrops()
    {
        var w0 = await StartWith(Ok(Snap(T0, 0.8, T0.AddHours(4))));

        await Pass(w0, Ok(Snap(T0.AddMinutes(15), 0.0, T0.AddHours(4))));

        var reset = Assert.Single(_resets);
        Assert.Equal(T0.AddMinutes(15), reset.ResetAt); // early: the moment it was noticed
        Assert.Equal(80, reset.PreviousUsedPercent);
    }

    [Fact]
    public async Task APassedResetTimeWithNothingReportedAnymoreIsAReset()
    {
        var w0 = await StartWith(Ok(Snap(T0, 0.5, T0.AddMinutes(10))));

        await Pass(w0, Ok(new UsageSnapshot(null, null, T0.AddMinutes(11))));

        Assert.Equal(T0.AddMinutes(10), Assert.Single(_resets).ResetAt);
    }

    [Fact]
    public async Task NothingIsAnnouncedForTheFirstReadingNormalGrowthOrAnUnusedLimit()
    {
        var w0 = await StartWith(Ok(Snap(T0, 0.0, T0.AddMinutes(5), 0.4, T0.AddDays(3))));
        Assert.Empty(_resets); // the first reading has nothing to compare with

        // 5-hour: unused before, so its roll-over is nothing to announce. Weekly: grows within the same period.
        await Pass(w0, Ok(Snap(T0.AddMinutes(6), 0.0, T0.AddHours(5), 0.45, T0.AddDays(3))));

        Assert.Empty(_resets);
    }

    [Fact]
    public async Task SmallShiftsOfAResetTimeAreNoise()
    {
        var w0 = await StartWith(Ok(Snap(T0, 0.5, T0.AddMinutes(20))));

        await Pass(w0, Ok(Snap(T0.AddMinutes(21), 0.55, T0.AddMinutes(20).AddSeconds(40))));

        Assert.Empty(_resets);
    }

    [Fact]
    public async Task AResetDuringAnOutageIsAnnouncedOnceWhenTheServiceIsBack()
    {
        var expected = T0.AddMinutes(20);
        var w0 = await StartWith(Ok(Snap(T0, 0.7, expected)));

        var w1 = await Pass(w0, Fail(ErrorCode.DeviceUnavailable));
        Assert.Empty(_resets);
        Assert.Equal(UsageStatus.Unavailable, _monitor.Current.Status);

        await Pass(w1, Ok(Snap(T0.AddHours(2), 0.02, T0.AddHours(6))));

        var reset = Assert.Single(_resets);
        Assert.Equal(expected, reset.ResetAt);
        Assert.True(reset.DetectedAt > reset.ResetAt); // noticed late, and the value says how late
    }

    [Fact]
    public async Task BothLimitsCanResetTogetherAndEachIsAnnounced()
    {
        var w0 = await StartWith(Ok(Snap(T0, 0.6, T0.AddMinutes(10), 0.9, T0.AddMinutes(10))));

        await Pass(w0, Ok(Snap(T0.AddMinutes(11), 0.0, T0.AddHours(5), 0.0, T0.AddDays(7))));

        Assert.Equal([UsageWindowKind.FiveHour, UsageWindowKind.Weekly], _resets.Select(x => x.Kind));
        Assert.Equal([60, 90], _resets.Select(x => x.PreviousUsedPercent));
    }

    [Fact]
    public async Task AnObserverThatThrowsCannotBreakTheMonitor()
    {
        _monitor.Resets.Subscribe(new Throwing());
        var w0 = await StartWith(Ok(Snap(T0, 0.6, T0.AddMinutes(10))));

        await Pass(w0, Ok(Snap(T0.AddMinutes(11), 0.0, T0.AddHours(5))));

        Assert.Single(_resets);
        Assert.True(_monitor.IsRunning);
    }

    private sealed class Throwing : IObserver<UsageLimitReset>
    {
        public void OnNext(UsageLimitReset value) => throw new InvalidOperationException("observer failure");

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }

    // ---- disposal ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DisposingStopsTheLoopCompletesTheObservablesAndMakesNoMoreRequests()
    {
        await StartWith(Ok(Snap(T0, 0.1, T0.AddHours(3))));
        var completed = false;
        _monitor.Changes.Subscribe(new Done(() => completed = true));

        _monitor.Dispose();
        _monitor.Start();
        await _monitor.RefreshAsync(Ct);

        Assert.True(completed);
        Assert.False(_monitor.IsRunning);
        Assert.Equal(1, _source.Calls);
    }

    private sealed class Done(Action onCompleted) : IObserver<UsageState>
    {
        public void OnNext(UsageState value)
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted() => onCompleted();
    }
}
