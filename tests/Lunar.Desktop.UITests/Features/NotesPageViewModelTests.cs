using System.Globalization;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Notes;
using Lunar.Application.Notes;
using Lunar.Desktop.Features.Notes;
using Lunar.Desktop.Resources;
using Lunar.Domain.Notes;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Desktop.UITests.Features;

// FE-31: the Notes page's logic over the real NotesService and an in-memory notes file. Timers are fired by hand. No WPF here.
public sealed class NotesPageViewModelTests
{
    private const string Secret = "my bank PIN is 4321";

    internal sealed class FixedTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 7, 15, 30, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
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

    private sealed class Rig : IDisposable
    {
        public Rig(Action<FakeNotesRepository>? configure = null)
        {
            configure?.Invoke(Repository);
            Service = new NotesService(Repository, Time, Log);
            Model = new NotesPageViewModel(Service, work => work(), SaveTimer, UndoTimer, Time, Log);
        }

        public FakeNotesRepository Repository { get; } = new();

        public FixedTime Time { get; } = new();

        public RecordingLog Log { get; } = new();

        public ManualTimer SaveTimer { get; } = new();

        public ManualTimer UndoTimer { get; } = new();

        public NotesService Service { get; }

        public NotesPageViewModel Model { get; }

        public async Task StartAsync() => await Model.InitializeAsync(CancellationToken.None);

        /// <summary>Runs a command and waits until it has finished (AsyncRelayCommand runs it as async void).</summary>
        public async Task RunAsync(System.Windows.Input.ICommand command)
        {
            Assert.True(command.CanExecute(null), "The command cannot run now.");
            command.Execute(null);
            var running = (Lunar.Desktop.Shell.AsyncRelayCommand)command;
            for (var i = 0; i < 500 && running.IsRunning; i++)
                await Task.Delay(2);
            Assert.False(running.IsRunning, "The command did not finish.");
        }

        public void Dispose()
        {
            Model.Dispose();
            Service.Dispose();
        }
    }

    private static Note Stored(string text, int minutesAgo) =>
        Note.Create(Guid.NewGuid(), text, new DateTimeOffset(2026, 11, 7, 15, 30, 0, TimeSpan.Zero).AddMinutes(-minutesAgo)).Value;

    [Fact]
    public async Task TheListShowsEveryNoteNewestFirstWithItsFirstLineAndWhenItChanged()
    {
        var older = Stored("Groceries\nmilk", 600);
        var newer = Stored("\n  Call mum  \nabout Sunday", 5);
        var blank = Stored("   ", 1000);
        using var rig = new Rig(r => r.Stored = [older, newer, blank]);

        await rig.StartAsync();

        Assert.True(rig.Model.HasItems);
        Assert.False(rig.Model.IsEmpty);
        Assert.Equal(["Call mum", "Groceries", "Empty note"], rig.Model.Items.Select(i => i.Title));
        Assert.Equal("Call mum, changed " + rig.Model.Items[0].ChangedText, rig.Model.Items[0].AutomationName);
        Assert.Equal("Delete Call mum", rig.Model.Items[0].DeleteName);
    }

    [Fact]
    public async Task NoNotesSaysSoAndOffersANewNote()
    {
        using var rig = new Rig();

        await rig.StartAsync();

        Assert.True(rig.Model.IsEmpty);
        Assert.False(rig.Model.HasItems);
        Assert.True(rig.Model.NewNoteCommand.CanExecute(null));
    }

    [Fact]
    public async Task ANewNoteOpensAndSavesOnItsOwnAfterAPauseInTyping()
    {
        using var rig = new Rig();
        await rig.StartAsync();

        await rig.RunAsync(rig.Model.NewNoteCommand);
        Assert.True(rig.Model.IsEditing);
        Assert.False(rig.Model.IsListShown);

        rig.Model.EditorText = "Draft";
        rig.Model.EditorText = "Draft one";
        Assert.Equal(NotesPageViewModel.SaveDelay, rig.SaveTimer.Delay);
        Assert.Equal(string.Empty, rig.Model.SaveStatus); // typing: nothing claimed yet
        Assert.Equal(string.Empty, Assert.Single(rig.Repository.Stored).Text); // not saved before the pause

        rig.SaveTimer.Fire();
        await rig.Model.FlushAsync();

        Assert.Equal("Draft one", Assert.Single(rig.Repository.Stored).Text);
        Assert.Equal("Saved", rig.Model.SaveStatus);
    }

    [Fact]
    public async Task ClosingANoteSavesWhatWasTypedAndShowsItInTheList()
    {
        using var rig = new Rig();
        await rig.StartAsync();
        await rig.RunAsync(rig.Model.NewNoteCommand);
        rig.Model.EditorText = "Typed and closed at once";

        await rig.RunAsync(rig.Model.CloseEditorCommand);

        Assert.False(rig.Model.IsEditing);
        Assert.Equal("Typed and closed at once", Assert.Single(rig.Repository.Stored).Text);
        Assert.Equal("Typed and closed at once", Assert.Single(rig.Model.Items).Title);
        Assert.Null(rig.SaveTimer.Pending);
    }

