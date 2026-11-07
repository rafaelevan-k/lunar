using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Notes;
using Lunar.Application.Notes;
using Lunar.Desktop.Resources;
using Lunar.Desktop.Shell;
using Lunar.Domain.Notes;
using Lunar.Domain.Shared;

namespace Lunar.Desktop.Features.Notes;

/// <summary>One row of the notes list: its title (or "Empty note"), when it last changed, and what opening or deleting it does.</summary>
public sealed record NoteListItem(Guid Id, string Title, string ChangedText, string AutomationName, string DeleteName, ICommand OpenCommand, ICommand DeleteCommand);

/// <summary>
/// The Notes page (FE-31, ADR-0020). Two views share it: the list (newest first, no limit, it scrolls) and the editor of one note.
/// <list type="bullet">
/// <item>The editor saves on its own about a second after typing stops, and again when the note is closed or the island collapses.
/// A note left empty is removed when it is closed.</item>
/// <item>Delete is immediate and offers Undo for a few seconds.</item>
/// <item>Every failure is said in words; a note whose save failed stays open so nothing typed is lost.</item>
/// </list>
/// It only talks to <see cref="NotesService"/>; note text is never logged. Create and use it on the UI thread; awaited calls resume there.
/// </summary>
public sealed class NotesPageViewModel : INotifyPropertyChanged, IDisposable
{
    private const string Component = "NotesPage";
    internal static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan UndoTime = TimeSpan.FromSeconds(6);

    private readonly NotesService _notes;
    private readonly Action<Action> _post;
    private readonly IOneShotTimer _saveTimer;
    private readonly IOneShotTimer _undoTimer;
    private readonly TimeProvider _time;
    private readonly IDiagnosticLog? _log;
    private readonly CancellationTokenSource _lifetime = new();

    private IReadOnlyList<NoteListItem> _items = [];
    private bool _loaded;
    private bool _loading;
    private string? _problem;
    private bool _noticeDismissed;
    private Guid? _editing;
    private string _editorText = string.Empty;
    private string _savedText = string.Empty;
    private SaveState _saveState = SaveState.Saved;
    private Task _saving = Task.CompletedTask;
    private Note? _deleted;
    private bool _disposed;

