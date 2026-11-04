using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Conversion.Pipeline;
using Lunar.Conversion.Tests.Support;
using Xunit;

namespace Lunar.Conversion.Tests.Pipeline;

// BE-26: the private working folder of a job and its cleanup.
public sealed class TempWorkspaceTests : IDisposable
{
    private sealed class RecordingLog : IDiagnosticLog
    {
        public List<DiagnosticEvent> Events { get; } = [];

        public void Write(DiagnosticEvent diagnosticEvent)
        {
            lock (Events)
                Events.Add(diagnosticEvent);
        }
    }

    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private string Root => Path.Combine(_folder.Root, "workspaces");

    private static string Part() => TempWorkspace.NewPartFileName();

    [Fact]
    public void ACreatedWorkspaceIsAnEmptyUniqueFolderNamedByARandomIdentifier()
    {
        using var first = TempWorkspace.Create(Root);
        using var second = TempWorkspace.Create(Root);

        Assert.True(Directory.Exists(first.Directory));
        Assert.Empty(Directory.GetFileSystemEntries(first.Directory));
        Assert.NotEqual(first.Directory, second.Directory);
        Assert.Matches(@"^job-[0-9a-f]{32}$", Path.GetFileName(first.Directory));
        Assert.Equal(Root, Path.GetDirectoryName(first.Directory));
    }

    [Fact]
    public void TheRootIsCreatedWhenItIsMissing()
    {
        var root = Path.Combine(_folder.Root, "a", "b", "c");

        using var workspace = TempWorkspace.Create(root);

        Assert.True(Directory.Exists(workspace.Directory));
    }

    [Fact]
    public void NewFilePathsAreInsideTheFolderUniqueAndNamedWithoutAnythingFromTheUser()
    {
        using var workspace = TempWorkspace.Create(Root);

        var paths = Enumerable.Range(0, 50).Select(_ => workspace.NewFilePath(".png")).ToList();

        Assert.Equal(50, paths.Distinct().Count());
        Assert.All(paths, p =>
        {
            Assert.True(workspace.Owns(p));
            Assert.Matches(@"^[0-9a-f]{32}\.png$", Path.GetFileName(p));
        });
        Assert.All(paths, p => Assert.False(File.Exists(p))); // a path is made, not a file
    }

    [Theory]
    [InlineData("png")]
    [InlineData(".PNG")]
    [InlineData("../x")]
    [InlineData(".png/..")]
    [InlineData(".a b")]
    [InlineData(".")]
    [InlineData(".abcdefghijklmnopqrstuvwxyz")]
    public void AnExtensionThatIsNotADotAndAFewLowerCaseLettersIsRefused(string extension)
    {
        using var workspace = TempWorkspace.Create(Root);

        Assert.ThrowsAny<ArgumentException>(() => workspace.NewFilePath(extension));
    }

    [Fact]
    public void OwnsMeansAFileDirectlyInsideTheFolderAndNothingElse()
    {
        using var workspace = TempWorkspace.Create(Root);
        using var other = TempWorkspace.Create(Root);

        Assert.True(workspace.Owns(Path.Combine(workspace.Directory, "x.png")));
        Assert.False(workspace.Owns(Path.Combine(other.Directory, "x.png")));
        Assert.False(workspace.Owns(Path.Combine(workspace.Directory, "sub", "x.png")));
        Assert.False(workspace.Owns(Path.Combine(workspace.Directory, "..", "x.png")));
        Assert.False(workspace.Owns(Path.Combine(workspace.Directory, "..", Path.GetFileName(workspace.Directory) + "-evil", "x.png")));
        Assert.False(workspace.Owns(workspace.Directory));
        Assert.False(workspace.Owns(@"C:\Windows\notepad.exe"));
        Assert.False(workspace.Owns(""));
        Assert.False(workspace.Owns("a\0b"));
    }

