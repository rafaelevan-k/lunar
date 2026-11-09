using Lunar.Application.Abstractions.Audio;
using Lunar.Domain.Recording;
using Lunar.Domain.Shared;

namespace Lunar.Application.Recording;

/// <summary>Something the user should be told that is not a failure of what they just asked for.</summary>
public enum RecordingNotice
{
    None,

    /// <summary>The chosen microphone is gone, so the Windows default is being used.</summary>
    ChosenInputMissing,

    /// <summary>The microphone or audio device was lost. What had been captured is saved.</summary>
    DeviceLost,

    /// <summary>The destination is nearly full. The recording was stopped and saved.</summary>
    DiskNearlyFull,

    /// <summary>The recording reached the length limit and was saved.</summary>
    LengthLimitReached,

    /// <summary>A recording left by an unexpected stop was found; see <see cref="RecordingSnapshot.Recovered"/>.</summary>
    RecordingRecovered
}

/// <summary>The recorder's state as the page and the island show it.</summary>
public sealed record RecordingSnapshot(
    RecordingPhase Phase,
    RecordingNotice Notice = RecordingNotice.None,
    OperationError? Error = null,
    SavedRecording? LastSaved = null,
    RecoveredRecording? Recovered = null)
{
    public static RecordingSnapshot Initial { get; } = new(RecordingPhase.Idle);
}
