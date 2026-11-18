using Lunar.Domain.Shared;

namespace Lunar.Application.Abstractions.Audio;

/// <summary>
/// One reading of what Windows is playing, low frequencies first: <see cref="PlaybackLevelBands.Count"/> values from 0 (silence) to 1 (full
/// scale). Measured from the real sound and never stored (ADR-0026).
/// </summary>
public sealed record PlaybackLevels(IReadOnlyList<float> Bands)
{
    /// <summary>All bands at zero: silence, a pause, or no sound to read.</summary>
    public static PlaybackLevels Flat { get; } = new(new float[PlaybackLevelBands.Count]);
}

/// <summary>How the media pill divides the sound (ADR-0026).</summary>
public static class PlaybackLevelBands
{
    public const int Count = 6;

    /// <summary>About how often a reading is published while the meter runs.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(33);
}

/// <summary>
/// Reads the level of what the default playback device is playing (a loopback copy, never the microphone) while the media pill is visible
/// (ADR-0026). Nothing runs until <see cref="StartAsync"/> and nothing after <see cref="Stop"/>. When the default device changes the meter moves
/// to the new one by itself.
/// </summary>
public interface IPlaybackLevelSource : IDisposable
{
    /// <summary>True between a successful start and a stop.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Opens the default playback device for reading and starts publishing. Starting twice is harmless. Fails with
    /// <see cref="ErrorCode.DeviceUnavailable"/> when there is no playback device and <see cref="ErrorCode.Unsupported"/> for a sound format the
    /// meter cannot read. Runs off the calling thread.
    /// </summary>
    Task<Result> StartAsync(CancellationToken ct);

    /// <summary>Stops reading and releases the device; publishes one flat reading. Stopping twice is harmless. Never blocks for long.</summary>
    void Stop();

    /// <summary>About 30 readings a second while running; flat when nothing plays. Raised on an audio thread: handlers must be quick and not throw.</summary>
    event Action<PlaybackLevels>? LevelsChanged;
}
