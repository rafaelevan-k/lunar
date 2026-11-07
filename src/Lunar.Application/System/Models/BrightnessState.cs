namespace Lunar.Application.System.Models;

/// <summary>How a display's brightness is reached (ADR-0021).</summary>
public enum BrightnessSource
{
    /// <summary>No way to change it from Lunar.</summary>
    None,

    /// <summary>A built-in panel, through Windows (WMI), the same path as the quick-settings slider.</summary>
    BuiltIn,

    /// <summary>An external monitor, through DDC/CI. Best effort: depends on the monitor and the cable or dock.</summary>
    External
}

/// <summary>
/// One display's brightness, 0–100, and whether Lunar can change it. <see cref="Percent"/> is meaningful only when
/// <see cref="Capability"/> is <see cref="CapabilityStatus.Supported"/>.
/// </summary>
public sealed record BrightnessState
{
    public BrightnessState(int percent, BrightnessSource source, CapabilityStatus capability)
    {
        if (percent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(percent), percent, "Brightness must be between 0 and 100.");
        if (!Enum.IsDefined(source))
            throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown source.");
        if (!Enum.IsDefined(capability))
            throw new ArgumentOutOfRangeException(nameof(capability), capability, "Unknown capability.");

        Percent = percent;
        Source = source;
        Capability = capability;
    }

    public int Percent { get; }

    public BrightnessSource Source { get; }

    public CapabilityStatus Capability { get; }

    /// <summary>Neither Windows nor the monitor lets Lunar change this display's brightness.</summary>
    public static BrightnessState Unsupported { get; } = new(0, BrightnessSource.None, CapabilityStatus.Unsupported);

    /// <summary>The display could not be found (unplugged, or the session is locked).</summary>
    public static BrightnessState Unavailable { get; } = new(0, BrightnessSource.None, CapabilityStatus.Unavailable);
}
