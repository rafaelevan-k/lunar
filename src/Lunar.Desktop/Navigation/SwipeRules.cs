namespace Lunar.Desktop.Navigation;

/// <summary>
/// Pure swipe rules (PRD §3.4): how far a drag moves the content, and which page it lands on. No WPF types, so they
/// are tested without UI. Distances are device-independent units (DIPs), velocity is DIPs per millisecond and positive
/// to the right.
/// </summary>
public static class SwipeRules
{
    /// <summary>Movement below this is a click, not a drag.</summary>
    public const double Slop = 4;

    /// <summary>A drag of at least this many DIPs, or a quarter of the page width if that is smaller, changes the page.</summary>
    public const double ThresholdDip = 55;

    public const double ThresholdShare = 0.25;

    /// <summary>A quick flick (at least this fast, over at least <see cref="FlickMinDistance"/>) changes the page too.</summary>
    public const double FlickVelocity = 0.5;

    public const double FlickMinDistance = 12;

    /// <summary>Dragging past the first or last page moves the content only this share of the finger distance.</summary>
    public const double RubberBandFactor = 0.35;

    /// <summary>The rubber band never stretches farther than this share of the page width.</summary>
    public const double RubberBandMaxShare = 0.25;

    /// <summary>Time to settle on a page after a swipe, in milliseconds (PRD: 200–300 ms).</summary>
    public const int SettleMilliseconds = 250;

    /// <summary>The drag distance that changes the page for pages of the given width.</summary>
    public static double Threshold(double pageWidth) =>
        double.IsFinite(pageWidth) && pageWidth > 0 ? Math.Min(ThresholdDip, pageWidth * ThresholdShare) : ThresholdDip;

    /// <summary>
    /// Where the content sits, relative to the resting position of page <paramref name="index"/>, for a finger that has
    /// moved <paramref name="delta"/>. It follows the finger inside the page range, is damped and capped past either
    /// end, and never moves more than one page.
    /// </summary>
    public static double ContentOffset(int index, int pageCount, double delta, double pageWidth)
    {
        if (!double.IsFinite(delta) || !double.IsFinite(pageWidth) || pageWidth <= 0 || pageCount <= 0)
            return 0;

        var pastStart = index <= 0 && delta > 0;
        var pastEnd = index >= pageCount - 1 && delta < 0;
        if (pastStart || pastEnd)
        {
            var stretch = Math.Min(Math.Abs(delta) * RubberBandFactor, pageWidth * RubberBandMaxShare);
            return Math.Sign(delta) * stretch;
        }

        return Math.Clamp(delta, -pageWidth, pageWidth);
    }

    /// <summary>
    /// The page a finished drag lands on, always within 0 to <paramref name="pageCount"/> - 1. Dragging left goes to the
    /// next page. A drag past the threshold or a flick in the drag's direction changes the page; otherwise it snaps back.
    /// </summary>
    public static int TargetIndex(int index, int pageCount, double delta, double velocity, double pageWidth)
    {
        var current = Math.Clamp(index, 0, Math.Max(pageCount - 1, 0));
        if (pageCount <= 0 || !double.IsFinite(delta) || !double.IsFinite(velocity))
            return current;

        var distance = Math.Abs(delta);
        var farEnough = distance >= Threshold(pageWidth);
        var flicked = Math.Abs(velocity) >= FlickVelocity && distance >= FlickMinDistance && Math.Sign(velocity) == Math.Sign(delta);
        if (!farEnough && !flicked)
            return current;

        var step = delta < 0 ? 1 : -1;
        return Math.Clamp(current + step, 0, pageCount - 1);
    }
}
