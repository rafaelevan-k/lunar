using System.Globalization;

namespace Lunar.Domain.Recording;

/// <summary>Where one recording stands (ADR-0022). <see cref="Failed"/> is a resting state: a new recording may start from it.</summary>
public enum RecordingPhase
{
    Idle,
    Starting,
    Recording,
    Paused,
    Saving,
    Failed
}

/// <summary>The fixed rules of a recording (ADR-0022).</summary>
public static class RecordingRules
{
    /// <summary>A recording stops and is saved at this length.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(12);

    /// <summary>A recording does not start, and stops and is saved, under this much free space on the destination.</summary>
    public const long MinFreeBytes = 100L * 1024 * 1024;

    public static bool IsActive(RecordingPhase phase) => phase is RecordingPhase.Starting or RecordingPhase.Recording or RecordingPhase.Paused or RecordingPhase.Saving;

    /// <summary>The name of a recording started at this local time, without extension. It holds only characters every Windows path accepts.</summary>
    public static string BaseNameFor(DateTime localStart) =>
        "Recording " + localStart.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);

    /// <summary>The name given to a recording rescued after Lunar stopped unexpectedly.</summary>
    public static string RecoveredBaseName(string baseName) => baseName + " (recovered)";
}
