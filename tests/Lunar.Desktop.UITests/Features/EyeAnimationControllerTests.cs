using Lunar.Desktop.Features.Eyes;
using Xunit;

namespace Lunar.Desktop.UITests.Features;

// Pure state-machine tests: a hand-driven timer, scripted random numbers and a hand-driven clock. No WPF is touched.
public sealed class EyeAnimationControllerTests
{
    internal sealed class FakeTimer : IEyeTimer
    {
        private Action? _callback;

        public TimeSpan? Pending { get; private set; }

        public int Cancels { get; private set; }

        public bool Disposed { get; private set; }

        public void Schedule(TimeSpan delay, Action callback)
        {
            Pending = delay;
            _callback = callback;
        }

        public void Cancel()
        {
            Cancels++;
            Pending = null;
            _callback = null;
        }

        public void Dispose() => Disposed = true;

        /// <summary>Fires the pending call as the real timer would: it is no longer pending when it runs.</summary>
        public TimeSpan Fire()
        {
            var delay = Pending ?? throw new InvalidOperationException("Nothing is scheduled.");
            var callback = _callback!;
            Pending = null;
            _callback = null;
            callback();
            return delay;
        }
    }

    internal sealed class ScriptedRandom : IRandomSource
    {
        private readonly Queue<double> _values;

        public ScriptedRandom(params double[] values) => _values = new Queue<double>(values);

        public int Drawn { get; private set; }

        public double NextDouble()
        {
            Drawn++;
            return _values.Count > 0 ? _values.Dequeue() : 0.5;
        }
    }

    internal sealed class FakeTime : TimeProvider
    {
        private long _ms;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => _ms;

        public void Advance(TimeSpan by) => _ms += (long)by.TotalMilliseconds;
    }

    private sealed record Rig(EyeAnimationController Controller, FakeTimer Timer, FakeTime Time, List<EyeAnimationState> Seen);

    private static Rig Create(IRandomSource random, EyeAnimationOptions? options = null, bool visible = true, bool start = true)
    {
        var timer = new FakeTimer();
        var time = new FakeTime();
        var controller = new EyeAnimationController(timer, random, time, options);
        var seen = new List<EyeAnimationState>();
        controller.StateChanged += (_, state) => seen.Add(state);
        controller.IsHidden = !visible;
        if (start)
            controller.Start();
        return new Rig(controller, timer, time, seen);
    }

    [Fact]
    public void NothingIsScheduledUntilStartedAndVisible()
    {
        var rig = Create(new ScriptedRandom(), visible: false, start: false);
        Assert.Null(rig.Timer.Pending);

        rig.Controller.Start();
        Assert.Null(rig.Timer.Pending); // started but not visible
        Assert.False(rig.Controller.IsRunning);

        rig.Controller.IsHidden = false;
        Assert.NotNull(rig.Timer.Pending);
        Assert.True(rig.Controller.IsRunning);
    }

    [Theory]
    [InlineData(0.0, 3)]
    [InlineData(0.5, 5)]
    [InlineData(0.999999, 7)]
    public void FirstBlinkIsBetweenThreeAndSevenSeconds(double draw, double expectedSeconds)
    {
        var rig = Create(new ScriptedRandom(draw));

        Assert.Equal(expectedSeconds, rig.Timer.Pending!.Value.TotalSeconds, precision: 3);
    }

    [Fact]
    public void ASingleBlinkClosesHoldsAndOpensWithinTheBlinkDuration()
    {
        // interval draw, blink-duration draw (0.5 -> 170 ms), double-blink draw (0.9 -> no double), next interval draw
        var rig = Create(new ScriptedRandom(0.0, 0.5, 0.9, 0.0));

        rig.Timer.Fire();
        Assert.Equal(new EyeAnimationState(EyePhase.Closing, TimeSpan.FromMilliseconds(68)), rig.Controller.State);
        Assert.Equal(TimeSpan.FromMilliseconds(68), rig.Timer.Pending);

        rig.Timer.Fire();
        Assert.Equal(new EyeAnimationState(EyePhase.Closed, TimeSpan.FromMilliseconds(17)), rig.Controller.State);

        rig.Timer.Fire();
        Assert.Equal(new EyeAnimationState(EyePhase.Opening, TimeSpan.FromMilliseconds(85)), rig.Controller.State);

        rig.Timer.Fire();
        Assert.Equal(EyeAnimationState.Open, rig.Controller.State);
        Assert.Equal(TimeSpan.FromSeconds(3), rig.Timer.Pending); // the next blink, no double
        Assert.Equal(
            new[] { EyePhase.Closing, EyePhase.Closed, EyePhase.Opening, EyePhase.Open },
            rig.Seen.Select(s => s.Phase));
    }

