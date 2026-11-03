namespace Lunar.Desktop.Features.Eyes;

/// <summary>
/// Pure gaze rule (FE-09): where a pupil goes for a pointer at a given distance from the eye. No WPF types, so it is
/// tested without UI. The displacement grows smoothly from zero at the eye centre to a fixed cap at
/// <see cref="ProximityRadius"/> and stays there farther out, so the pupil never leaves the eye and never jitters near
/// the centre.
/// </summary>
public static class EyeGaze
{
    /// <summary>Largest pupil displacement in the eye's own logical units (the pupil is 12 in a 24x26 eye).</summary>
    public const double MaxOffset = 5;

    /// <summary>Pointer distance, in device-independent units, at which the cap is reached.</summary>
    public const double ProximityRadius = 70;

    /// <summary>
    /// Pupil offset for a pointer at (<paramref name="dx"/>, <paramref name="dy"/>) device-independent units from the
    /// eye centre. Non-finite input means no gaze (neutral). The result's length never exceeds <see cref="MaxOffset"/>.
    /// </summary>
    public static (double X, double Y) Offset(double dx, double dy)
    {
        if (!double.IsFinite(dx) || !double.IsFinite(dy))
            return (0, 0);

        var distance = Math.Sqrt((dx * dx) + (dy * dy));
        if (distance < 1e-9)
            return (0, 0);

        var t = Math.Min(distance / ProximityRadius, 1);
        var eased = t * t * (3 - (2 * t)); // smoothstep: no sudden jumps near the centre or at the cap
        var length = MaxOffset * eased;
        return (dx / distance * length, dy / distance * length);
    }
}
