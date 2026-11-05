namespace Lunar.Conversion.Output;

/// <summary>The real disk.</summary>
internal sealed class PhysicalOutputFileSystem : IOutputFileSystem
{
    private const int BufferBytes = 81_920;

    public async Task CopyToNewFileAsync(string source, string destination, CancellationToken ct)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferBytes, FileOptions.SequentialScan | FileOptions.Asynchronous);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferBytes, FileOptions.Asynchronous);
        await input.CopyToAsync(output, BufferBytes, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    public void MoveWithoutOverwrite(string from, string to) => File.Move(from, to, overwrite: false);

    public void ReplaceExisting(string replacement, string existing) =>
        File.Replace(replacement, existing, destinationBackupFileName: null, ignoreMetadataErrors: true);

    public bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool IsLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // a name that cannot be inspected is not replaced
        }
    }

    public long? GetFreeBytes(string directory)
    {
        try
        {
            var root = Path.GetPathRoot(directory);
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
