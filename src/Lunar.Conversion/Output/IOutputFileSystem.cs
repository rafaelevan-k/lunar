namespace Lunar.Conversion.Output;

/// <summary>
/// The few file operations the output transaction performs on the user's destination folder, behind a seam so tests can make any
/// one of them fail the way a full disk, a locked file or a vanished folder would. Every method acts on exactly the paths given.
/// </summary>
internal interface IOutputFileSystem
{
    /// <summary>
    /// Copies <paramref name="source"/> to a new file at <paramref name="destination"/> (failing if it exists), then flushes it to the
    /// disk so a following rename cannot publish a file whose data is still only in memory.
    /// </summary>
    Task CopyToNewFileAsync(string source, string destination, CancellationToken ct);

    /// <summary>Renames <paramref name="from"/> to <paramref name="to"/> within one volume, failing with an <see cref="IOException"/> if <paramref name="to"/> exists.</summary>
    void MoveWithoutOverwrite(string from, string to);

    /// <summary>Atomically replaces the existing file <paramref name="existing"/> with <paramref name="replacement"/>.</summary>
    void ReplaceExisting(string replacement, string existing);

    /// <summary>Deletes a file; false when it could not (a missing file counts as deleted).</summary>
    bool TryDelete(string path);

    /// <summary>True when a file or a folder is at the path.</summary>
    bool Exists(string path);

    /// <summary>True when the file or folder at the path is a symbolic link or junction (a name that points somewhere else). Never replaced.</summary>
    bool IsLink(string path);

    /// <summary>True when a folder is at the path.</summary>
    bool DirectoryExists(string path);

    /// <summary>Free bytes on the volume of <paramref name="directory"/> for this user, or null when it cannot be told.</summary>
    long? GetFreeBytes(string directory);
}
