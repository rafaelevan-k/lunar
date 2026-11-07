using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Notes;
using Lunar.Application.Notes;
using Lunar.Domain.Notes;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Application.Tests.Notes;

public sealed class NotesServiceTests
{
    private const string Secret = "my bank PIN is 4321";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeRepository : INotesRepository
    {
        private readonly Lock _gate = new();

        public List<Note> Stored { get; set; } = [];

        public NotesRecovery Recovery { get; set; } = NotesRecovery.None;

        public ErrorCode? FailLoadWith { get; set; }

        public ErrorCode? FailSaveWith { get; set; }

        public int Loads { get; private set; }

        public int Saves { get; private set; }

        public int Concurrent { get; private set; }

        public int MaxConcurrent { get; private set; }

        public async Task<Result<NotesLoadResult>> LoadAsync(CancellationToken cancellationToken)
        {
            await Task.Yield();
            Loads++;
            if (FailLoadWith is { } code)
                return Result.Failure<NotesLoadResult>(new OperationError(code, "Cannot read."));
            return Result.Success(new NotesLoadResult(Stored.ToList(), Recovery));
        }

        public async Task<Result> SaveAsync(IReadOnlyList<Note> notes, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Concurrent++;
                MaxConcurrent = Math.Max(MaxConcurrent, Concurrent);
            }

            await Task.Delay(1, cancellationToken);
            lock (_gate)
                Concurrent--;
            Saves++;
            if (FailSaveWith is { } code)
                return Result.Failure(new OperationError(code, "Cannot write."));
            Stored = notes.ToList();
            return Result.Success();
        }
    }

    private sealed class RecordingLog : IDiagnosticLog
    {
        public List<DiagnosticEvent> Events { get; } = [];

        public void Write(DiagnosticEvent diagnosticEvent)
        {
            lock (Events)
                Events.Add(diagnosticEvent);
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static Note Stored(int seconds, string text) =>
        Note.Create(Guid.NewGuid(), text, new DateTimeOffset(2026, 11, 1, 0, 0, seconds, TimeSpan.Zero)).Value;

    [Fact]
    public async Task TheFileIsReadOnceAndNotesAreListedMostRecentlyChangedFirst()
    {
        var older = Stored(1, "older");
        var newer = Stored(2, "newer");
        var repository = new FakeRepository { Stored = [older, newer] };
        using var service = new NotesService(repository);

        var first = await service.LoadAsync(Ct);
        await service.LoadAsync(Ct);

        Assert.Equal([newer, older], first.Value);
        Assert.Equal(1, repository.Loads);
        Assert.Equal(NotesRecovery.None, service.Recovery);
    }

    [Fact]
    public async Task CreateUpdateDeleteAndRestoreAreSavedBeforeTheyAreShown()
    {
        var repository = new FakeRepository();
        var clock = new Clock();
        using var service = new NotesService(repository, clock);
        var changes = 0;
        service.Changed += (_, _) => changes++;

        var note = (await service.CreateAsync("first", Ct)).Value;
        Assert.Equal([note], repository.Stored);

        clock.Now = clock.Now.AddMinutes(1);
        var updated = (await service.UpdateAsync(note.Id, "first, edited", Ct)).Value;
        Assert.Equal("first, edited", Assert.Single(repository.Stored).Text);
        Assert.Equal(clock.Now, updated.Changed);
        Assert.Equal(note.Created, updated.Created);

        var deleted = (await service.DeleteAsync(note.Id, Ct)).Value;
        Assert.Empty(repository.Stored);
        Assert.Empty(service.Notes);

        var restored = (await service.RestoreAsync(deleted, Ct)).Value;
        Assert.Equal(updated, restored);
        Assert.Equal([updated], service.Notes);
        Assert.Equal(4, changes);
    }

    [Fact]
    public async Task TheSameTextAgainWritesNothingAndTellsNobody()
    {
        var repository = new FakeRepository();
        using var service = new NotesService(repository);
        var note = (await service.CreateAsync("same", Ct)).Value;
        var changes = 0;
        service.Changed += (_, _) => changes++;

        Assert.True((await service.UpdateAsync(note.Id, "same", Ct)).IsSuccess);
        Assert.True((await service.RestoreAsync(note, Ct)).IsSuccess); // already there

        Assert.Equal(1, repository.Saves);
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task AFailedSaveChangesNothing()
    {
        var existing = Stored(1, "kept");
        var repository = new FakeRepository { Stored = [existing] };
        using var service = new NotesService(repository);
        await service.LoadAsync(Ct);
        repository.FailSaveWith = ErrorCode.IoFailure;
        var changes = 0;
        service.Changed += (_, _) => changes++;

        Assert.Equal(ErrorCode.IoFailure, (await service.CreateAsync("new", Ct)).Error!.Code);
        Assert.Equal(ErrorCode.IoFailure, (await service.UpdateAsync(existing.Id, "changed", Ct)).Error!.Code);
        Assert.Equal(ErrorCode.IoFailure, (await service.DeleteAsync(existing.Id, Ct)).Error!.Code);

        Assert.Equal([existing], service.Notes);
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task ANoteThatNoLongerExistsIsInvalidInput()
    {
        using var service = new NotesService(new FakeRepository());

        Assert.Equal(ErrorCode.InvalidInput, (await service.UpdateAsync(Guid.NewGuid(), "x", Ct)).Error!.Code);
        Assert.Equal(ErrorCode.InvalidInput, (await service.DeleteAsync(Guid.NewGuid(), Ct)).Error!.Code);
    }

    [Fact]
    public async Task TheLimitsAreExplainedAndNothingIsWritten()
    {
        var full = Enumerable.Range(0, NoteRules.MaxNotes).Select(i => Stored(0, "n")).ToList();
        var repository = new FakeRepository { Stored = full };
        using var service = new NotesService(repository);

        var tooMany = await service.CreateAsync("one more", Ct);
        var tooLong = await service.UpdateAsync(full[0].Id, new string('a', NoteRules.MaxTextLength + 1), Ct);
        var deleted = (await service.DeleteAsync(full[0].Id, Ct)).Value;
        Assert.True((await service.CreateAsync("fits again", Ct)).IsSuccess);
        var restoreWhenFull = await service.RestoreAsync(deleted, Ct);

        Assert.Equal(ErrorCode.InvalidInput, tooMany.Error!.Code);
        Assert.Contains("10,000", tooMany.Error.SafeMessage, StringComparison.Ordinal);
        Assert.Equal(ErrorCode.InvalidInput, tooLong.Error!.Code);
        Assert.Equal(ErrorCode.InvalidInput, restoreWhenFull.Error!.Code);
        Assert.Equal(2, repository.Saves);
    }

    [Fact]
    public async Task ANewerFileIsShownButEveryChangeIsRefused()
    {
        var note = Stored(1, "from a newer Lunar");
        var repository = new FakeRepository { Stored = [note], Recovery = NotesRecovery.NewerVersionReadOnly };
        using var service = new NotesService(repository);

        Assert.Equal([note], (await service.LoadAsync(Ct)).Value);
        Assert.True(service.IsReadOnly);
        Assert.Equal(ErrorCode.Unsupported, (await service.CreateAsync("x", Ct)).Error!.Code);
        Assert.Equal(ErrorCode.Unsupported, (await service.UpdateAsync(note.Id, "x", Ct)).Error!.Code);
        Assert.Equal(ErrorCode.Unsupported, (await service.DeleteAsync(note.Id, Ct)).Error!.Code);
        Assert.Equal(0, repository.Saves);
    }

    [Fact]
    public async Task AnUnreadableFileRefusesChangesUntilALaterReadWorks()
    {
        var existing = Stored(1, "on disk");
        var repository = new FakeRepository { Stored = [existing], FailLoadWith = ErrorCode.IoFailure };
        using var service = new NotesService(repository);

        Assert.Equal(ErrorCode.IoFailure, (await service.LoadAsync(Ct)).Error!.Code);
        Assert.Equal(ErrorCode.IoFailure, (await service.CreateAsync("would overwrite", Ct)).Error!.Code);
        Assert.Equal(0, repository.Saves);
        Assert.False(service.IsLoaded);

        repository.FailLoadWith = null;
        Assert.True((await service.CreateAsync("now fine", Ct)).IsSuccess);
        Assert.Equal(2, repository.Stored.Count); // the note on disk was kept
    }

    [Fact]
    public async Task ConcurrentEditsAreSerializedAndNoneIsLost()
    {
        var repository = new FakeRepository();
        using var service = new NotesService(repository);
        var shared = (await service.CreateAsync("start", Ct)).Value;

        var creates = Enumerable.Range(0, 50).Select(i => service.CreateAsync("note " + i, Ct));
        var edits = Enumerable.Range(0, 50).Select(i => service.UpdateAsync(shared.Id, "edit " + i, Ct));
        var results = await Task.WhenAll(creates.Concat(edits));

        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(1, repository.MaxConcurrent);
        Assert.Equal(51, repository.Stored.Count);
        Assert.Equal(51, service.Notes.Count);
        Assert.Equal(50, Enumerable.Range(0, 50).Count(i => service.Notes.Any(n => n.Text == "note " + i)));
        Assert.StartsWith("edit ", service.Notes.Single(n => n.Id == shared.Id).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACancelledCallIsReportedAsCancelledAndChangesNothing()
    {
        var repository = new FakeRepository();
        using var service = new NotesService(repository);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Assert.True((await service.CreateAsync("x", cancelled.Token)).IsCancelled);
        Assert.True((await service.LoadAsync(cancelled.Token)).IsCancelled);
        Assert.Equal(0, repository.Saves);
    }

    [Fact]
    public async Task NoteTextNeverReachesTheLog()
    {
        var repository = new FakeRepository();
        var log = new RecordingLog();
        using var service = new NotesService(repository, log: log);
        var note = (await service.CreateAsync(Secret, Ct)).Value;
        repository.FailSaveWith = ErrorCode.AccessDenied;
        await service.UpdateAsync(note.Id, Secret + " changed", Ct);
        await service.CreateAsync(Secret, Ct);

        Assert.NotEmpty(log.Events);
        Assert.All(log.Events, e => Assert.DoesNotContain("4321", e.ToString(), StringComparison.Ordinal));
        Assert.All(log.Events, e => Assert.Equal("notes.save.failed", e.EventId));
    }
}
