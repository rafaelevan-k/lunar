using System.Text;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Notes;
using Lunar.Application.Notes;
using Lunar.Domain.Notes;
using Lunar.Domain.Shared;
using Lunar.Infrastructure.Logging;
using Lunar.Infrastructure.Notes;
using Xunit;

namespace Lunar.Infrastructure.Tests.Notes;

// ADR-0020, BE-36: the real store on the real disk, in a scratch folder; the user's %LocalAppData%\Lunar is never touched.
public sealed class JsonNotesRepositoryTests : IDisposable
{
    private const string Secret = "my bank PIN is 4321";

    private static readonly DateTimeOffset Now = new(2026, 11, 7, 10, 0, 0, TimeSpan.FromHours(7));

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "Lunar.NotesTests." + Guid.NewGuid().ToString("N"));

    public JsonNotesRepositoryTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string MainPath => Path.Combine(_folder, JsonNotesRepository.FileName);

    private string BackupPath => Path.Combine(_folder, JsonNotesRepository.BackupName);

    private string CorruptPath => Path.Combine(_folder, JsonNotesRepository.CorruptName);

    private string TempPath => Path.Combine(_folder, JsonNotesRepository.TempName);

    private sealed class RecordingLog : IDiagnosticLog
    {
        public List<DiagnosticEvent> Events { get; } = [];

        public void Write(DiagnosticEvent diagnosticEvent)
        {
            lock (Events)
                Events.Add(diagnosticEvent);
        }
    }

    private static Note Make(string text, int minutes = 0) => Note.Create(Guid.NewGuid(), text, Now.AddMinutes(minutes)).Value;

    private async Task<NotesLoadResult> LoadAsync(JsonNotesRepository repository)
    {
        var loaded = await repository.LoadAsync(Ct);
        Assert.True(loaded.IsSuccess, loaded.Error?.SafeMessage);
        return loaded.Value;
    }

    [Fact]
    public async Task NoFileMeansNoNotes()
    {
        using var repository = new JsonNotesRepository(_folder);

        var loaded = await LoadAsync(repository);

        Assert.Empty(loaded.Notes);
        Assert.Equal(NotesRecovery.CreatedEmpty, loaded.Recovery);
        Assert.False(File.Exists(MainPath)); // nothing is written by reading
    }

    [Fact]
    public async Task NotesRoundTripExactlyIncludingEveryLanguageEmojiAndLineBreaks()
    {
        var notes = new[]
        {
            Make("Belanja:\n- susu\n- roti", 1),
            Make("日本語のメモ 🙂 \"quotes\" \\ backslash\ttab", 2),
            Make(string.Empty, 3),
            Make(new string('z', NoteRules.MaxTextLength), 4)
        };
        using (var writer = new JsonNotesRepository(_folder))
            Assert.True((await writer.SaveAsync(notes, Ct)).IsSuccess);

        using var reader = new JsonNotesRepository(_folder);
        var loaded = await LoadAsync(reader);

        Assert.Equal(notes, loaded.Notes);
        Assert.Equal(NotesRecovery.None, loaded.Recovery);
        Assert.Contains("日本語のメモ", File.ReadAllText(MainPath, Encoding.UTF8), StringComparison.Ordinal); // readable, not \u escapes (emoji still are)
        Assert.Equal(Now.AddMinutes(2), loaded.Notes[1].Changed);
        Assert.Equal(Now.Offset, loaded.Notes[1].Changed.Offset);
    }

    [Fact]
    public async Task EverySaveIsAtomicAndKeepsThePreviousGoodCopyAsTheBackup()
    {
        using var repository = new JsonNotesRepository(_folder);
        var first = new[] { Make("first") };
        var second = new[] { Make("second") };

        Assert.True((await repository.SaveAsync(first, Ct)).IsSuccess);
        Assert.False(File.Exists(BackupPath));
        var firstBytes = File.ReadAllBytes(MainPath);
        Assert.True((await repository.SaveAsync(second, Ct)).IsSuccess);

        Assert.Equal(firstBytes, File.ReadAllBytes(BackupPath));
        Assert.Equal(second, (await LoadAsync(repository)).Notes);
        Assert.False(File.Exists(TempPath));
    }

    [Fact]
    public async Task AWriteInterruptedBeforeTheSwapLeavesTheOldFileIntactAndTheLeftoverIsRemoved()
    {
        var kept = new[] { Make("kept") };
        using var repository = new JsonNotesRepository(_folder);
        Assert.True((await repository.SaveAsync(kept, Ct)).IsSuccess);
        File.WriteAllText(TempPath, "{ \"schemaVersion\": 1, \"notes\": [ { \"id\": "); // a crash mid-write

        var loaded = await LoadAsync(repository);

        Assert.Equal(kept, loaded.Notes);
        Assert.False(File.Exists(TempPath));
    }

    [Fact]
    public async Task ADamagedFileIsKeptAsideWholeAndTheBackupIsUsed()
    {
        var good = new[] { Make("good") };
        using var repository = new JsonNotesRepository(_folder);
        Assert.True((await repository.SaveAsync(good, Ct)).IsSuccess);
        Assert.True((await repository.SaveAsync([Make("newer, then damaged")], Ct)).IsSuccess);
        var damaged = Encoding.UTF8.GetBytes("{ \"schemaVersion\": 1, \"notes\": [ broken");
        File.WriteAllBytes(MainPath, damaged);

        var loaded = await LoadAsync(repository);

        Assert.Equal(NotesRecovery.RestoredFromBackup, loaded.Recovery);
        Assert.Equal(good, loaded.Notes);
        Assert.Equal(damaged, File.ReadAllBytes(CorruptPath));
        Assert.False(File.Exists(MainPath));

        // The next save writes a fresh file and leaves the backup alone.
        Assert.True((await repository.SaveAsync([.. good, Make("after recovery")], Ct)).IsSuccess);
        Assert.Equal(2, (await LoadAsync(repository)).Notes.Count);
        Assert.True(File.Exists(BackupPath));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("{\"notes\":[]}")]
    [InlineData("{\"schemaVersion\":0,\"notes\":[]}")]
    [InlineData("{\"schemaVersion\":1}")]
    [InlineData("{\"schemaVersion\":1,\"notes\":[{\"id\":\"nope\",\"text\":\"x\",\"created\":\"2026-11-07T10:00:00.0000000+00:00\",\"changed\":\"2026-11-07T10:00:00.0000000+00:00\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"notes\":[{\"id\":\"4c7f0a52-1f0e-4bb2-9f0c-3d1a6e2b8a10\",\"text\":5,\"created\":\"2026-11-07T10:00:00.0000000+00:00\",\"changed\":\"2026-11-07T10:00:00.0000000+00:00\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"notes\":[{\"id\":\"4c7f0a52-1f0e-4bb2-9f0c-3d1a6e2b8a10\",\"text\":\"x\",\"created\":\"yesterday\",\"changed\":\"2026-11-07T10:00:00.0000000+00:00\"}]}")]
    public async Task ADamagedFileWithoutABackupIsKeptAsideAndSaysSo(string content)
    {
        File.WriteAllText(MainPath, content);
        var log = new RecordingLog();
        using var repository = new JsonNotesRepository(_folder, log);

        var loaded = await LoadAsync(repository);

        Assert.Equal(NotesRecovery.DamagedNoBackup, loaded.Recovery);
        Assert.Empty(loaded.Notes);
        Assert.Equal(content, File.ReadAllText(CorruptPath));
        Assert.Contains(log.Events, e => e.EventId == "notes.load.damaged");
    }

    [Fact]
    public async Task TwoNotesWithTheSameIdMakeTheFileDamaged()
    {
        var note = Make("twice");
        File.WriteAllBytes(MainPath, JsonNotesRepository.Serialize([note, note]));
        using var repository = new JsonNotesRepository(_folder);

        Assert.Equal(NotesRecovery.DamagedNoBackup, (await LoadAsync(repository)).Recovery);
    }

    [Fact]
    public async Task AByteOrderMarkAndHandEditedLineBreaksAreAccepted()
    {
        var note = Make("a\nb");
        var json = Encoding.UTF8.GetString(JsonNotesRepository.Serialize([note])).Replace("a\\nb", "a\\r\\nb", StringComparison.Ordinal);
        File.WriteAllBytes(MainPath, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(json)]);
        using var repository = new JsonNotesRepository(_folder);

        var loaded = await LoadAsync(repository);

        Assert.Equal(NotesRecovery.None, loaded.Recovery);
        Assert.Equal("a\nb", Assert.Single(loaded.Notes).Text);
    }

    [Fact]
    public async Task AFileFromANewerLunarIsShownButNeverWritten()
    {
        var note = Make("from the future");
        var json = Encoding.UTF8.GetString(JsonNotesRepository.Serialize([note]))
            .Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2, \"tags\": [\"x\"]", StringComparison.Ordinal);
        File.WriteAllText(MainPath, json);
        var before = File.ReadAllBytes(MainPath);
        using var repository = new JsonNotesRepository(_folder);

        var loaded = await LoadAsync(repository);
        var save = await repository.SaveAsync([Make("would overwrite")], Ct);

        Assert.Equal(NotesRecovery.NewerVersionReadOnly, loaded.Recovery);
        Assert.Equal([note], loaded.Notes);
        Assert.Equal(ErrorCode.Unsupported, save.Error!.Code);
        Assert.Equal(before, File.ReadAllBytes(MainPath));
    }

    [Fact]
    public async Task ANewerFileThatCannotBeReadIsStillReadOnlyAndUntouched()
    {
        File.WriteAllText(MainPath, "{\"schemaVersion\":7,\"pages\":{}}");
        using var repository = new JsonNotesRepository(_folder);

        var loaded = await LoadAsync(repository);

        Assert.Equal(NotesRecovery.NewerVersionReadOnly, loaded.Recovery);
        Assert.Empty(loaded.Notes);
        Assert.False(File.Exists(CorruptPath));
    }

    [Fact]
    public async Task ALockedFileFailsToLoadAndIsNeverWrittenOver()
    {
        using var repository = new JsonNotesRepository(_folder);
        Assert.True((await repository.SaveAsync([Make("on disk")], Ct)).IsSuccess);
        var before = File.ReadAllBytes(MainPath);

        Result<NotesLoadResult> load;
        Result save;
        using (new FileStream(MainPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            load = await repository.LoadAsync(Ct);
            save = await repository.SaveAsync([Make("would overwrite")], Ct);
        }

        Assert.Equal(ErrorCode.IoFailure, load.Error!.Code);
        Assert.Equal(ErrorCode.IoFailure, save.Error!.Code);
        Assert.Equal(before, File.ReadAllBytes(MainPath));
    }

    [Fact]
    public async Task NotesTooLargeToStoreTogetherAreRefusedAndTheFileIsUnchanged()
    {
        using var repository = new JsonNotesRepository(_folder);
        Assert.True((await repository.SaveAsync([Make("small")], Ct)).IsSuccess);
        var before = File.ReadAllBytes(MainPath);
        // 210 notes of 100,000 characters is about 21 MB.
        var huge = Enumerable.Range(0, 210).Select(i => Make(new string((char)('a' + (i % 26)), NoteRules.MaxTextLength))).ToArray();

        var save = await repository.SaveAsync(huge, Ct);

        Assert.Equal(ErrorCode.InvalidInput, save.Error!.Code);
        Assert.Equal(before, File.ReadAllBytes(MainPath));
        Assert.False(File.Exists(TempPath));
    }

    [Fact]
    public async Task TheServiceOnTheRealDiskKeepsEveryConcurrentEdit()
    {
        using var repository = new JsonNotesRepository(_folder);
        using var service = new NotesService(repository);
        var shared = (await service.CreateAsync("start", Ct)).Value;

        var work = Enumerable.Range(0, 40).Select(i => service.CreateAsync("note " + i, Ct))
            .Concat(Enumerable.Range(0, 40).Select(i => service.UpdateAsync(shared.Id, "edit " + i, Ct)));
        var results = await Task.WhenAll(work);

        Assert.All(results, r => Assert.True(r.IsSuccess, r.Error?.SafeMessage));
        using var reader = new JsonNotesRepository(_folder);
        var onDisk = (await LoadAsync(reader)).Notes;
        Assert.Equal(41, onDisk.Count);
        Assert.Equal(service.Notes.OrderBy(n => n.Id), onDisk.OrderBy(n => n.Id));
    }

    [Fact]
    public async Task NoteTextNeverReachesTheRealLogFile()
    {
        var logs = Path.Combine(_folder, "Logs");
        using (var log = new SerilogDiagnosticLog(logs, diagnosticsEnabled: true))
        {
            using var repository = new JsonNotesRepository(_folder, log);
            using var service = new NotesService(repository, log: log);
            var note = (await service.CreateAsync(Secret, Ct)).Value;
            await service.UpdateAsync(note.Id, Secret + " again", Ct);

            File.WriteAllText(MainPath, "{ damaged " + Secret);
            using var second = new JsonNotesRepository(_folder, log);
            using var reload = new NotesService(second, log: log);
            await reload.LoadAsync(Ct);
            using (new FileStream(CorruptPath, FileMode.Open, FileAccess.Read, FileShare.None))
                await reload.CreateAsync(Secret, Ct);
            using (new FileStream(Path.Combine(_folder, JsonNotesRepository.FileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                await reload.CreateAsync(Secret, Ct);
        }

        var written = string.Concat(Directory.GetFiles(logs).Select(f => File.ReadAllText(f, Encoding.UTF8)));
        Assert.Contains("notes.", written, StringComparison.Ordinal); // events were written
        Assert.DoesNotContain("4321", written, StringComparison.Ordinal);
        Assert.DoesNotContain("PIN", written, StringComparison.Ordinal);
        Assert.DoesNotContain(_folder, written, StringComparison.OrdinalIgnoreCase);
    }
}
