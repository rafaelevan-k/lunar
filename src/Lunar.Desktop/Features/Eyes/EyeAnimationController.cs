namespace Lunar.Desktop.Features.Eyes;

/// <summary>
/// Deterministic blink scheduler (FE-08). It owns no rendering: it publishes <see cref="State"/> (the phase the lids
/// head to and how long that takes) and the view animates it, so there is no per-frame polling. Time, randomness and
/// the timer are injected, so a test scripts every step.
///
/// Rules: while running, a blink starts every 3–7 s (random) and lasts 120–220 ms; a blink can be followed by a quick
/// second one; after <see cref="EyeAnimationOptions.SleepAfter"/> without <see cref="NotifyInteraction"/> the eyes
/// fall asleep and the timer stops until the next interaction. It never runs while not started, paused, hidden or in
/// reduced motion; in each of those cases the lids snap open and no timer is pending, so an eyelid can never stay
/// frozen shut. Reduced motion means no automatic blinking at all (PRD §3.3).
/// Must be used on the UI thread.
/// </summary>
public sealed class EyeAnimationController : IDisposable
{
    private const double CloseShare = 0.4;
    private const double HoldShare = 0.1;

    private readonly IEyeTimer _timer;
    private readonly IRandomSource _random;
    private readonly TimeProvider _time;
    private readonly EyeAnimationOptions _options;

    private bool _started;
    private bool _paused;
    private bool _hidden = true;
    private bool _reduceMotion;
    private bool _disposed;
    private bool _scheduled;
    private bool _doubleNext;
    private bool _inDouble;
    private long _lastInteraction;
    private TimeSpan _blinkDuration;

    internal EyeAnimationController(IEyeTimer timer, IRandomSource random, TimeProvider time, EyeAnimationOptions? options = null)
    {
        _timer = timer ?? throw new ArgumentNullException(nameof(timer));
        _random = random ?? throw new ArgumentNullException(nameof(random));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = options ?? EyeAnimationOptions.Default;
        _options.Validate();
        _lastInteraction = _time.GetTimestamp();
    }

    /// <summary>Raised on every phase change, on the UI thread.</summary>
    public event EventHandler<EyeAnimationState>? StateChanged;

    public EyeAnimationState State { get; private set; } = EyeAnimationState.Open;

    /// <summary>True while a timer may be pending: started, not paused, visible and not in reduced motion.</summary>
    public bool IsRunning => IsActive;

    /// <summary>Windows or Lunar reduced motion: stops automatic blinking and snaps the eyes open.</summary>
    public bool ReduceMotion
    {
        get => _reduceMotion;
        set => Change(ref _reduceMotion, value);
    }

    /// <summary>The eyes are not on screen (hidden or minimized). Starts true until the view reports it is visible.</summary>
    public bool IsHidden
    {
        get => _hidden;
        set => Change(ref _hidden, value);
    }

    private bool IsActive => _started && !_paused && !_hidden && !_reduceMotion && !_disposed;

    public void Start()
    {
        if (_started || _disposed)
            return;
        _started = true;
        _lastInteraction = _time.GetTimestamp();
        Refresh();
    }

    public void Pause() => Change(ref _paused, true);

    public void Resume() => Change(ref _paused, false);

    /// <summary>The user hovered or clicked: resets the sleep countdown and wakes sleeping eyes.</summary>
    public void NotifyInteraction()
    {
        if (_disposed)
            return;
        _lastInteraction = _time.GetTimestamp();
        if (State.Phase == EyePhase.Asleep && IsActive)
        {
            SetState(EyePhase.Open, _options.WakeTransition);
            ScheduleBlink();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer.Cancel();
        _timer.Dispose();
        _scheduled = false;
    }

    private void Change(ref bool field, bool value)
    {
        if (field == value || _disposed)
            return;
        field = value;
        Refresh();
    }

    // Brings the timer and lids in line with the current inputs. Safe to call repeatedly.
    private void Refresh()
    {
        if (!IsActive)
        {
            _timer.Cancel();
            _scheduled = false;
            _doubleNext = false;
            _inDouble = false;
            if (State.Phase != EyePhase.Open)
                SetState(EyePhase.Open, TimeSpan.Zero);
            return;
        }

        // Asleep stays asleep (no timer) until an interaction. A blink cannot be in progress here: becoming
        // inactive already snapped the lids open and cleared the timer.
        if (State.Phase == EyePhase.Open && !_scheduled)
            ScheduleBlink();
    }

    private void ScheduleBlink() => Schedule(Between(_options.BlinkIntervalMin, _options.BlinkIntervalMax));

    private void Schedule(TimeSpan delay)
    {
        _scheduled = true;
        _timer.Schedule(delay, OnTimer);
    }

    private void OnTimer()
    {
        _scheduled = false;
        if (!IsActive)
            return;

        switch (State.Phase)
        {
            case EyePhase.Open:
                if (_time.GetElapsedTime(_lastInteraction) >= _options.SleepAfter)
                {
                    SetState(EyePhase.Asleep, _options.SleepTransition);
                    return;
                }

                _blinkDuration = Between(_options.BlinkDurationMin, _options.BlinkDurationMax);
                if (!_inDouble)
                    _doubleNext = _random.NextDouble() < _options.DoubleBlinkChance;
                Step(EyePhase.Closing, CloseShare);
                break;
            case EyePhase.Closing:
                Step(EyePhase.Closed, HoldShare);
                break;
            case EyePhase.Closed:
                Step(EyePhase.Opening, 1 - CloseShare - HoldShare);
                break;
            case EyePhase.Opening:
                SetState(EyePhase.Open, TimeSpan.Zero);
                if (_doubleNext)
                {
                    _doubleNext = false;
                    _inDouble = true;
                    Schedule(Between(_options.DoubleBlinkGapMin, _options.DoubleBlinkGapMax));
                }
                else
                {
                    _inDouble = false;
                    ScheduleBlink();
                }

                break;
        }
    }

    private void Step(EyePhase phase, double share)
    {
        var duration = TimeSpan.FromTicks((long)(_blinkDuration.Ticks * share));
        SetState(phase, duration);
        Schedule(duration);
    }

    private TimeSpan Between(TimeSpan min, TimeSpan max) =>
        min + TimeSpan.FromTicks((long)((max - min).Ticks * _random.NextDouble()));

    private void SetState(EyePhase phase, TimeSpan duration)
    {
        State = new EyeAnimationState(phase, duration);
        StateChanged?.Invoke(this, State);
    }
}