    [Fact]
    public void ADoubleBlinkIsFollowedByAShortGapThenOneMoreBlinkAndNoFurtherDouble()
    {
        // interval, duration, double? (0.0 -> yes), gap draw (0.0 -> 90 ms), duration 2, next interval (0.5 -> 5 s)
        var random = new ScriptedRandom(0.0, 0.0, 0.0, 0.0, 0.0, 0.5);
        var rig = Create(random);

        rig.Timer.Fire(); // first blink starts
        rig.Timer.Fire();
        rig.Timer.Fire();
        rig.Timer.Fire(); // first blink ends
        Assert.Equal(TimeSpan.FromMilliseconds(90), rig.Timer.Pending);

        var drawsBeforeSecond = random.Drawn;
        rig.Timer.Fire(); // second blink starts
        Assert.Equal(EyePhase.Closing, rig.Controller.State.Phase);
        Assert.Equal(drawsBeforeSecond + 1, random.Drawn); // only the duration; the double-blink dice are not rolled again

        rig.Timer.Fire();
        rig.Timer.Fire();
        rig.Timer.Fire(); // second blink ends
        Assert.Equal(EyeAnimationState.Open, rig.Controller.State);
        Assert.Equal(TimeSpan.FromSeconds(5), rig.Timer.Pending);
    }

    [Fact]
    public void EveryBlinkAndIntervalStaysInsideTheConfiguredBounds()
    {
        var options = EyeAnimationOptions.Default;
        var rig = Create(new SeededRandom(7), options);

        for (var blink = 0; blink < 500; blink++)
        {
            var wait = rig.Timer.Fire(); // the wait before this blink: a normal interval or a double-blink gap
            var isGap = wait <= options.DoubleBlinkGapMax;
            Assert.InRange(wait,
                isGap ? options.DoubleBlinkGapMin : options.BlinkIntervalMin,
                isGap ? options.DoubleBlinkGapMax : options.BlinkIntervalMax);

            var total = rig.Timer.Fire() + rig.Timer.Fire() + rig.Timer.Fire(); // close + hold + open
            Assert.InRange(total, options.BlinkDurationMin - TimeSpan.FromTicks(3), options.BlinkDurationMax);
            Assert.Equal(EyeAnimationState.Open, rig.Controller.State);
        }
    }

    private sealed class SeededRandom(int seed) : IRandomSource
    {
        private readonly Random _random = new(seed);

        public double NextDouble() => _random.NextDouble();
    }

    [Fact]
    public void ReducedMotionStopsBlinkingAndSnapsAnOpenLidMidBlink()
    {
        var rig = Create(new ScriptedRandom(0.0, 0.5, 0.9));
        rig.Timer.Fire();
        rig.Timer.Fire();
        Assert.Equal(EyePhase.Closed, rig.Controller.State.Phase);

        rig.Controller.ReduceMotion = true;

        Assert.Equal(EyeAnimationState.Open, rig.Controller.State);
        Assert.Null(rig.Timer.Pending);
        Assert.False(rig.Controller.IsRunning);

        rig.Controller.ReduceMotion = false;
        Assert.NotNull(rig.Timer.Pending); // blinking resumes
    }

    [Fact]
    public void ReducedMotionFromTheStartNeverSchedulesAnything()
    {
        var timer = new FakeTimer();
        var controller = new EyeAnimationController(timer, new ScriptedRandom(), new FakeTime()) { ReduceMotion = true, IsHidden = false };

        controller.Start();

        Assert.Null(timer.Pending);
        Assert.Equal(EyeAnimationState.Open, controller.State);
    }

    [Fact]
    public void PauseMidBlinkOpensTheLidAndResumeSchedulesTheNextBlink()
    {
        var rig = Create(new ScriptedRandom(0.0, 0.5, 0.9));
        rig.Timer.Fire();
        rig.Timer.Fire();

        rig.Controller.Pause();
        Assert.Equal(EyeAnimationState.Open, rig.Controller.State);
        Assert.Null(rig.Timer.Pending);

        rig.Controller.Pause(); // repeating is harmless
        Assert.Null(rig.Timer.Pending);

        rig.Controller.Resume();
        Assert.Equal(EyePhase.Open, rig.Controller.State.Phase);
        Assert.NotNull(rig.Timer.Pending);
    }

