namespace Lunar.Conversion.Tests.Support;

/// <summary>A scratch folder under the temp directory with an "in" and an "out" folder, removed when the test ends.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Root = Path.Combine(Path.GetTempPath(), "lunar-bt20-" + Guid.NewGuid().ToString("N"));
        In = Path.Combine(Root, "in");
        Out = Path.Combine(Root, "out");
        Directory.CreateDirectory(In);
        Directory.CreateDirectory(Out);
    }

    public string Root { get; }

    public string In { get; }

    public string Out { get; }

    public string Write(string name, byte[] bytes, string? folder = null)
    {
        var path = Path.Combine(folder ?? In, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Every file under the root with its size and last write time, to prove a call changed nothing.</summary>
    public string Snapshot() => string.Join('\n', Directory
        .EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories)
        .OrderBy(p => p, StringComparer.Ordinal)
        .Select(p => File.Exists(p) ? $"{p}|{new FileInfo(p).Length}|{File.GetLastWriteTimeUtc(p).Ticks}|{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)))}" : p + "|dir"));

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A scratch folder left behind in %TEMP% does no harm.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
