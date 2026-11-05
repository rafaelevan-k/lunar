namespace Lunar.Desktop.Navigation;

/// <summary>What a finished drag decided.</summary>
/// <param name="TargetIndex">The page the content settles on.</param>
/// <param name="Changed">The selected page changed.</param>
public readonly record struct SwipeResult(int TargetIndex, bool Changed);

/// <summary>
/// Tracks one drag gesture at a time (FE-11) and turns it into a content offset while it runs and a page choice when it
/// ends, using <see cref="SwipeRules"/>. It has no WPF types and no clock: the caller passes pointer positions and
/// millisecond timestamps, so every case is tested deterministically. The page itself is held by
/// <see cref="PageNavigationViewModel"/>, whose range check keeps the index within 0 to PageCount - 1.
/// </summary>
public sealed class SwiperController
{
    private const int VelocityWindowMs = 100;

    private readonly PageNavigationViewModel _navigation;
    private readonly List<(double X, long Time)> _samples = [];
    private double _startX;

    public SwiperController(PageNavigationViewModel navigation)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        _navigation = navigation;
    }

    /// <summary>The width of one page in device-independent units; the view keeps it current.</summary>
    public double PageWidth { get; set; }

    public bool IsDragging { get; private set; }

    /// <summary>Finger distance since the drag began; positive is to the right.</summary>
    public double Delta { get; private set; }

    /// <summary>Where the content sits relative to the resting position of the selected page (damped past the ends).</summary>
    public double Offset { get; private set; }

    /// <summary>Starts a drag. False when one is already running or the page width is not known yet.</summary>
    public bool Begin(double x, long timestampMs)
    {
        if (IsDragging || !double.IsFinite(x) || !double.IsFinite(PageWidth) || PageWidth <= 0)
            return false;

        IsDragging = true;
        _startX = x;
        Delta = 0;
        Offset = 0;
        _samples.Clear();
        _samples.Add((x, timestampMs));
        return true;
    }

    /// <summary>Follows the finger and returns the new <see cref="Offset"/>. Ignored when no drag is running.</summary>
    public double Move(double x, long timestampMs)
    {
        if (!IsDragging || !double.IsFinite(x))
            return Offset;

        Delta = x - _startX;
        Offset = SwipeRules.ContentOffset(_navigation.SelectedIndex, PageNavigationViewModel.PageCount, Delta, PageWidth);
        _samples.Add((x, timestampMs));
        Trim(timestampMs);
        return Offset;
    }

    /// <summary>Ends the drag, selects the page it lands on, and clears the offset.</summary>
    public SwipeResult End(long timestampMs)
    {
        if (!IsDragging)
            return new SwipeResult(_navigation.SelectedIndex, false);

        Trim(timestampMs);
        var target = SwipeRules.TargetIndex(_navigation.SelectedIndex, PageNavigationViewModel.PageCount, Delta, Velocity(), PageWidth);
        Reset();
        var changed = _navigation.GoTo(target);
        return new SwipeResult(_navigation.SelectedIndex, changed);
    }

    /// <summary>Abandons the drag without changing the page (capture lost, window deactivated).</summary>
    public void Cancel() => Reset();

    private void Reset()
    {
        IsDragging = false;
        Delta = 0;
        Offset = 0;
        _samples.Clear();
    }

    // Keeps only the last 100 ms of movement, so a pause before releasing means no flick.
    private void Trim(long now) => _samples.RemoveAll(sample => now - sample.Time > VelocityWindowMs);

    private double Velocity()
    {
        if (_samples.Count < 2)
            return 0;

        var first = _samples[0];
        var last = _samples[^1];
        var elapsed = last.Time - first.Time;
        return elapsed <= 0 ? 0 : (last.X - first.X) / elapsed;
    }
}
