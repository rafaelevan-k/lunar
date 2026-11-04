using Lunar.Conversion.Output;

namespace Lunar.Conversion.Tests.Support;

/// <summary>The real disk with hooks that can fail or interfere with any operation, to reproduce a full disk, a vanished folder or a race.</summary>
internal sealed class FaultingFileSystem : IOutputFileSystem
{
    private readonly PhysicalOutputFileSystem _real = new();
    private int _copies;
    private int _moves;
    private int _replaces;

    /// <summary>Called before the n-th copy (from 1) with the source and the destination; may throw.</summary>
    public Action<int, string, string>? BeforeCopy { get; set; }

    /// <summary>Called before the n-th rename (from 1) with the two paths; may throw or make the name taken.</summary>
    public Action<int, string, string>? BeforeMove { get; set; }

    public Action<int, string, string>? BeforeReplace { get; set; }

    /// <summary>When set, deleting a path can be made to fail.</summary>
    public Func<string, bool>? DeleteFails { get; set; }

    public long? FreeBytes { get; set; }

    public bool UseRealFreeBytes { get; set; } = true;

    public int Copies => Volatile.Read(ref _copies);

    public int Moves => Volatile.Read(ref _moves);

    public int Replaces => Volatile.Read(ref _replaces);

    public Task CopyToNewFileAsync(string source, string destination, CancellationToken ct)
    {
        BeforeCopy?.Invoke(Interlocked.Increment(ref _copies), source, destination);
        return _real.CopyToNewFileAsync(source, destination, ct);
    }

    public void MoveWithoutOverwrite(string from, string to)
    {
        BeforeMove?.Invoke(Interlocked.Increment(ref _moves), from, to);
        _real.MoveWithoutOverwrite(from, to);
    }

    public void ReplaceExisting(string replacement, string existing)
    {
        BeforeReplace?.Invoke(Interlocked.Increment(ref _replaces), replacement, existing);
        _real.ReplaceExisting(replacement, existing);
    }

    public bool TryDelete(string path) => DeleteFails?.Invoke(path) == true ? false : _real.TryDelete(path);

    public bool Exists(string path) => _real.Exists(path);

    public bool DirectoryExists(string path) => _real.DirectoryExists(path);

    public long? GetFreeBytes(string directory) => UseRealFreeBytes ? _real.GetFreeBytes(directory) : FreeBytes;

    /// <summary>What Windows reports for a full disk (ERROR_DISK_FULL as an HRESULT).</summary>
    public static IOException DiskFull() => new("There is not enough space on the disk.", unchecked((int)0x80070070));
}
