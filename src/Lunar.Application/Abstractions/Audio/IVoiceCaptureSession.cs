using Lunar.Domain.Shared;

namespace Lunar.Application.Abstractions.Audio;

/// <summary>
/// One running recording. Calls are serialized and run off the calling thread. Disposing releases the microphone; when
/// <see cref="FinishAsync"/> did not succeed the file stays as it is, for the owner to recover or report.
/// </summary>
public interface IVoiceCaptureSession : IAsyncDisposable
{
    /// <summary>Time actually written to the file, counted from the audio itself: a pause adds nothing. Safe from any thread.</summary>
    TimeSpan Recorded { get; }

    /// <summary>Stops writing and keeps the file open. The levels go flat. Pausing twice is harmless.</summary>
    Task<Result> PauseAsync(CancellationToken ct);

    /// <summary>Continues writing to the same file. Resuming while recording is harmless.</summary>
    Task<Result> ResumeAsync(CancellationToken ct);

    /// <summary>
    /// Stops and completes the file, within a time limit (<see cref="ErrorCode.Timeout"/>). Fails with <see cref="ErrorCode.IoFailure"/> when
    /// the file could not be completed. Also the way to save what was captured after <see cref="Failed"/>.
    /// </summary>
    Task<Result> FinishAsync(CancellationToken ct);

    /// <summary>About 20 readings a second while recording, and one flat reading on pause. Raised on an audio thread: handlers must be quick, marshal to the UI and not throw.</summary>
    event Action<VoiceLevels>? LevelsChanged;

    /// <summary>The recording cannot go on (the input was removed, Windows lost the device). Raised at most once, on a background thread. Call <see cref="FinishAsync"/> next.</summary>
    event Action<OperationError>? Failed;
}
