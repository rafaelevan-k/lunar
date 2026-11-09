using Lunar.Application.Abstractions.Audio;
using Lunar.Domain.Shared;
using Lunar.Infrastructure.Recording;
using Xunit;

namespace Lunar.Infrastructure.Tests.Recording;

/// <summary>The recording files on the real disk (ADR-0022).</summary>
public sealed class RecordingFileStoreTests : IDisposable
{
    private const string Base = "Recording 2026-11-09 14-30-05";
    private readonly string _root = Directory.CreateTempSubdirectory("lunar-rec-").FullName;
    private readonly string _folder;
    private readonly string _appData;
    private readonly RecordingFileStore _store;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public RecordingFileStoreTests()
    {
        _folder = Path.Combine(_root, "Takes");
        _appData = Path.Combine(_root, "AppData");
        _store = new RecordingFileStore(_appData, _folder);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Journal => Path.Combine(_appData, RecordingFileStore.JournalName);

    // Valid MPEG-1 Layer III frames at 192 kbps, 48 kHz, behind a short ID3 tag.
    private static byte[] Mp3(int frames = 8)
    {
        const int FrameLength = 144 * 192000 / 48000;
        var bytes = new byte[10 + (frames * FrameLength)];
        bytes[0] = (byte)'I';
        bytes[1] = (byte)'D';
        bytes[2] = (byte)'3';
        for (var i = 0; i < frames; i++)
        {
            var at = 10 + (i * FrameLength);
            bytes[at] = 0xFF;
            bytes[at + 1] = 0xFB;
            bytes[at + 2] = 0xB4;
            bytes[at + 3] = 0x00;
        }

        return bytes;
    }

    private async Task<string> ReserveWithAudioAsync(byte[]? content = null)
    {
        var part = await _store.ReservePartAsync(_folder, Base, Ct);
        Assert.True(part.IsSuccess);
        await File.WriteAllBytesAsync(part.Value, content ?? Mp3(), Ct);
        return part.Value;
    }

    [Fact]
    public async Task ReservingCreatesTheFolderAndAPartNameAndRemembersIt()
    {
        var part = await _store.ReservePartAsync(_folder, Base, Ct);

        Assert.True(Directory.Exists(_folder));
        Assert.Matches("^~[0-9a-f]{32}\\.lunar-part$", Path.GetFileName(part.Value));
        Assert.False(File.Exists(part.Value)); // the engine creates it
        Assert.Contains(part.Value, await File.ReadAllTextAsync(Journal, Ct));
    }

    [Fact]
    public async Task CommittingPublishesUnderItsNameAndForgetsThePart()
    {
        var part = await ReserveWithAudioAsync();

        var saved = await _store.CommitAsync(part, Base, Ct);

        Assert.Equal(Base + ".mp3", saved.Value.FileName);
        Assert.True(File.Exists(saved.Value.Path));
        Assert.False(File.Exists(part));
        Assert.False(File.Exists(Journal));
    }

    [Fact]
    public async Task ATakenNameGetsANumberAndNothingIsOverwritten()
    {
        Directory.CreateDirectory(_folder);
        var existing = Path.Combine(_folder, Base + ".mp3");
        await File.WriteAllTextAsync(existing, "earlier recording", Ct);
        var part = await ReserveWithAudioAsync();

        var saved = await _store.CommitAsync(part, Base, Ct);

        Assert.Equal(Base + " (2).mp3", saved.Value.FileName);
        Assert.Equal("earlier recording", await File.ReadAllTextAsync(existing, Ct));
    }

    [Fact]
    public async Task AFileThatIsNotMp3IsNotPublishedAndStays()
    {
        var part = await ReserveWithAudioAsync(new byte[4096]);

        var saved = await _store.CommitAsync(part, Base, Ct);

        Assert.Equal(ErrorCode.InvalidInput, saved.Error!.Code);
        Assert.True(File.Exists(part));
        Assert.True(File.Exists(Journal)); // the next start's recovery still knows about it
    }

    [Fact]
    public async Task AnEmptyRecordingIsNotPublished()
    {
        var part = await ReserveWithAudioAsync([]);

        var saved = await _store.CommitAsync(part, Base, Ct);

        Assert.Equal(ErrorCode.InvalidInput, saved.Error!.Code);
        Assert.False(File.Exists(part));
    }

    [Fact]
    public async Task AbandoningRemovesOnlyAnEmptyPart()
    {
        var empty = await ReserveWithAudioAsync([]);
        await _store.AbandonAsync(empty, Ct);
        Assert.False(File.Exists(empty));
        Assert.False(File.Exists(Journal));

        var withAudio = await ReserveWithAudioAsync();
        await _store.AbandonAsync(withAudio, Ct);
        Assert.True(File.Exists(withAudio));
    }

    [Fact]
    public async Task APartLeftByACrashIsRecoveredOnceUnderARecoveredName()
    {
        await ReserveWithAudioAsync();
        var afterCrash = new RecordingFileStore(_appData, _folder);

        var found = await afterCrash.RecoverAsync(Ct);

        Assert.Equal(RecoveryOutcome.Recovered, found.Value!.Outcome);
        Assert.Equal(Base + " (recovered).mp3", Path.GetFileName(found.Value.Path));
        Assert.True(File.Exists(found.Value.Path));
        Assert.Null((await afterCrash.RecoverAsync(Ct)).Value);
    }

    [Fact]
    public async Task APartThatDoesNotPlayIsLeftWhereItIsAndReported()
    {
        var part = await ReserveWithAudioAsync(new byte[4096]);

        var found = await new RecordingFileStore(_appData, _folder).RecoverAsync(Ct);

        Assert.Equal(RecoveryOutcome.KeptAsItIs, found.Value!.Outcome);
        Assert.Equal(part, found.Value.Path);
        Assert.True(File.Exists(part));
    }

    [Fact]
    public async Task NothingToRepairIsNull() => Assert.Null((await _store.RecoverAsync(Ct)).Value);

    [Theory]
    [InlineData(@"C:\Windows\notepad.exe")]
    [InlineData(@"C:\important\document.docx")]
    [InlineData("relative.lunar-part")]
    public async Task AJournalIsOnlyAHintAndCanNeverPointAtAnotherFile(string target)
    {
        Directory.CreateDirectory(_appData);
        await File.WriteAllTextAsync(Journal, target + "\n" + Base, Ct);

        var found = await _store.RecoverAsync(Ct);

        Assert.Null(found.Value);
        Assert.False(File.Exists(Journal));
    }

    [Fact]
    public async Task AGarbageJournalIsDropped()
    {
        Directory.CreateDirectory(_appData);
        await File.WriteAllBytesAsync(Journal, new byte[100_000], Ct);

        Assert.Null((await _store.RecoverAsync(Ct)).Value);
        Assert.False(File.Exists(Journal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData(@"\\server\share\takes")]
    [InlineData(@"C:\takes:stream")]
    public void FoldersThatCannotBeUsedAreRefused(string folder) =>
        Assert.Equal(ErrorCode.InvalidInput, _store.ValidateFolder(folder).Error!.Code);

    [Fact]
    public void AnOrdinaryFolderIsFineEvenBeforeItExists() => Assert.True(_store.ValidateFolder(_folder).IsSuccess);

    [Fact]
    public void ALinkIsRefusedAsAFolder()
    {
        var target = Directory.CreateDirectory(Path.Combine(_root, "real")).FullName;
        var link = Path.Combine(_root, "link");
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (IOException)
        {
            Assert.Skip("This account may not create symbolic links.");
        }
        catch (UnauthorizedAccessException)
        {
            Assert.Skip("This account may not create symbolic links.");
        }

        Assert.Equal(ErrorCode.InvalidInput, _store.ValidateFolder(link).Error!.Code);
    }

    [Fact]
    public void FreeSpaceIsReadFromTheRealDrive() => Assert.True(_store.GetFreeBytes(_folder).Value > 0);

    [Fact]
    public void MessagesCarryNoPath()
    {
        var error = _store.ValidateFolder(@"\\server\private-share").Error!;

        Assert.DoesNotContain("private-share", error.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }
}
