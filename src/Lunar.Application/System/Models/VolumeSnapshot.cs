namespace Lunar.Application.System.Models;

/// <summary>Where a <see cref="VolumeSnapshot"/> came from, so the UI can tell its own writes from outside changes.</summary>
public enum VolumeChangeSource
{
    /// <summary>The first reading, when the controller started.</summary>
    Initial,

    /// <summary>Windows reporting back a change this controller just made (the echo of a slider drag or a mute toggle).</summary>
    Self,

    /// <summary>A change made by someone else: the keyboard volume keys, another app, the taskbar, a device switch.</summary>
    External,

    /// <summary>The authoritative value read after a failed write, so the UI can snap back to the truth.</summary>
    Corrected
}

/// <summary>A volume state and where it came from.</summary>
public sealed record VolumeSnapshot(VolumeState State, VolumeChangeSource Source);

/// <summary>Tunable behavior of the volume controller.</summary>
public sealed record VolumeControllerOptions
{
    public static VolumeControllerOptions Default { get; } = new();

    /// <summary>
    /// How far a reported level may differ from the written one and still count as the echo of that write. Drivers round
    /// to their own steps (commonly 1%), so exact equality would label the echo of every drag as an outside change.
    /// </summary>
    public float EchoTolerance { get; init; } = 0.011f;

    /// <summary>How long an echo is expected after a write; after that a matching report is treated as an outside change.</summary>
    public TimeSpan EchoWindow { get; init; } = TimeSpan.FromSeconds(2);

    internal void Validate()
    {
        if (!(EchoTolerance is >= 0f and <= 0.5f))
            throw new ArgumentOutOfRangeException(nameof(EchoTolerance), EchoTolerance, "Tolerance must be between 0 and 0.5.");
        if (EchoWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(EchoWindow), EchoWindow, "The echo window must be positive.");
    }
}
