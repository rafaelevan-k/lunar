using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Notes;
using Lunar.Domain.Notes;
using Lunar.Domain.Shared;
using Lunar.Infrastructure.Settings;

namespace Lunar.Infrastructure.Notes;

/// <summary>
/// Stores every note in <c>notes.json</c> next to <c>settings.json</c> (ADR-0020), written the same way as the settings:
/// <list type="bullet">
/// <item>atomic writes through a temporary file that is flushed to disk and swapped in;</item>
/// <item><c>notes.json.bak</c> holds the previous good copy;</item>
/// <item>a damaged file is moved aside to <c>notes.corrupt.json</c> (never deleted, never copied partially) and the backup is used;</item>
/// <item>a file from a newer Lunar is read if possible and never written.</item>
/// </list>
/// Calls are serialized. File content never makes <see cref="LoadAsync"/> throw, and log events carry codes only, never text or paths.
/// </summary>
public sealed class JsonNotesRepository : INotesRepository, IDisposable
{
    internal const string FileName = "notes.json";
    internal const string BackupName = "notes.json.bak";
    internal const string TempName = "notes.json.tmp";
    internal const string CorruptName = "notes.corrupt.json";
    internal const int SchemaVersion = 1;

    private const string Component = "Notes";

    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = 8 };

    private static readonly JsonWriterOptions WriteOptions = new()
    {
        Indented = true,
        // A local file, never HTML: keep letters of every language readable instead of \u escapes (and three times smaller).
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _directory;
    private readonly IDiagnosticLog? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="directory">The data folder; <c>%LocalAppData%\Lunar</c> when null.</param>
    public JsonNotesRepository(string? directory = null, IDiagnosticLog? log = null)
    {
        _directory = string.IsNullOrWhiteSpace(directory) ? JsonSettingsRepository.DefaultDirectory : directory;
        _log = log;
    }

    private string MainPath => Path.Combine(_directory, FileName);

    private string BackupPath => Path.Combine(_directory, BackupName);

    private string TempPath => Path.Combine(_directory, TempName);

    private string CorruptPath => Path.Combine(_directory, CorruptName);

    public async Task<Result<NotesLoadResult>> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled<NotesLoadResult>();
        }

        try
        {
            return await Task.Run(LoadCore, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled<NotesLoadResult>();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Result> SaveAsync(IReadOnlyList<Note> notes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notes);
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
            return await Task.Run(() => SaveCore(notes), cancellationToken).ConfigureAwait(false);
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

    // ---- load ---------------------------------------------------------------------------------------------------------

    private Result<NotesLoadResult> LoadCore()
    {
        DeleteQuietly(TempPath);

        var main = Inspect(MainPath);
        switch (main.State)
        {
            case FileState.Ok:
                return Loaded(main.Notes!, NotesRecovery.None);

            case FileState.Newer:
                Log(DiagnosticLevel.Warning, "notes.load.newer.version", "Load");
                return Loaded(main.Notes ?? [], NotesRecovery.NewerVersionReadOnly);

            case FileState.Unreadable:
                Log(DiagnosticLevel.Warning, "notes.load.failed", "Load", main.Error!.Code, main.ExceptionType);
                return Result.Failure<NotesLoadResult>(main.Error);
        }

        if (main.State == FileState.Invalid)
        {
            var kept = MoveToCorruptSlot();
            if (kept.IsFailure)
                return Result.Failure<NotesLoadResult>(kept.Error!); // never write over notes that could not be kept
        }

        var backup = Inspect(BackupPath);
        switch (backup.State)
        {
            case FileState.Ok:
                Log(DiagnosticLevel.Warning, "notes.load.restored", "Load");
                return Loaded(backup.Notes!, NotesRecovery.RestoredFromBackup);

            case FileState.Unreadable:
                Log(DiagnosticLevel.Warning, "notes.load.failed", "Load", backup.Error!.Code, backup.ExceptionType);
                return Result.Failure<NotesLoadResult>(backup.Error);
        }

        if (main.State == FileState.Missing)
            return Loaded([], NotesRecovery.CreatedEmpty);

        Log(DiagnosticLevel.Warning, "notes.load.damaged", "Load");
        return Loaded([], NotesRecovery.DamagedNoBackup);
    }

    private FileInspection Inspect(string path)
    {
        byte[] bytes;
        try
        {
            if (!File.Exists(path))
                return FileInspection.Of(FileState.Missing);

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > NoteRules.MaxFileBytes + 3)
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

        var span = bytes.AsSpan();
        if (span.StartsWith("﻿"u8))
            span = span[3..];

        try
        {
            using var document = JsonDocument.Parse(bytes.AsMemory(bytes.Length - span.Length), ParseOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schemaVersion", out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var schema)
                || schema < 1)
            {
                return FileInspection.Of(FileState.Invalid);
            }

            var notes = ReadNotes(root);
            if (schema > SchemaVersion)
                return new FileInspection(FileState.Newer, notes, null, null); // shown if readable, never written
            return notes is null ? FileInspection.Of(FileState.Invalid) : new FileInspection(FileState.Ok, notes, null, null);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            return FileInspection.Of(FileState.Invalid);
        }
    }

    /// <summary>Every entry must be a valid note with a unique id; one bad entry makes the whole list unusable (and kept aside).</summary>
    private static List<Note>? ReadNotes(JsonElement root)
    {
        if (!root.TryGetProperty("notes", out var array) || array.ValueKind != JsonValueKind.Array
            || array.GetArrayLength() > NoteRules.MaxNotes)
        {
            return null;
        }

        var notes = new List<Note>(array.GetArrayLength());
        var ids = new HashSet<Guid>();
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !TryString(entry, "id", out var idText) || !Guid.TryParseExact(idText, "D", out var id)
                || !TryString(entry, "text", out var text)
                || !TryTime(entry, "created", out var created)
                || !TryTime(entry, "changed", out var changed)
                || !ids.Add(id))
            {
                return null;
            }

            var note = Note.From(id, text, created, changed); // a file edited by hand may use CR LF line breaks; they are read as LF
            if (note.IsFailure)
                return null;
            notes.Add(note.Value);
        }

        return notes;
    }

    private static bool TryString(JsonElement entry, string name, out string value)
    {
        value = string.Empty;
        if (!entry.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString()!;
        return true;
    }

    private static bool TryTime(JsonElement entry, string name, out DateTimeOffset value)
    {
        value = default;
        return TryString(entry, name, out var text)
            && DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    // ---- save ---------------------------------------------------------------------------------------------------------

    private Result SaveCore(IReadOnlyList<Note> notes)
    {
        if (notes.Count > NoteRules.MaxNotes)
            return Result.Failure(NoteRules.TooMany());

        var current = Inspect(MainPath);
        if (current.State == FileState.Newer)
            return Result.Failure(new OperationError(ErrorCode.Unsupported,
                "The notes file was written by a newer version of Lunar and was not changed."));
        if (current.State == FileState.Unreadable)
        {
            Log(DiagnosticLevel.Warning, "notes.save.failed", "Save", current.Error!.Code, current.ExceptionType);
            return Result.Failure(current.Error);
        }

        if (current.State == FileState.Invalid)
        {
            // Damaged since it was read (edited outside Lunar): keep it before writing.
            var kept = MoveToCorruptSlot();
            if (kept.IsFailure)
                return kept;
        }

        var bytes = Serialize(notes);
        if (bytes.Length > NoteRules.MaxFileBytes)
            return Result.Failure(NoteRules.TooLarge());

        try
        {
            Directory.CreateDirectory(_directory);
            using (var stream = new FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (current.State == FileState.Ok)
                File.Replace(TempPath, MainPath, BackupPath);
            else
                File.Move(TempPath, MainPath, overwrite: true);
            return Result.Success();
        }
        catch (UnauthorizedAccessException ex)
        {
            Log(DiagnosticLevel.Warning, "notes.save.failed", "Save", ErrorCode.AccessDenied, ex.GetType());
            return Result.Failure(new OperationError(ErrorCode.AccessDenied, "Your notes could not be saved because access was denied.", retryable: true));
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException)
        {
            Log(DiagnosticLevel.Warning, "notes.save.failed", "Save", ErrorCode.IoFailure, ex.GetType());
            return Result.Failure(new OperationError(ErrorCode.IoFailure, "Your notes could not be saved.", retryable: true));
        }
        finally
        {
            DeleteQuietly(TempPath);
        }
    }

    internal static byte[] Serialize(IReadOnlyList<Note> notes)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriteOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteStartArray("notes");
            foreach (var note in notes)
            {
                writer.WriteStartObject();
                writer.WriteString("id", note.Id.ToString("D"));
                writer.WriteString("text", note.Text);
                writer.WriteString("created", note.Created.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteString("changed", note.Changed.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    /// <summary>Moves the damaged file aside in one piece; one slot, the newest damaged file wins.</summary>
    private Result MoveToCorruptSlot()
    {
        try
        {
            File.Move(MainPath, CorruptPath, overwrite: true);
            Log(DiagnosticLevel.Warning, "notes.corrupt.kept", "Load");
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log(DiagnosticLevel.Warning, "notes.corrupt.move.failed", "Load", ErrorCode.IoFailure, ex.GetType());
            return Result.Failure(new OperationError(ErrorCode.IoFailure,
                "Your notes file is damaged and could not be set aside, so Lunar did not change it.", retryable: true));
        }
    }

    private static Result<NotesLoadResult> Loaded(IReadOnlyList<Note> notes, NotesRecovery recovery) =>
        Result.Success(new NotesLoadResult(notes, recovery));

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

    private sealed record FileInspection(FileState State, List<Note>? Notes, OperationError? Error, Type? ExceptionType)
    {
        public static FileInspection Of(FileState state) => new(state, null, null, null);

        public static FileInspection Unreadable(ErrorCode code, Exception ex) => new(
            FileState.Unreadable, null,
            new OperationError(code, code == ErrorCode.AccessDenied
                ? "Your notes file could not be read because access was denied."
                : "Your notes file could not be read.", retryable: true),
            ex.GetType());
    }
}
