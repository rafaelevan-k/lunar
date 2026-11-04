using System.ComponentModel;
using System.Diagnostics;
using Lunar.Application.Abstractions.Conversion;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Domain.Shared;

namespace Lunar.Windows.Environment;

/// <summary>
/// Shows a result in Windows Explorer (FE-22): a file selected in its folder, a folder opened. Explorer is started by its full path
/// under the Windows folder (never found through PATH) and the path is passed as one quoted argument after checks: it must be a full
/// local path (no network share, no device path), free of characters Windows does not allow in a path (so it cannot carry a quote or
/// another switch), and it must exist now. The file itself is never opened or run. A failure to start Explorer is a failed
/// <see cref="Result"/> with a message that holds no path.
/// </summary>
public sealed class WindowsOutputRevealer : IOutputRevealer
{
    private readonly Action<ProcessStartInfo> _start;
    private readonly IDiagnosticLog? _log;

    public WindowsOutputRevealer(IDiagnosticLog? log = null)
        : this(StartProcess, log)
    {
    }

    internal WindowsOutputRevealer(Action<ProcessStartInfo> start, IDiagnosticLog? log = null)
    {
        _start = start;
        _log = log;
    }

    public Task<Result> RevealAsync(string path, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Task.FromResult(Result.Cancelled());

        if (!IsUsablePath(path))
            return Task.FromResult(Result.Failure(new OperationError(ErrorCode.InvalidInput, "Lunar can only show a file or folder on this PC.")));

        var isFile = File.Exists(path);
        if (!isFile && !Directory.Exists(path))
            return Task.FromResult(Result.Failure(new OperationError(ErrorCode.InvalidInput, "That file or folder is no longer there.")));

        var explorer = Path.Combine(global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.Windows), "explorer.exe");
        var info = new ProcessStartInfo(explorer)
        {
            UseShellExecute = false,
            Arguments = isFile ? $"/select,\"{path}\"" : $"\"{path}\""
        };

        try
        {
            _start(info);
            return Task.FromResult(Result.Success());
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or PlatformNotSupportedException
            or UnauthorizedAccessException or NotSupportedException or ObjectDisposedException or ArgumentException)
        {
            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "output.reveal.failed", "Conversion", "Reveal", ErrorCode.IoFailure, ExceptionType: ex.GetType()));
            return Task.FromResult(Result.Failure(new OperationError(ErrorCode.IoFailure, "Windows Explorer could not be opened.", retryable: true)));
        }
    }

    // A full path on a local drive: not a share ("\\server\x"), not a device or long-path prefix ("\\?\", "\\.\"), nothing Windows forbids.
    internal static bool IsUsablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32_000)
            return false;
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            return false;
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || path.Contains('*') || path.Contains('?'))
            return false;
        return Path.IsPathFullyQualified(path) && path.Length >= 3 && path[1] == ':';
    }

    private static void StartProcess(ProcessStartInfo info)
    {
        using var process = Process.Start(info);
    }
}