    internal NotesPageViewModel(
        NotesService notes, Action<Action> post, IOneShotTimer saveTimer, IOneShotTimer undoTimer, TimeProvider? time = null, IDiagnosticLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(saveTimer);
        ArgumentNullException.ThrowIfNull(undoTimer);
        _notes = notes;
        _post = post;
        _saveTimer = saveTimer;
        _undoTimer = undoTimer;
        _time = time ?? TimeProvider.System;
        _log = log;

        var lifetime = _lifetime.Token;
        NewNoteCommand = new AsyncRelayCommand(NewNoteAsync, () => _loaded && !IsReadOnly && !IsEditing, OnFault, lifetime);
        CloseEditorCommand = new AsyncRelayCommand(_ => CloseEditorAsync(), () => IsEditing, OnFault, lifetime);
        DeleteCurrentCommand = new AsyncRelayCommand(DeleteCurrentAsync, () => IsEditing && !IsReadOnly, OnFault, lifetime);
        UndoCommand = new AsyncRelayCommand(UndoAsync, () => _deleted is not null, OnFault, lifetime);
        RetryCommand = new AsyncRelayCommand(LoadAsync, () => !_loaded && !_loading, OnFault, lifetime);
        DismissNoticeCommand = new RelayCommand(() =>
        {
            _noticeDismissed = true;
            Changed();
        });
        _notes.Changed += OnNotesChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ---- what the page shows --------------------------------------------------------------------------------------------

    public IReadOnlyList<NoteListItem> Items => _items;

    public bool IsLoading => _loading;

    /// <summary>The list is shown: loaded and no note is open.</summary>
    public bool IsListShown => _loaded && !IsEditing;

    /// <summary>Loaded, no notes and no note open: the "no notes yet" words are shown.</summary>
    public bool IsEmpty => IsListShown && _items.Count == 0;

    public bool HasItems => IsListShown && _items.Count > 0;

    public bool IsEditing => _editing is not null;

    /// <summary>The notes come from a newer Lunar: they can be read, not changed.</summary>
    public bool IsReadOnly => _notes.IsReadOnly;

    public bool CanEdit => IsEditing && !IsReadOnly;

    /// <summary>The open note's text. Typing here saves on its own after a short pause.</summary>
    public string EditorText
    {
        get => _editorText;
        set
        {
            value = NoteRules.Clean(value);
            if (_editorText == value || !IsEditing || IsReadOnly)
                return;
            _editorText = value;
            _saveState = _editorText == _savedText ? SaveState.Saved : SaveState.Pending;
            if (_saveState == SaveState.Pending)
                _saveTimer.Schedule(SaveDelay, () => _ = SaveNowAsync());
            Raise(nameof(EditorText));
            Raise(nameof(SaveStatus));
        }
    }

    public int MaxLength => NoteRules.MaxTextLength;

    /// <summary>"Saved", "Saving…", "Not saved" or nothing while typing.</summary>
    public string SaveStatus => !IsEditing || IsReadOnly
        ? string.Empty
        : _saveState switch
        {
            SaveState.Saving => Strings.Get("Notes.Saving"),
            SaveState.Failed => Strings.Get("Notes.NotSaved"),
            SaveState.Saved => Strings.Get("Notes.Saved"),
            _ => string.Empty
        };

    /// <summary>What went wrong, in words; null when nothing did.</summary>
    public string? Problem => _problem;

    public bool HasProblem => _problem is not null;

    /// <summary>How the notes were recovered at load (damaged file, newer Lunar); null when there is nothing to tell or it was dismissed.</summary>
    public string? Notice => _noticeDismissed || !_loaded
        ? null
        : _notes.Recovery switch
        {
            NotesRecovery.RestoredFromBackup => Strings.Get("Notes.RestoredFromBackup"),
            NotesRecovery.DamagedNoBackup => Strings.Get("Notes.DamagedNoBackup"),
            NotesRecovery.NewerVersionReadOnly => Strings.Get("Notes.NewerVersion"),
            _ => null
        };

    public bool HasNotice => Notice is not null;

    public bool CanUndo => _deleted is not null;

    public ICommand NewNoteCommand { get; }

    public ICommand CloseEditorCommand { get; }

    public ICommand DeleteCurrentCommand { get; }

    public ICommand UndoCommand { get; }

    public ICommand RetryCommand { get; }

    public ICommand DismissNoticeCommand { get; }

    // ---- lifetime -------------------------------------------------------------------------------------------------------

    /// <summary>Reads the notes. Call once; a failure is shown with Try again.</summary>
    public Task InitializeAsync(CancellationToken ct = default) => LoadAsync(ct);

    /// <summary>The island collapsed or Lunar is closing: the open note is saved now.</summary>
    public Task FlushAsync() => IsEditing && _saveState is SaveState.Pending or SaveState.Failed ? SaveNowAsync() : _saving;

    /// <summary>
    /// Lunar is closing: saves what is typed straight through the service, without coming back to the UI thread, so the caller may wait
    /// for it there. Completed when there is nothing to save.
    /// </summary>
    internal Task FlushForExitAsync() =>
        _editing is { } id && !IsReadOnly && _editorText != _savedText
            ? _notes.UpdateAsync(id, _editorText, CancellationToken.None)
            : Task.CompletedTask;

    internal void SetIslandOpen(bool open)
    {
        if (!open && !_disposed)
            _ = FlushAsync();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _notes.Changed -= OnNotesChanged;
        _saveTimer.Cancel();
        _undoTimer.Cancel();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    // ---- actions --------------------------------------------------------------------------------------------------------

    private async Task LoadAsync(CancellationToken ct)
    {
        if (_loaded || _loading || _disposed)
            return;
        _loading = true;
        _problem = null;
        Changed();

        var loaded = await _notes.LoadAsync(ct);
        _loading = false;
        if (loaded.IsSuccess)
        {
            _loaded = true;
            Rebuild(loaded.Value);
        }
        else if (!loaded.IsCancelled)
        {
            _problem = Strings.Get("Notes.CannotRead");
            Log(DiagnosticLevel.Warning, "notes.page.load.failed", "Load", loaded.Error!.Code);
        }

        Changed();
    }

    private async Task NewNoteAsync(CancellationToken ct)
    {
        var created = await _notes.CreateAsync(string.Empty, ct);
        if (Report(created))
            Open(created.Value);
    }

    private void Open(Note note)
    {
        _saveTimer.Cancel();
        _editing = note.Id;
        _editorText = note.Text;
        _savedText = note.Text;
        _saveState = SaveState.Saved;
        _problem = null;
        Changed();
    }

    private async Task OpenAsync(Guid id)
    {
        if (IsEditing && !await CloseEditorAsync())
            return; // the open note could not be saved; it stays open
        var note = _notes.Notes.FirstOrDefault(n => n.Id == id);
        if (note is not null)
            Open(note);
    }

    /// <summary>Saves and closes the open note; a note left empty is removed. False when the save failed (the note stays open).</summary>
    private async Task<bool> CloseEditorAsync()
    {
        if (_editing is not { } id)
            return true;
        // Typing during a save leaves it pending again, so flush until it is saved or has failed (a few rounds at most).
        for (var round = 0; round < 3 && _saveState is not SaveState.Saved; round++)
        {
            await FlushAsync();
            if (_saveState == SaveState.Failed)
                return false;
        }

        if (_editorText.Trim().Length == 0 && !IsReadOnly)
            await _notes.DeleteAsync(id, _lifetime.Token); // an empty note is not worth keeping; nothing to undo

        _editing = null;
        _editorText = string.Empty;
        _savedText = string.Empty;
        Rebuild(_notes.Notes);
        Changed();
        return true;
    }

    private async Task DeleteCurrentAsync(CancellationToken ct)
    {
        if (_editing is not { } id)
            return;
        _saveTimer.Cancel();
        await FlushAsync(); // Undo brings back what was typed
        var deleted = await _notes.DeleteAsync(id, ct);
        if (!Report(deleted))
            return;
        _editing = null;
        _editorText = string.Empty;
        OfferUndo(deleted.Value);
    }

    private async Task DeleteAsync(Guid id)
    {
        var deleted = await _notes.DeleteAsync(id, _lifetime.Token);
        if (Report(deleted))
            OfferUndo(deleted.Value);
    }

    private void OfferUndo(Note note)
    {
        _deleted = note.Text.Trim().Length == 0 ? null : note; // nothing worth undoing in an empty note
        if (_deleted is not null)
            _undoTimer.Schedule(UndoTime, () =>
            {
                _deleted = null;
                Changed();
            });
        Rebuild(_notes.Notes);
        Changed();
    }

    private async Task UndoAsync(CancellationToken ct)
    {
        if (_deleted is not { } note)
            return;
        _undoTimer.Cancel();
        _deleted = null;
        Report(await _notes.RestoreAsync(note, ct));
        Rebuild(_notes.Notes);
        Changed();
    }

    /// <summary>Starts a save after any save already running, so saves reach the service one at a time and in order.</summary>
    private Task SaveNowAsync()
    {
        _saveTimer.Cancel();
        _saving = SaveAfterAsync(_saving);
        return _saving;
    }

    // Never faults: an unexpected exception is reported through OnFault, so the timer's fire-and-forget call cannot lose one.
    private async Task SaveAfterAsync(Task previous)
    {
        try
        {
            await previous;
            if (_editing is not { } editing || _saveState is SaveState.Saved || IsReadOnly || _disposed)
                return;

            var text = _editorText;
            _saveState = SaveState.Saving;
            Raise(nameof(SaveStatus));
            var saved = await _notes.UpdateAsync(editing, text, _lifetime.Token);
            if (saved.IsSuccess)
            {
                _savedText = text;
                _problem = null;
                // Typing during the save leaves the note pending; the timer started by that typing saves it.
                _saveState = _editorText == text ? SaveState.Saved : SaveState.Pending;
            }
            else if (saved.IsCancelled)
            {
                _saveState = SaveState.Pending;
            }
            else
            {
                _saveState = SaveState.Failed;
                _problem = Strings.Format("Notes.SaveFailed", Reason(saved.Error!));
            }

            Changed();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _saveState = SaveState.Failed;
            OnFault(ex);
        }
        catch (OperationCanceledException)
        {
            _saveState = SaveState.Pending; // shutting down
        }
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------

    private bool Report<T>(Result<T> result)
    {
        if (result.IsSuccess)
        {
            _problem = null;
            return true;
        }

        if (!result.IsCancelled)
            _problem = Reason(result.Error!);
        Changed();
        return false;
    }

    private void OnNotesChanged(object? sender, EventArgs e) => _post(() =>
    {
        if (_disposed)
            return;
        Rebuild(_notes.Notes);
        Changed();
    });

    private void Rebuild(IReadOnlyList<Note> notes)
    {
        var now = _time.GetLocalNow();
        _items = notes.Select(n => Item(n, now)).ToArray();
    }

    private NoteListItem Item(Note note, DateTimeOffset now)
    {
        var title = note.Title.Length == 0 ? Strings.Get("Notes.EmptyNote") : note.Title;
        var changed = ChangedText(note.Changed, now, Strings.Culture, _time.LocalTimeZone);
        var id = note.Id;
        return new NoteListItem(
            id,
            title,
            changed,
            Strings.Format("Notes.ItemName", title, changed),
            Strings.Format("Notes.DeleteItemName", title),
            new AsyncRelayCommand(_ => OpenAsync(id), null, OnFault, _lifetime.Token),
            new AsyncRelayCommand(_ => DeleteAsync(id), () => !IsReadOnly, OnFault, _lifetime.Token));
    }

    /// <summary>The service's reasons in the user's language; the codes and the limit messages decide which.</summary>
    internal static string Reason(OperationError error)
    {
        if (error.Code == ErrorCode.InvalidInput)
        {
            if (error.SafeMessage == NoteRules.TooMany().SafeMessage)
                return Strings.Get("Notes.TooMany");
            if (error.SafeMessage == NoteRules.TooLong().SafeMessage)
                return Strings.Get("Notes.TooLong");
            if (error.SafeMessage == NoteRules.TooLarge().SafeMessage)
                return Strings.Get("Notes.TooLarge");
            return Strings.Get("Notes.Missing");
        }

        return error.Code switch
        {
            ErrorCode.Unsupported => Strings.Get("Notes.NewerVersion"),
            ErrorCode.AccessDenied => Strings.Get("Notes.AccessDenied"),
            _ => Strings.Get("Notes.DiskFailed")
        };
    }

    /// <summary>The time today, the day and month this year, the full date before.</summary>
    internal static string ChangedText(DateTimeOffset changed, DateTimeOffset now, CultureInfo culture, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(changed, zone);
        var today = TimeZoneInfo.ConvertTime(now, zone);
        if (local.Date == today.Date)
            return local.ToString("t", culture);
        return local.Year == today.Year ? local.ToString("d MMM", culture) : local.ToString("d MMM yyyy", culture);
    }

    private void Changed()
    {
        if (_disposed)
            return;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        foreach (var command in new[] { NewNoteCommand, CloseEditorCommand, DeleteCurrentCommand, UndoCommand, RetryCommand })
            ((AsyncRelayCommand)command).NotifyCanExecuteChanged();
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void OnFault(Exception ex)
    {
        Log(DiagnosticLevel.Error, "notes.page.fault", "Command", ErrorCode.Unexpected, ex.GetType());
        _problem = Strings.Get("Notes.Unexpected");
        Changed();
    }

    private void Log(DiagnosticLevel level, string eventId, string operation, ErrorCode? code = null, Type? exception = null) =>
        _log?.Write(new DiagnosticEvent(level, eventId, Component, operation, code, null, exception));

    private enum SaveState
    {
        Saved,
        Pending,
        Saving,
        Failed
    }
}
