using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Notes;
using Lunar.Domain.Notes;
using Lunar.Domain.Shared;

namespace Lunar.Application.Notes;

/// <summary>
/// The one writer of the user's notes (ADR-0020): list, create, update, delete and restore (for Undo). Every change is applied to the
/// latest list, checked against <see cref="NoteRules"/>, saved, and only then made current and announced, so a failed save changes
/// nothing and nobody sees a note that is not on disk. Calls are serialized; <see cref="Changed"/> is raised on the thread that finished
/// the save, and a handler must not wait for another call. Log events carry fixed names and codes only, never note text.
/// </summary>
public sealed class NotesService : IDisposable
{
    private const string Component = "Notes";

    private readonly INotesRepository _repository;
    private readonly TimeProvider _time;
    private readonly Func<Guid> _newId;
    private readonly IDiagnosticLog? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile IReadOnlyList<Note> _notes = [];
    private volatile bool _loaded;
    private NotesRecovery _recovery = NotesRecovery.CreatedEmpty;

    public NotesService(INotesRepository repository, TimeProvider? time = null, IDiagnosticLog? log = null, Func<Guid>? newId = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
        _time = time ?? TimeProvider.System;
        _log = log;
        _newId = newId ?? Guid.NewGuid;
    }

    /// <summary>Raised after a change has been saved.</summary>
    public event EventHandler? Changed;

    /// <summary>The saved notes, most recently changed first; empty until the first successful <see cref="LoadAsync"/>.</summary>
    public IReadOnlyList<Note> Notes => _notes;

    public bool IsLoaded => _loaded;

    /// <summary>How the notes were obtained at the first load.</summary>
    public NotesRecovery Recovery => _recovery;

    /// <summary>True when the file comes from a newer Lunar: notes are shown and every change is refused.</summary>
    public bool IsReadOnly => _recovery == NotesRecovery.NewerVersionReadOnly;

    /// <summary>
    /// Reads the file once; later calls return what was read. Fails only when the file cannot be read at all (then nothing is shown,
    /// every change is refused, and a later call tries again) or on cancellation.
    /// </summary>
    public async Task<Result<IReadOnlyList<Note>>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!await EnterAsync(cancellationToken).ConfigureAwait(false))
            return Result.Cancelled<IReadOnlyList<Note>>();
        try
        {
            var loaded = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            return loaded.IsSuccess ? Result.Success(_notes) : Result.Failure<IReadOnlyList<Note>>(loaded.Error!);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Adds a note. Fails with <see cref="ErrorCode.InvalidInput"/> at <see cref="NoteRules.MaxNotes"/> or for a text too long.</summary>
    public Task<Result<Note>> CreateAsync(string? text, CancellationToken cancellationToken) =>
        ChangeAsync("Create", notes =>
        {
            if (notes.Count >= NoteRules.MaxNotes)
                return Result.Failure<(List<Note>, Note)>(NoteRules.TooMany());
            var created = Note.Create(_newId(), text, _time.GetUtcNow());
            if (created.IsFailure)
                return Result.Failure<(List<Note>, Note)>(created.Error!);
            return Result.Success((new List<Note>(notes) { created.Value }, created.Value));
        }, cancellationToken);

    /// <summary>
    /// Replaces a note's text. The same text again changes nothing. Fails with <see cref="ErrorCode.InvalidInput"/> when the note no
    /// longer exists or the text is too long.
    /// </summary>
    public Task<Result<Note>> UpdateAsync(Guid id, string? text, CancellationToken cancellationToken) =>
        ChangeAsync("Update", notes =>
        {
            var index = IndexOf(notes, id);
            if (index < 0)
                return Result.Failure<(List<Note>, Note)>(Missing());
            if (notes[index].Text == NoteRules.Clean(text))
                return Result.Success((notes, notes[index]));
            var updated = notes[index].WithText(text, _time.GetUtcNow());
            if (updated.IsFailure)
                return Result.Failure<(List<Note>, Note)>(updated.Error!);
            var next = new List<Note>(notes) { [index] = updated.Value };
            return Result.Success((next, updated.Value));
        }, cancellationToken);

    /// <summary>Removes a note and returns it, so the caller can offer Undo with <see cref="RestoreAsync"/>.</summary>
    public Task<Result<Note>> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        ChangeAsync("Delete", notes =>
        {
            var index = IndexOf(notes, id);
            if (index < 0)
                return Result.Failure<(List<Note>, Note)>(Missing());
            var next = new List<Note>(notes);
            next.RemoveAt(index);
            return Result.Success((next, notes[index]));
        }, cancellationToken);

    /// <summary>Puts a deleted note back as it was (Undo). A note with the same id that exists already is left as it is.</summary>
    public Task<Result<Note>> RestoreAsync(Note note, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(note);
        return ChangeAsync("Restore", notes =>
        {
            var index = IndexOf(notes, note.Id);
            if (index >= 0)
                return Result.Success((notes, notes[index]));
            if (notes.Count >= NoteRules.MaxNotes)
                return Result.Failure<(List<Note>, Note)>(NoteRules.TooMany());
            return Result.Success((new List<Note>(notes) { note }, note));
        }, cancellationToken);
    }

    public void Dispose() => _gate.Dispose();

    private async Task<Result<Note>> ChangeAsync(
        string operation, Func<List<Note>, Result<(List<Note> Next, Note Note)>> change, CancellationToken cancellationToken)
    {
        if (!await EnterAsync(cancellationToken).ConfigureAwait(false))
            return Result.Cancelled<Note>();
        try
        {
            var loaded = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            if (loaded.IsFailure)
                return Result.Failure<Note>(loaded.Error!);
            if (IsReadOnly)
                return Result.Failure<Note>(new OperationError(ErrorCode.Unsupported,
                    "These notes were saved by a newer version of Lunar and cannot be changed here."));

            var current = new List<Note>(_notes);
            var planned = change(current);
            if (planned.IsFailure)
                return Result.Failure<Note>(planned.Error!);
            var (next, note) = planned.Value;
            if (ReferenceEquals(next, current))
                return Result.Success(note); // nothing to write, nobody to tell

            var sorted = Sort(next);
            var saved = await _repository.SaveAsync(sorted, cancellationToken).ConfigureAwait(false);
            if (saved.IsFailure)
            {
                if (!saved.IsCancelled)
                    _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "notes.save.failed", Component, operation, saved.Error!.Code));
                return Result.Failure<Note>(saved.Error!);
            }

            _notes = sorted;
            Changed?.Invoke(this, EventArgs.Empty);
            return Result.Success(note);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Result> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
            return Result.Success();

        var loaded = await _repository.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
            return Result.Failure(loaded.Error!);

        _notes = Sort(loaded.Value.Notes);
        _recovery = loaded.Value.Recovery;
        _loaded = true;
        return Result.Success();
    }

    private async Task<bool> EnterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Most recently changed first; the id breaks ties so the order never jumps.</summary>
    private static IReadOnlyList<Note> Sort(IEnumerable<Note> notes) =>
        notes.OrderByDescending(n => n.Changed).ThenBy(n => n.Id).ToArray();

    private static int IndexOf(List<Note> notes, Guid id) => notes.FindIndex(n => n.Id == id);

    private static OperationError Missing() => new(ErrorCode.InvalidInput, "That note no longer exists.");
}
