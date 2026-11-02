using System.Text.Json;
using System.Text.Json.Nodes;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Settings;
using Lunar.Application.Settings;
using Lunar.Application.Settings.Models;
using Lunar.Domain.Shared;

namespace Lunar.Infrastructure.Settings;

/// <summary>
/// Stores <see cref="LunarSettings"/> as <c>settings.json</c> in one folder (ADR-0011): size-capped, validated reads
/// with backup restore, atomic writes through a temporary file, and no overwrite of files from a newer Lunar.
/// File content never makes <see cref="LoadAsync"/> fail or throw; log events carry codes only, never values or paths.
/// </summary>
public sealed class JsonSettingsRepository : ISettingsRepository, IDisposable
{
    internal const string FileName = "settings.json";
    internal const string BackupName = "settings.json.bak";
    internal const string TempName = "settings.json.tmp";
    internal const string CorruptName = "settings.corrupt.json";
    internal const int MaxFileBytes = 64 * 1024;

    private const int MaxCorruptCopyBytes = 1024 * 1024;
    private const string Component = "Settings";

    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = 16 };

    private readonly string _directory;
    private readonly IDiagnosticLog? _log;
    private readonly SettingsMigration _migration;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonSettingsRepository(string? directory = null, IDiagnosticLog? log = null)
        : this(directory, log, new SettingsMigration())
    {
    }

    internal JsonSettingsRepository(string? directory, IDiagnosticLog? log, SettingsMigration migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        _directory = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory : directory;
        _log = log;
        _migration = migration;
    }

    /// <summary><c>%LocalAppData%\Lunar</c>, resolved by the OS.</summary>
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lunar");

    private string MainPath => Path.Combine(_directory, FileName);

    private string BackupPath => Path.Combine(_directory, BackupName);

    private string TempPath => Path.Combine(_directory, TempName);

    private string CorruptPath => Path.Combine(_directory, CorruptName);

    public async Task<Result<SettingsLoadResult>> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled<SettingsLoadResult>();
        }

        try
        {
            return await Task.Run(LoadCore, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled<SettingsLoadResult>();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Result> SaveAsync(LunarSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled();
        }

        try
        {
            return await Task.Run(() => SaveCore(settings), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private Result<SettingsLoadResult> LoadCore()
    {
        DeleteQuietly(TempPath);

        var main = Inspect(MainPath);
        switch (main.State)
        {
            case FileState.Ok:
                if (main.Migrated)
                {
                    // The upgraded format is persisted once; a failure here only means the next start migrates again.
                    var persisted = Write(main.Settings!, FileState.Ok);
                    if (persisted.IsFailure)
                        Log(DiagnosticLevel.Warning, "settings.migrate.save.failed", "Load", persisted.Error!.Code);
                }

                if (main.ValuesReset)
                    Log(DiagnosticLevel.Warning, "settings.load.values.reset", "Load");
                return Loaded(main.Settings!, main.ValuesReset ? SettingsRecovery.ValuesReset : SettingsRecovery.None);

            case FileState.Newer:
                Log(DiagnosticLevel.Warning, "settings.load.newer.version", "Load");
                return Loaded(LunarSettings.Defaults, SettingsRecovery.NewerVersionIgnored);

            case FileState.Unreadable:
                Log(DiagnosticLevel.Warning, "settings.load.failed", "Load", main.Error!.Code, main.ExceptionType);
                return Result.Failure<SettingsLoadResult>(main.Error);

            default:
                if (main.State == FileState.Invalid)
                    CopyToCorruptSlot();

                var backup = Inspect(BackupPath);
                if (backup.State == FileState.Ok)
                {
                    Log(DiagnosticLevel.Warning, "settings.load.restored", "Load");
                    return Loaded(backup.Settings!, SettingsRecovery.RestoredFromBackup);
                }

                if (main.State == FileState.Missing)
                    return Loaded(LunarSettings.Defaults, SettingsRecovery.CreatedDefaults);

                Log(DiagnosticLevel.Warning, "settings.load.reset", "Load");
                return Loaded(LunarSettings.Defaults, SettingsRecovery.ResetToDefaults);
        }
    }

    private Result SaveCore(LunarSettings settings)
    {
        var normalized = Sanitize(SettingsRules.Normalize(settings, out _), out _);

        var current = Inspect(MainPath);
        if (current.State == FileState.Newer)
        {
            return Result.Failure(new OperationError(
                ErrorCode.Unsupported, "The settings file was written by a newer version of Lunar and was not changed."));
        }

        if (current.State == FileState.Unreadable)
        {
            Log(DiagnosticLevel.Warning, "settings.save.failed", "Save", current.Error!.Code, current.ExceptionType);
            return Result.Failure(current.Error);
        }

        var written = Write(normalized, current.State);
        if (written.IsFailure)
            Log(DiagnosticLevel.Warning, "settings.save.failed", "Save", written.Error!.Code);
        return written;
    }

    /// <summary>
    /// Writes a temporary file, flushes it to disk and swaps it in. An existing valid file becomes the backup; an
    /// invalid one is overwritten without touching the last good backup.
    /// </summary>
    private Result Write(LunarSettings settings, FileState currentState)
    {
        var bytes = SettingsSerializer.Write(settings, _migration.Latest);
        try
        {
            Directory.CreateDirectory(_directory);
            using (var stream = new FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (currentState == FileState.Ok)
                File.Replace(TempPath, MainPath, BackupPath);
            else
                File.Move(TempPath, MainPath, overwrite: true);

            return Result.Success();
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(ErrorCode.AccessDenied, "The settings could not be saved because access was denied.");
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException)
        {
            return Failure(ErrorCode.IoFailure, "The settings could not be saved.");
        }
        finally
        {
            DeleteQuietly(TempPath);
        }

        static Result Failure(ErrorCode code, string message) => Result.Failure(new OperationError(code, message, retryable: true));
    }

    private FileInspection Inspect(string path)
    {
        byte[] bytes;
        try
        {
            if (!File.Exists(path))
                return FileInspection.Of(FileState.Missing);

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxFileBytes)
                return FileInspection.Of(FileState.Invalid);

            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return FileInspection.Of(FileState.Missing);
        }
        catch (UnauthorizedAccessException ex)
        {
            return FileInspection.Unreadable(ErrorCode.AccessDenied, ex);
        }
        catch (IOException ex)
        {
            return FileInspection.Unreadable(ErrorCode.IoFailure, ex);
        }

        // Editors such as Notepad may add a UTF-8 byte-order mark.
        var span = bytes.AsSpan();
        if (span.StartsWith("\uFEFF"u8))
            span = span[3..];

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(span, nodeOptions: null, ParseOptions) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return FileInspection.Of(FileState.Invalid);
        }

        if (root is null)
            return FileInspection.Of(FileState.Invalid);

        var outcome = _migration.Migrate(root);
        if (outcome == MigrationOutcome.Invalid)
            return FileInspection.Of(FileState.Invalid);
        if (outcome == MigrationOutcome.Newer)
            return FileInspection.Of(FileState.Newer);

        var settings = SettingsSerializer.Read(root, out var reset);
        settings = SettingsRules.Normalize(settings, out var changed);
        settings = Sanitize(settings, out var pathReset);
        return new FileInspection(FileState.Ok, settings, reset || changed || pathReset, outcome == MigrationOutcome.Migrated, null, null);
    }

    /// <summary>The output folder must be an absolute local path; anything else is reset to the default.</summary>
    private static LunarSettings Sanitize(LunarSettings settings, out bool reset)
    {
        var directory = settings.Conversion.OutputDirectory;
        if (directory is null || IsAbsoluteLocalPath(directory))
        {
            reset = false;
            return settings;
        }

        reset = true;
        return settings with { Conversion = settings.Conversion with { OutputDirectory = null } };
    }

    private static bool IsAbsoluteLocalPath(string path)
    {
        // Network shares and device paths (\\server\share, //server/share, \\?\) are not local output folders.
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            return false;

        try
        {
            return Path.IsPathFullyQualified(path) && path.IndexOfAny(Path.GetInvalidPathChars()) < 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Keeps the unusable file for inspection; one slot, overwritten, never larger than 1 MiB.</summary>
    private void CopyToCorruptSlot()
    {
        try
        {
            if (new FileInfo(MainPath).Length <= MaxCorruptCopyBytes)
                File.Copy(MainPath, CorruptPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log(DiagnosticLevel.Warning, "settings.corrupt.copy.failed", "Load", null, ex.GetType());
        }
    }

    private static Result<SettingsLoadResult> Loaded(LunarSettings settings, SettingsRecovery recovery) =>
        Result.Success(new SettingsLoadResult(settings, recovery));

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary file is replaced by the next write.
        }
    }

    private void Log(DiagnosticLevel level, string eventId, string operation, ErrorCode? error = null, Type? exceptionType = null) =>
        _log?.Write(new DiagnosticEvent(level, eventId, Component, operation, error, null, exceptionType));

    private enum FileState
    {
        Missing,
        Invalid,
        Newer,
        Unreadable,
        Ok
    }

    private sealed record FileInspection(
        FileState State,
        LunarSettings? Settings,
        bool ValuesReset,
        bool Migrated,
        OperationError? Error,
        Type? ExceptionType)
    {
        public static FileInspection Of(FileState state) => new(state, null, false, false, null, null);

        public static FileInspection Unreadable(ErrorCode code, Exception ex) => new(
            FileState.Unreadable, null, false, false,
            new OperationError(code, code == ErrorCode.AccessDenied
                ? "The settings file could not be read because access was denied."
                : "The settings file could not be read.", retryable: true),
            ex.GetType());
    }
}
