using Lunar.Application.Settings.Models;

namespace Lunar.Application.Settings;

/// <summary>
/// When motion is off. The user's "reduce motion" and Windows' "show animations" switch stop every animation; the idle-eye
/// switch stops only the eyes (blinking and gaze), not slides and fades.
/// </summary>
public static class MotionPolicy
{
    /// <summary>Slides, fades, squish and confetti.</summary>
    public static bool InterfaceMotionOff(AppearanceSettings appearance, bool windowsAnimationsOn)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        return appearance.ReduceMotion || !windowsAnimationsOn;
    }

    /// <summary>The eyes' blinking, gaze and reactions.</summary>
    public static bool EyeMotionOff(AppearanceSettings appearance, bool windowsAnimationsOn)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        return InterfaceMotionOff(appearance, windowsAnimationsOn) || !appearance.EyeAnimationEnabled;
    }
}