    [Fact]
    public void DisposingRemovesTheFolderAndEverythingInIt()
    {
        var workspace = TempWorkspace.Create(Root);
        File.WriteAllBytes(workspace.NewFilePath(".png"), new byte[100]);
        Directory.CreateDirectory(Path.Combine(workspace.Directory, "sub"));
        File.WriteAllBytes(Path.Combine(workspace.Directory, "sub", "deep.bin"), new byte[10]);

        workspace.Dispose();

        Assert.False(Directory.Exists(workspace.Directory));
        Assert.True(Directory.Exists(Root)); // only its own folder goes
    }

    [Fact]
    public void DisposingTwiceIsHarmlessAndAWorkspaceCannotBeUsedAfterwards()
    {
        var workspace = TempWorkspace.Create(Root);

        workspace.Dispose();
        workspace.Dispose();

        Assert.Throws<ObjectDisposedException>(() => workspace.NewFilePath(".png"));
    }

    [Fact]
    public void AFileHeldByAnotherProgramDoesNotMakeDisposeThrowAndIsLeftForRecovery()
    {
        var log = new RecordingLog();
        var workspace = TempWorkspace.Create(Root, log);
        var path = workspace.NewFilePath(".png");
        File.WriteAllBytes(path, new byte[10]);
        var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        workspace.Dispose();

        Assert.True(Directory.Exists(workspace.Directory)); // could not go
        lock (log.Events)
        {
            var warning = Assert.Single(log.Events);
            Assert.Equal("conversion.workspace.cleanup_failed", warning.EventId);
            Assert.Equal(Lunar.Domain.Shared.ErrorCode.IoFailure, warning.Error);
        }

        hold.Dispose();
        Assert.Equal(1, TempWorkspace.RecoverAbandoned(Root));
        Assert.False(Directory.Exists(workspace.Directory));
    }

    [Fact]
    public void RecoveryRemovesOnlyFoldersThatLookLikeWorkspaces()
    {
        Directory.CreateDirectory(Root);
        var mine = Directory.CreateDirectory(Path.Combine(Root, "job-" + new string('a', 32))).FullName;
        var alsoMine = Directory.CreateDirectory(Path.Combine(Root, "job-" + new string('0', 32))).FullName;
        var notMine1 = Directory.CreateDirectory(Path.Combine(Root, "job-short")).FullName;
        var notMine2 = Directory.CreateDirectory(Path.Combine(Root, "important")).FullName;
        var notMine3 = Directory.CreateDirectory(Path.Combine(Root, "job-" + new string('C', 32))).FullName; // upper case: not made by Lunar
        File.WriteAllText(Path.Combine(mine, "x.tmp"), "x");
        File.WriteAllText(Path.Combine(Root, "loose-file.txt"), "keep");

        var removed = TempWorkspace.RecoverAbandoned(Root);

        Assert.Equal(2, removed);
        Assert.False(Directory.Exists(mine));
        Assert.False(Directory.Exists(alsoMine));
        Assert.True(Directory.Exists(notMine1));
        Assert.True(Directory.Exists(notMine2));
        Assert.True(Directory.Exists(notMine3));
        Assert.True(File.Exists(Path.Combine(Root, "loose-file.txt")));
    }

    [Fact]
    public void RecoveryOfAMissingRootIsZero()
    {
        Assert.Equal(0, TempWorkspace.RecoverAbandoned(Path.Combine(_folder.Root, "nothing-here")));
    }

