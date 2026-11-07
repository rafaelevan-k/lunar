using Lunar.Domain.Notes;
using Lunar.Domain.Shared;

namespace Lunar.Application.Abstractions.Notes;

/// <summary>How the notes were obtained at load (ADR-0020). Anything other than None or CreatedEmpty is told to the user.</summary>
public enum NotesRecovery
{
    /// <summary>The file was read as it is.</summary>
    None,

    /// <summary>There was no file yet: no notes.</summary>
    CreatedEmpty,

    /// <summary>The file was damaged and kept aside as notes.corrupt.json; the last good copy was used instead.</summary>
    RestoredFromBackup,

    /// <summary>The file was damaged, kept aside as notes.corrupt.json, and there was no good copy: no notes.</summary>
    DamagedNoBackup,

    /// <summary>The file comes from a newer Lunar: its notes are shown if they can be read, and nothing can be changed.</summary>
    NewerVersionReadOnly
}

/// <summary>What one load returned.</summary>
public sealed record NotesLoadResult(IReadOnlyList<Note> Notes, NotesRecovery Recovery);

/// <summary>
/// Stores all notes together in one local file (ADR-0020). No path, file or JSON type crosses this port. Expected failures are
/// <see cref="Result"/> failures, cancellation is <see cref="ErrorCode.Cancelled"/>, and nothing logged ever carries note text.
/// </summary>
public interface INotesRepository
{
    /// <summary>
    /// Reads the notes. A missing, damaged or newer file is a successful load with its <see cref="NotesRecovery"/>; only a file
    /// that cannot be read at all (locked, access denied) fails, with <see cref="ErrorCode.IoFailure"/> or
    /// <see cref="ErrorCode.AccessDenied"/>, and then nothing may be saved over it.
    /// </summary>
    Task<Result<NotesLoadResult>> LoadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the stored notes atomically: the old file is either fully kept or fully replaced, and the previous good copy
    /// becomes the backup. Fails with <see cref="ErrorCode.InvalidInput"/> when the file would exceed
    /// <see cref="NoteRules.MaxFileBytes"/>, with <see cref="ErrorCode.Unsupported"/> when the file is from a newer Lunar, and
    /// with <see cref="ErrorCode.IoFailure"/> or <see cref="ErrorCode.AccessDenied"/> when the disk refuses.
    /// </summary>
    Task<Result> SaveAsync(IReadOnlyList<Note> notes, CancellationToken cancellationToken);
}
