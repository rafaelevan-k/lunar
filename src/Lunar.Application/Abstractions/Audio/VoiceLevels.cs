namespace Lunar.Application.Abstractions.Audio;

/// <summary>
/// One reading of the microphone's sound, low frequencies first: <see cref="VoiceLevelBands.Count"/> values from 0 (silence) to 1 (full scale).
/// Measured from the real signal and never stored.
/// </summary>
public sealed record VoiceLevels(IReadOnlyList<float> Bands);

/// <summary>How the frequency range is divided (ADR-0024).</summary>
public static class VoiceLevelBands
{
    public const int Count = 16;
}
