using Lunar.Domain.Shared;

namespace Lunar.Application.Abstractions.Audio;

/// <summary>A finished recording.</summary>
public sealed record SavedRecording(string Path, string FileName);

/// <summary>What became of a recording left behind when Lunar stopped unexpectedly.</summary>
public enum RecoveryOutcome
{
    /// <summary>It plays and now has a normal name.</summary>
    Recovered,

    /// <summary>It could not be read as audio. It was left where it is, and the user is told.</summary>
    KeptAsItIs
}

/// <summary>A recording found at start-up.</summary>
public sealed record RecoveredRecording(string Path, RecoveryOutcome Outcome);

/// <summary>
/// The files of a recording (ADR-0022). Audio goes to a part file in the destination folder, and appears under its real name only when it
/// is complete. Expected failures are typed, with messages that carry no path. Nothing here logs a path.
/// </summary>
public interface IRecordingFiles
{
    /// <summary>The folder used until the user chooses one. It need not exist yet.</summary>
    string DefaultFolder { get; }

    /// <summary>Checks a folder could be used: a full local path that is not a link. Touches nothing.</summary>
    Result ValidateFolder(string folder);

    /// <summary>Free space where the folder is or would be.</summary>
    Result<long> GetFreeBytes(string folder);

    /// <summary>
    /// Creates the folder if needed, chooses a new part file path and remembers it, so a crash can be repaired. The file itself is not
    /// created. <paramref name="baseName"/> is the name it will get.
    /// </summary>
    Task<Result<string>> ReservePartAsync(string folder, string baseName, CancellationToken ct);

    /// <summary>
    /// Publishes a part file under its name, never replacing anything (a taken name gets a number). Only a file that really holds MP3 audio
    /// is published; otherwise it fails with <see cref="ErrorCode.InvalidInput"/> and the file stays for the next start's recovery.
    /// </summary>
    Task<Result<SavedRecording>> CommitAsync(string partPath, string baseName, CancellationToken ct);

    /// <summary>A recording that never began: forgets the part file and removes it if empty.</summary>
    Task AbandonAsync(string partPath, CancellationToken ct);

    /// <summary>Repairs what an unexpected stop left behind. Null when there is nothing. Never deletes audio.</summary>
    Task<Result<RecoveredRecording?>> RecoverAsync(CancellationToken ct);
}
