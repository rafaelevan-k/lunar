using System.Text;
using System.Text.RegularExpressions;
using Lunar.Application.Abstractions.Audio;
using Lunar.Domain.Conversion;
using Lunar.Domain.Recording;
using Lunar.Domain.Shared;

namespace Lunar.Infrastructure.Recording;

/// <summary>
/// The files of a recording (ADR-0022). The audio is written to <c>~&lt;32 hex&gt;.lunar-part</c> in the destination folder and renamed once
/// complete, never over anything. A small journal beside the settings names the part file while a recording runs, so that an unexpected stop
/// can be repaired at the next start. Paths reach neither messages nor logs.
/// </summary>
public sealed partial class RecordingFileStore : IRecordingFiles
{
    internal const string JournalName = "recording.journal";
    private const string Extension = ".mp3";
    private const int MaxNameAttempts = 999;
    private const int MaxJournalBytes = 8 * 1024;
    private const int HeaderBytes = 64 * 1024;
    private const int FramesToProve = 4;

    private static readonly int[] Kilobits = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];
    private static readonly int[] Hertz = [44100, 48000, 32000, 0];
    private readonly string _journal;

    public RecordingFileStore(string? appDataFolder = null, string? defaultFolder = null)
    {
        var root = appDataFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lunar");
        _journal = Path.Combine(root, JournalName);
        DefaultFolder = defaultFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Lunar Recordings");
    }

    public string DefaultFolder { get; }

    public Result ValidateFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder) || folder.StartsWith(@"\\", StringComparison.Ordinal)
            || folder.IndexOf(':', 2) >= 0 || folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return Result.Failure(new OperationError(ErrorCode.InvalidInput, "Choose a folder on this PC."));
        try
        {
            if (new DirectoryInfo(folder).LinkTarget is not null)
                return Result.Failure(new OperationError(ErrorCode.InvalidInput, "That folder is a link. Choose an ordinary folder."));
            return Result.Success();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return Result.Failure(new OperationError(ErrorCode.InvalidInput, "That folder cannot be used."));
        }
    }

    public Result<long> GetFreeBytes(string folder)
    {
        try
        {
            return Result.Success(new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return Result.Failure<long>(new OperationError(ErrorCode.IoFailure, "The free space could not be read."));
        }
    }

    public async Task<Result<string>> ReservePartAsync(string folder, string baseName, CancellationToken ct)
    {
        var valid = ValidateFolder(folder);
        if (valid.IsFailure)
            return Result.Failure<string>(valid.Error!);
        try
        {
            Directory.CreateDirectory(folder);
            var part = Path.Combine(folder, $"~{Guid.NewGuid():N}.lunar-part");
            Directory.CreateDirectory(Path.GetDirectoryName(_journal)!);
            await File.WriteAllTextAsync(_journal, part + "\n" + baseName, Encoding.UTF8, ct).ConfigureAwait(false);
            return Result.Success(part);
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Result.Failure<string>(new OperationError(ErrorCode.IoFailure, "Lunar is not allowed to write to that folder."));
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return Result.Failure<string>(new OperationError(ErrorCode.IoFailure, "That folder cannot be written to."));
        }
    }

    public Task<Result<SavedRecording>> CommitAsync(string partPath, string baseName, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Task.FromResult(Result.Cancelled<SavedRecording>());
        return Task.Run(() => Commit(partPath, baseName), CancellationToken.None);
    }

    public Task AbandonAsync(string partPath, CancellationToken ct) => Task.Run(() =>
    {
        DeleteIfEmpty(partPath);
        ForgetJournal();
    }, CancellationToken.None);

    public Task<Result<RecoveredRecording?>> RecoverAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Task.FromResult(Result.Cancelled<RecoveredRecording?>());
        return Task.Run(Recover, CancellationToken.None);
    }

    /// <summary>True when the file starts with real MP3 frames. Reads only the start of the file.</summary>
    internal static bool HoldsMp3Audio(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bytes = new byte[Math.Min(HeaderBytes, stream.Length)];
        stream.ReadExactly(bytes);
        var i = bytes is [(byte)'I', (byte)'D', (byte)'3', _, _, _, _, _, _, _, ..]
            ? 10 + (((bytes[6] & 0x7F) << 21) | ((bytes[7] & 0x7F) << 14) | ((bytes[8] & 0x7F) << 7) | (bytes[9] & 0x7F))
            : 0;
        var frames = 0;
        while (i >= 0 && i + 4 <= bytes.Length && frames < FramesToProve)
        {
            if (bytes[i] != 0xFF || (bytes[i + 1] & 0xFE) != 0xFA)
                return false;
            var kilobits = Kilobits[bytes[i + 2] >> 4];
            var hertz = Hertz[(bytes[i + 2] >> 2) & 3];
            if (kilobits == 0 || hertz == 0)
                return false;
            i += (144 * kilobits * 1000 / hertz) + ((bytes[i + 2] >> 1) & 1);
            frames++;
        }

        return frames >= FramesToProve;
    }

    private Result<SavedRecording> Commit(string partPath, string baseName)
    {
        try
        {
            if (!File.Exists(partPath) || new FileInfo(partPath).Length == 0)
            {
                DeleteIfEmpty(partPath);
                ForgetJournal();
                return Result.Failure<SavedRecording>(new OperationError(ErrorCode.InvalidInput, "Nothing was recorded."));
            }

            if (!HoldsMp3Audio(partPath))
                return Result.Failure<SavedRecording>(new OperationError(ErrorCode.InvalidInput, "The recording could not be completed as an MP3 file."));

            var saved = Publish(partPath, baseName);
            ForgetJournal();
            return Result.Success(saved);
        }
        catch (UnauthorizedAccessException)
        {
            return Result.Failure<SavedRecording>(new OperationError(ErrorCode.IoFailure, "Lunar is not allowed to finish the file in that folder."));
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return Result.Failure<SavedRecording>(new OperationError(ErrorCode.IoFailure, "The recording could not be saved under its name."));
        }
    }

    private Result<RecoveredRecording?> Recover()
    {
        try
        {
            if (!File.Exists(_journal) || new FileInfo(_journal).Length > MaxJournalBytes)
            {
                ForgetJournal();
                return Result.Success<RecoveredRecording?>(null);
            }

            var lines = File.ReadAllLines(_journal, Encoding.UTF8);
            var part = lines.Length == 2 ? lines[0] : string.Empty;
            var baseName = lines.Length == 2 ? OutputNaming.SanitizeBaseName(lines[1]) : string.Empty;

            // The journal is only a hint from disk: nothing but a Lunar part file by a full path is ever touched.
            if (!Path.IsPathFullyQualified(part) || !PartName().IsMatch(Path.GetFileName(part)) || baseName.Length == 0 || !File.Exists(part))
            {
                ForgetJournal();
                return Result.Success<RecoveredRecording?>(null);
            }

            if (new FileInfo(part).Length == 0)
            {
                DeleteIfEmpty(part);
                ForgetJournal();
                return Result.Success<RecoveredRecording?>(null);
            }

            if (!HoldsMp3Audio(part))
            {
                ForgetJournal();
                return Result.Success<RecoveredRecording?>(new RecoveredRecording(part, RecoveryOutcome.KeptAsItIs));
            }

            var saved = Publish(part, RecordingRules.RecoveredBaseName(baseName));
            ForgetJournal();
            return Result.Success<RecoveredRecording?>(new RecoveredRecording(saved.Path, RecoveryOutcome.Recovered));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || IsExpected(ex))
        {
            return Result.Failure<RecoveredRecording?>(new OperationError(ErrorCode.IoFailure, "A recording left by an earlier run could not be repaired."));
        }
    }

    // A rename that cannot overwrite: a taken name simply moves on to the next number.
    private static SavedRecording Publish(string partPath, string baseName)
    {
        var folder = Path.GetDirectoryName(partPath)!;
        for (var counter = 1; counter <= MaxNameAttempts; counter++)
        {
            var name = (counter == 1 ? baseName : OutputNaming.WithCounter(baseName, counter)) + Extension;
            var target = Path.Combine(folder, name);
            if (File.Exists(target))
                continue;
            try
            {
                File.Move(partPath, target, overwrite: false);
                return new SavedRecording(target, name);
            }
            catch (IOException) when (File.Exists(target))
            {
                // Somebody took the name a moment ago.
            }
        }

        throw new IOException("No free name.");
    }

    private void ForgetJournal()
    {
        try
        {
            File.Delete(_journal);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || IsExpected(ex))
        {
            // At worst the next start finds nothing to repair at that path, or repairs a file that no longer exists: both are harmless.
        }
    }

    private static void DeleteIfEmpty(string path)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length == 0)
                File.Delete(path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || IsExpected(ex))
        {
            // An empty file left behind holds nothing; the user's audio is never at stake here.
        }
    }

    private static bool IsExpected(Exception ex) => ex is IOException or ArgumentException or NotSupportedException or System.Security.SecurityException;

    [GeneratedRegex("^~[0-9a-f]{32}\\.lunar-part$", RegexOptions.CultureInvariant)]
    private static partial Regex PartName();
}