    [Fact]
    public void RecoveryDoesNotFollowALinkNamedLikeAWorkspace()
    {
        Directory.CreateDirectory(Root);
        var target = Directory.CreateDirectory(Path.Combine(_folder.Root, "precious")).FullName;
        File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");
        var link = Path.Combine(Root, "job-" + new string('b', 32));
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target })
            start.ArgumentList.Add(argument);
        using (var process = System.Diagnostics.Process.Start(start)!)
        {
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, "The test could not create a junction on this machine.");
        }

        var removed = TempWorkspace.RecoverAbandoned(Root);

        Assert.Equal(0, removed);
        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
        Directory.Delete(link); // removes the link only
    }

    // ---- the journal of part files -----------------------------------------------------------------------------------

    [Fact]
    public void DisposingRemovesAPartFileTheOutputStepLeftBehindInAUserFolder()
    {
        var workspace = TempWorkspace.Create(Root);
        var part = Path.Combine(_folder.Out, Part());
        workspace.RecordPart(part);
        File.WriteAllBytes(part, new byte[10]);

        workspace.Dispose();

        Assert.False(File.Exists(part));
    }

    [Fact]
    public void RecoveryAfterACrashRemovesTheJournalledPartFilesAndTheFolder()
    {
        var workspace = TempWorkspace.Create(Root);
        var part1 = Path.Combine(_folder.Out, Part());
        var part2 = Path.Combine(_folder.Out, Part());
        workspace.RecordPart(part1);
        workspace.RecordPart(part2);
        File.WriteAllBytes(part1, new byte[10]);
        File.WriteAllBytes(part2, new byte[10]);
        // the app dies here: no Dispose

        var removed = TempWorkspace.RecoverAbandoned(Root);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(part1));
        Assert.False(File.Exists(part2));
        Assert.False(Directory.Exists(workspace.Directory));
    }

    [Fact]
    public void AJournalCanNeverMakeLunarDeleteAFileThatIsNotAPartFile()
    {
        var workspace = TempWorkspace.Create(Root);
        var precious = _folder.Write("precious.docx", [1, 2, 3], _folder.Out);
        var lookalike = _folder.Write("~short.lunar-part", [1], _folder.Out);
        var wrongHex = _folder.Write("~" + new string('G', 32) + ".lunar-part", [1], _folder.Out);
        var upper = _folder.Write("~" + new string('A', 32) + ".lunar-part", [1], _folder.Out);
        File.WriteAllLines(Path.Combine(workspace.Directory, "journal.txt"),
        [
            precious,
            lookalike,
            wrongHex,
            upper,
            @"relative\~" + new string('a', 32) + ".lunar-part",
            "~" + new string('a', 32) + ".lunar-part",
            "",
            "garbage \0 line"
        ]);

        workspace.Dispose();

        Assert.True(File.Exists(precious));
        Assert.True(File.Exists(lookalike));
        Assert.True(File.Exists(wrongHex));
        Assert.True(File.Exists(upper));
    }

    [Fact]
    public void OnlyNamesTheOutputStepMakesCanBeRecorded()
    {
        using var workspace = TempWorkspace.Create(Root);

        Assert.Throws<ArgumentException>(() => workspace.RecordPart(Path.Combine(_folder.Out, "photo.jpg")));
        Assert.Throws<ArgumentException>(() => workspace.RecordPart("~" + new string('a', 32) + ".lunar-part"));
        workspace.RecordPart(Path.Combine(_folder.Out, Part()));
    }

    [Theory]
    [InlineData("~0123456789abcdef0123456789abcdef.lunar-part", true)]
    [InlineData("~0123456789abcdef0123456789abcde.lunar-part", false)]
    [InlineData("0123456789abcdef0123456789abcdef.lunar-part", false)]
    [InlineData("~0123456789abcdef0123456789abcdef.lunar-part.exe", false)]
    [InlineData("~0123456789ABCDEF0123456789ABCDEF.lunar-part", false)]
    [InlineData("photo.jpg", false)]
    [InlineData("", false)]
    public void PartFileNamesFollowOneStrictPattern(string name, bool expected)
    {
        Assert.Equal(expected, TempWorkspace.IsPartFileName(name));
    }

    [Fact]
    public void GeneratedPartNamesMatchThePatternAndAreShortEnoughForAnyFolder()
    {
        var names = Enumerable.Range(0, 100).Select(_ => TempWorkspace.NewPartFileName()).ToList();

        Assert.Equal(100, names.Distinct().Count());
        Assert.All(names, n =>
        {
            Assert.True(TempWorkspace.IsPartFileName(n));
            Assert.True(n.Length < 50);
        });
    }
}