    [Fact]
    public void HidingStopsTheTimerAndShowingRestartsIt()
    {
        var rig = Create(new ScriptedRandom());

        rig.Controller.IsHidden = true;
        Assert.Null(rig.Timer.Pending);

        rig.Controller.IsHidden = false;
        Assert.NotNull(rig.Timer.Pending);
    }

    [Fact]
    public void RedundantInputsDoNotRescheduleOrRaiseEvents()
    {
        var rig = Create(new ScriptedRandom(0.0));
        var pending = rig.Timer.Pending;

        rig.Controller.Resume();
        rig.Controller.IsHidden = false;
        rig.Controller.ReduceMotion = false;
        rig.Controller.Start();

        Assert.Equal(pending, rig.Timer.Pending);
        Assert.Empty(rig.Seen);
    }

    [Fact]
    public void LongInactivityPutsTheEyesToSleepWithNoTimerAndInteractionWakesThem()
    {
        var rig = Create(new ScriptedRandom(0.0, 0.0));

        rig.Time.Advance(TimeSpan.FromMinutes(6));
        rig.Timer.Fire();

        Assert.Equal(new EyeAnimationState(EyePhase.Asleep, TimeSpan.FromMilliseconds(600)), rig.Controller.State);
        Assert.Null(rig.Timer.Pending); // asleep: zero timers

        rig.Controller.NotifyInteraction();

        Assert.Equal(new EyeAnimationState(EyePhase.Open, TimeSpan.FromMilliseconds(200)), rig.Controller.State);
        Assert.NotNull(rig.Timer.Pending); // blinking resumes
    }

    [Fact]
    public void InteractionResetsTheSleepCountdown()
    {
        var rig = Create(new ScriptedRandom(0.0, 0.5, 0.9, 0.0));

        rig.Time.Advance(TimeSpan.FromMinutes(4));
        rig.Controller.NotifyInteraction();
        rig.Time.Advance(TimeSpan.FromMinutes(4));
        rig.Timer.Fire();

        Assert.Equal(EyePhase.Closing, rig.Controller.State.Phase); // 4 minutes since interaction: still awake
    }

    [Fact]
    public void AsleepEyesDoNotWakeWhilePausedOrHidden()
    {
        var rig = Create(new ScriptedRandom(0.0));
        rig.Time.Advance(TimeSpan.FromMinutes(6));
        rig.Timer.Fire();
        Assert.Equal(EyePhase.Asleep, rig.Controller.State.Phase);

        rig.Controller.IsHidden = true;
        Assert.Equal(EyeAnimationState.Open, rig.Controller.State); // never left half shut off screen

        rig.Controller.NotifyInteraction();
        Assert.Null(rig.Timer.Pending);
    }

    [Fact]
    public void DisposeCancelsTheTimerAndLaterCallsDoNothing()
    {
        var rig = Create(new ScriptedRandom());

        rig.Controller.Dispose();
        rig.Controller.Dispose(); // idempotent

        Assert.True(rig.Timer.Disposed);
        Assert.Null(rig.Timer.Pending);
        rig.Controller.Start();
        rig.Controller.Resume();
        rig.Controller.NotifyInteraction();
        rig.Controller.IsHidden = true;
        rig.Controller.IsHidden = false;
        Assert.Null(rig.Timer.Pending);
        Assert.False(rig.Controller.IsRunning);
    }

    [Theory]
    [InlineData("interval")]
    [InlineData("duration")]
    [InlineData("chance")]
    [InlineData("sleep")]
    public void InvalidOptionsAreRejected(string which)
    {
        var options = which switch
        {
            "interval" => EyeAnimationOptions.Default with { BlinkIntervalMin = TimeSpan.FromSeconds(8) },
            "duration" => EyeAnimationOptions.Default with { BlinkDurationMin = TimeSpan.Zero },
            "chance" => EyeAnimationOptions.Default with { DoubleBlinkChance = 1.5 },
            _ => EyeAnimationOptions.Default with { SleepAfter = TimeSpan.Zero }
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => new EyeAnimationController(new FakeTimer(), new ScriptedRandom(), new FakeTime(), options));
    }
}
