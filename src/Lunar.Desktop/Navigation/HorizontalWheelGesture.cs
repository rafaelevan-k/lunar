namespace Lunar.Desktop.Navigation;

/// <summary>
/// Turns the horizontal wheel messages of a touchpad two-finger swipe (or a tilt wheel) into at most one page step per
/// gesture (FE-11). Deltas are summed; once they reach <see cref="StepDelta"/> one step is returned and the gesture is
/// locked until the input pauses for <see cref="IdleGapMs"/>, so a long swipe (and the inertia that follows it) moves a
/// single page. Pure: the caller passes millisecond timestamps.
/// </summary>
public sealed class HorizontalWheelGesture
{
    /// <summary>One wheel notch, as Windows reports it.</summary>
    public const int StepDelta = 120;

    public const long IdleGapMs = 250;

    private int _sum;
    private bool _locked;
    private long _last = long.MinValue;

    /// <summary>
    /// Feeds one horizontal delta (positive scrolls right). Returns 1 for the next page, -1 for the previous page, 0 for
    /// nothing yet.
    /// </summary>
    public int Add(int delta, long timestampMs)
    {
        if (_last != long.MinValue && timestampMs - _last > IdleGapMs)
        {
            _sum = 0;
            _locked = false;
        }

        _last = timestampMs;
        if (delta == 0 || _locked)
            return 0;

        // A reversal inside one gesture starts counting again from the new direction.
        if (Math.Sign(delta) != Math.Sign(_sum) && _sum != 0)
            _sum = 0;

        _sum += delta;
        if (Math.Abs(_sum) < StepDelta)
            return 0;

        var step = _sum > 0 ? 1 : -1;
        _sum = 0;
        _locked = true;
        return step;
    }

    public void Reset()
    {
        _sum = 0;
        _locked = false;
        _last = long.MinValue;
    }
}