    [Fact]
    public async Task ANoteLeftEmptyIsRemovedWhenClosedWithoutUndo()
    {
        using var rig = new Rig();
        await rig.StartAsync();
        await rig.RunAsync(rig.Model.NewNoteCommand);

        await rig.RunAsync(rig.Model.CloseEditorCommand);

        Assert.Empty(rig.Repository.Stored);
        Assert.True(rig.Model.IsEmpty);
        Assert.False(rig.Model.CanUndo);
    }

    [Fact]
    public async Task OpeningANoteFromTheListShowsItsTextAndEditsIt()
    {
        var note = Stored("Old text", 60);
        using var rig = new Rig(r => r.Stored = [note]);
        await rig.StartAsync();

        await rig.RunAsync(rig.Model.Items[0].OpenCommand);
        Assert.Equal("Old text", rig.Model.EditorText);
        rig.Model.EditorText = "New text";
        await rig.RunAsync(rig.Model.CloseEditorCommand);

        var saved = Assert.Single(rig.Repository.Stored);
        Assert.Equal((note.Id, "New text", rig.Time.Now), (saved.Id, saved.Text, saved.Changed));
    }

    [Fact]
    public async Task AFailedSaveIsSaidInWordsAndTheNoteStaysOpenSoNothingIsLost()
    {
        var note = Stored("Kept", 60);
        using var rig = new Rig(r => r.Stored = [note]);
        await rig.StartAsync();
        await rig.RunAsync(rig.Model.Items[0].OpenCommand);
        rig.Repository.FailSaveWith = ErrorCode.AccessDenied;

        rig.Model.EditorText = "Unsaved words";
        await rig.RunAsync(rig.Model.CloseEditorCommand);

        Assert.True(rig.Model.IsEditing);
        Assert.Equal("Unsaved words", rig.Model.EditorText);
        Assert.Equal("Not saved", rig.Model.SaveStatus);
        Assert.Equal("Windows denied access to your notes file. The note stays open so nothing is lost.", rig.Model.Problem);

        rig.Repository.FailSaveWith = null;
        await rig.RunAsync(rig.Model.CloseEditorCommand);
        Assert.False(rig.Model.IsEditing);
        Assert.Equal("Unsaved words", Assert.Single(rig.Repository.Stored).Text);
        Assert.Null(rig.Model.Problem);
    }

    [Fact]
    public async Task DeletingAnOpenNoteOffersUndoWhichBringsItBackAsItWas()
    {
        var note = Stored("Keep me", 60);
        using var rig = new Rig(r => r.Stored = [note]);
        await rig.StartAsync();
        await rig.RunAsync(rig.Model.Items[0].OpenCommand);
        rig.Model.EditorText = "Keep me, edited";

        await rig.RunAsync(rig.Model.DeleteCurrentCommand);
        Assert.False(rig.Model.IsEditing);
        Assert.Empty(rig.Repository.Stored);
        Assert.True(rig.Model.CanUndo);
        Assert.Equal(NotesPageViewModel.UndoTime, rig.UndoTimer.Delay);

        await rig.RunAsync(rig.Model.UndoCommand);

        Assert.Equal("Keep me, edited", Assert.Single(rig.Repository.Stored).Text); // what was typed is not lost
        Assert.False(rig.Model.CanUndo);
        Assert.Single(rig.Model.Items);
    }

    [Fact]
    public async Task UndoGoesAwayAfterAFewSecondsAndTheDeleteIsFinal()
    {
        var note = Stored("Gone", 60);
        using var rig = new Rig(r => r.Stored = [note]);
        await rig.StartAsync();

        await rig.RunAsync(rig.Model.Items[0].DeleteCommand);
        Assert.True(rig.Model.CanUndo);
        rig.UndoTimer.Fire();

        Assert.False(rig.Model.CanUndo);
        Assert.False(rig.Model.UndoCommand.CanExecute(null));
        Assert.Empty(rig.Repository.Stored);
        Assert.True(rig.Model.IsEmpty);
    }

