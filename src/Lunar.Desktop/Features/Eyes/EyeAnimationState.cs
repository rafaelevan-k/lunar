namespace Lunar.Desktop.Features.Eyes;

/// <summary>Where the eyelids are in a blink or rest cycle.</summary>
public enum EyePhase
{
    /// <summary>Eyes open and waiting for the next blink.</summary>
    Open,
    Closing,
    Closed,
    Opening,
    /// <summary>Idle for a long time: lids half closed and no timer running until the user interacts.</summary>
    Asleep
}

/// <summary>
/// One step of the eye animation: the phase the lids are heading to and the time the view should take to get there.
/// A zero <see cref="Duration"/> means "already there / snap", so the view never leaves a lid frozen mid-way.
/// </summary>
public readonly record struct EyeAnimationState(EyePhase Phase, TimeSpan Duration)
{
    public static EyeAnimationState Open { get; } = new(EyePhase.Open, TimeSpan.Zero);
}

/// <summary>Timing bounds of the eye animation (PRD §3.3). Every range is inclusive and validated on use.</summary>
public sealed record EyeAnimationOptions
{
    public static EyeAnimationOptions Default { get; } = new();

    public TimeSpan BlinkIntervalMin { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan BlinkIntervalMax { get; init; } = TimeSpan.FromSeconds(7);

    /// <summary>Total time of one blink (close, hold, open).</summary>
    public TimeSpan BlinkDurationMin { get; init; } = TimeSpan.FromMilliseconds(120);

    public TimeSpan BlinkDurationMax { get; init; } = TimeSpan.FromMilliseconds(220);

    /// <summary>Probability, 0 to 1, that a blink is followed by a quick second one.</summary>
    public double DoubleBlinkChance { get; init; } = 0.15;

    public TimeSpan DoubleBlinkGapMin { get; init; } = TimeSpan.FromMilliseconds(90);

    public TimeSpan DoubleBlinkGapMax { get; init; } = TimeSpan.FromMilliseconds(160);

    /// <summary>No interaction for this long: the eyes fall asleep and every timer stops.</summary>
    public TimeSpan SleepAfter { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan SleepTransition { get; init; } = TimeSpan.FromMilliseconds(600);

    public TimeSpan WakeTransition { get; init; } = TimeSpan.FromMilliseconds(200);

    internal void Validate()
    {
        Range(BlinkIntervalMin, BlinkIntervalMax, nameof(BlinkIntervalMin), positive: true);
        Range(BlinkDurationMin, BlinkDurationMax, nameof(BlinkDurationMin), positive: true);
        Range(DoubleBlinkGapMin, DoubleBlinkGapMax, nameof(DoubleBlinkGapMin), positive: false);
        if (!(DoubleBlinkChance is >= 0 and <= 1))
            throw new ArgumentOutOfRangeException(nameof(DoubleBlinkChance), DoubleBlinkChance, "Chance must be between 0 and 1.");
        if (SleepAfter <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(SleepAfter), SleepAfter, "Sleep delay must be positive.");
        if (SleepTransition < TimeSpan.Zero || WakeTransition < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(SleepTransition), "Transition times must not be negative.");
    }

    private static void Range(TimeSpan min, TimeSpan max, string name, bool positive)
    {
        if (min < TimeSpan.Zero || max < min || (positive && min == TimeSpan.Zero))
            throw new ArgumentOutOfRangeException(name, "Range must be non-negative with min not above max.");
    }
}
