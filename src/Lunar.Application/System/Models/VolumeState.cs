namespace Lunar.Application.System.Models;

/// <summary>Master volume snapshot. <see cref="Scalar"/> is 0..1; the UI maps it to 0–100.</summary>
public sealed record VolumeState
{
    public VolumeState(float scalar, bool isMuted, CapabilityStatus capability)
    {
        if (float.IsNaN(scalar) || scalar < 0f || scalar > 1f)
            throw new ArgumentOutOfRangeException(nameof(scalar), scalar, "Volume must be between 0 and 1.");
        if (!Enum.IsDefined(capability))
            throw new ArgumentOutOfRangeException(nameof(capability), capability, "Unknown capability.");

        Scalar = scalar;
        IsMuted = isMuted;
        Capability = capability;
    }

    public float Scalar { get; }

    public bool IsMuted { get; }

    public CapabilityStatus Capability { get; }

    /// <summary>State to show when no endpoint can be read.</summary>
    public static VolumeState Unavailable { get; } = new(0f, false, CapabilityStatus.Unavailable);
}