    [Fact]
    public async Task AnUnreadableFileIsSaidInWordsAndTryAgainReadsIt()
    {
        using var rig = new Rig(r =>
        {
            r.FailLoadWith = ErrorCode.IoFailure;
            r.Stored = [Stored("on disk", 1)];
        });
        await rig.StartAsync();

        Assert.Equal("Lunar couldn't read your notes. They have not been changed.", rig.Model.Problem);
        Assert.False(rig.Model.NewNoteCommand.CanExecute(null));
        Assert.False(rig.Model.IsListShown);

        rig.Repository.FailLoadWith = null;
        await rig.RunAsync(rig.Model.RetryCommand);

        Assert.Null(rig.Model.Problem);
        Assert.Single(rig.Model.Items);
        Assert.False(rig.Model.RetryCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(NotesRecovery.RestoredFromBackup, "Your notes file was damaged, so Lunar opened the last good copy.")]
    [InlineData(NotesRecovery.DamagedNoBackup, "Your notes file was damaged and no good copy was found.")]
    [InlineData(NotesRecovery.NewerVersionReadOnly, "These notes were saved by a newer version of Lunar.")]
    public async Task ARecoveryAtLoadIsToldOnceAndCanBeDismissed(NotesRecovery recovery, string start)
    {
        using var rig = new Rig(r => r.Recovery = recovery);
        await rig.StartAsync();

        Assert.StartsWith(start, rig.Model.Notice, StringComparison.Ordinal);
        rig.Model.DismissNoticeCommand.Execute(null);

        Assert.False(rig.Model.HasNotice);
    }

    [Theory]
    [InlineData(NotesRecovery.None)]
    [InlineData(NotesRecovery.CreatedEmpty)]
    public async Task ANormalLoadHasNoNotice(NotesRecovery recovery)
    {
        using var rig = new Rig(r => r.Recovery = recovery);
        await rig.StartAsync();

        Assert.False(rig.Model.HasNotice);
    }

    [Fact]
    public async Task NotesFromANewerLunarCanBeReadButNotChanged()
    {
        var note = Stored("future", 1);
        using var rig = new Rig(r =>
        {
            r.Stored = [note];
            r.Recovery = NotesRecovery.NewerVersionReadOnly;
        });
        await rig.StartAsync();

        Assert.False(rig.Model.NewNoteCommand.CanExecute(null));
        Assert.False(rig.Model.Items[0].DeleteCommand.CanExecute(null));
        await rig.RunAsync(rig.Model.Items[0].OpenCommand);
        Assert.Equal("future", rig.Model.EditorText);
        rig.Model.EditorText = "changed";

        Assert.Equal("future", rig.Model.EditorText);
        Assert.False(rig.Model.CanEdit);
        Assert.False(rig.Model.DeleteCurrentCommand.CanExecute(null));
        await rig.RunAsync(rig.Model.CloseEditorCommand);
        Assert.Equal(0, rig.Repository.Saves);
    }

    [Fact]
    public async Task CollapsingTheIslandSavesTheOpenNoteAtOnce()
    {
        using var rig = new Rig();
        await rig.StartAsync();
        await rig.RunAsync(rig.Model.NewNoteCommand);
        rig.Model.EditorText = "Before collapse";

        rig.Model.SetIslandOpen(false);
        await rig.Model.FlushAsync();

        Assert.Equal("Before collapse", Assert.Single(rig.Repository.Stored).Text);
        Assert.True(rig.Model.IsEditing); // still open when the island opens again
    }

    [Fact]
    public async Task ClosingLunarSavesTheOpenNoteWithoutTheUiThread()
    {
        using var rig = new Rig();
        await rig.StartAsync();
        await rig.RunAsync(rig.Model.NewNoteCommand);
        rig.Model.EditorText = "Before exit";

        await rig.Model.FlushForExitAsync().WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal("Before exit", Assert.Single(rig.Repository.Stored).Text);
    }

    [Fact]
    public async Task ALimitIsExplainedInTheUsersLanguage()
    {
        var full = Enumerable.Range(0, NoteRules.MaxNotes).Select(i => Stored("n", 1)).ToList();
        using var rig = new Rig(r => r.Stored = full);
        await rig.StartAsync();

        try
        {
            Strings.Use("id");
            await rig.RunAsync(rig.Model.NewNoteCommand);
            Assert.Equal("Lunar bisa menyimpan hingga 10.000 catatan. Hapus sebuah catatan untuk memberi ruang.", rig.Model.Problem);
        }
        finally
        {
            Strings.Use("en");
        }

        Assert.False(rig.Model.IsEditing);
    }

    [Fact]
    public async Task NoteTextNeverReachesTheLog()
    {
        using var rig = new Rig();
        await rig.StartAsync();
        await rig.RunAsync(rig.Model.NewNoteCommand);
        rig.Repository.FailSaveWith = ErrorCode.IoFailure;
        rig.Model.EditorText = Secret;
        await rig.RunAsync(rig.Model.CloseEditorCommand);

        Assert.NotEmpty(rig.Log.Events);
        Assert.All(rig.Log.Events, e => Assert.DoesNotContain("4321", e.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void TheChangedTimeIsTheClockTodayTheDayThisYearAndTheFullDateBefore()
    {
        var now = new DateTimeOffset(2026, 11, 7, 15, 30, 0, TimeSpan.Zero);
        var culture = CultureInfo.GetCultureInfo("en-GB");

        Assert.Equal("09:05", NotesPageViewModel.ChangedText(new DateTimeOffset(2026, 11, 7, 9, 5, 0, TimeSpan.Zero), now, culture, TimeZoneInfo.Utc));
        Assert.Equal("3 Feb", NotesPageViewModel.ChangedText(new DateTimeOffset(2026, 2, 3, 9, 5, 0, TimeSpan.Zero), now, culture, TimeZoneInfo.Utc));
        Assert.Equal("3 Feb 2025", NotesPageViewModel.ChangedText(new DateTimeOffset(2025, 2, 3, 9, 5, 0, TimeSpan.Zero), now, culture, TimeZoneInfo.Utc));
    }
}
