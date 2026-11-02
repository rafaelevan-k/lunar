using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Domain.Shared;
using Lunar.Infrastructure.Logging;
using Xunit;

namespace Lunar.Infrastructure.Tests.Logging;

// Real files in a temporary directory; nothing touches the user's %LocalAppData%\Lunar\Logs.
public sealed class SerilogDiagnosticLogTests : IDisposable
{
    // The sink checks the size before a write, so a file may exceed the limit by at most one line.
    // A line is bounded: three 64-character tokens, a short code, timestamp, versions.
    private const int MaxLineBytes = 512;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Lunar.Tests." + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private string[] LogFiles() =>
        Directory.Exists(_directory) ? Directory.GetFiles(_directory, "lunar-*.log") : [];

    private string AllText() => string.Concat(LogFiles().Select(File.ReadAllText));

    private static DiagnosticEvent Event(DiagnosticLevel level, string id = "test.event", string component = "Tests", string operation = "Run") =>
        new(level, id, component, operation);

    [Fact]
    public void NormalModeWritesInformationWarningAndErrorButNotDebug()
    {
        using (var log = new SerilogDiagnosticLog(_directory, diagnosticsEnabled: false))
        {
            Assert.True(log.IsOperational);
            log.Write(Event(DiagnosticLevel.Debug, "debug.event"));
            log.Write(Event(DiagnosticLevel.Information, "info.event"));
            log.Write(Event(DiagnosticLevel.Warning, "warn.event"));
            log.Write(Event(DiagnosticLevel.Error, "error.event"));
        }

        var text = AllText();
        Assert.DoesNotContain("debug.event", text);
        Assert.Contains("info.event", text);
        Assert.Contains("warn.event", text);
        Assert.Contains("error.event", text);
    }

    [Fact]
    public void DiagnosticModeAlsoWritesDebug()
    {
        using (var log = new SerilogDiagnosticLog(_directory, diagnosticsEnabled: true))
            log.Write(Event(DiagnosticLevel.Debug, "debug.event"));

        Assert.Contains("debug.event", AllText());
    }

    [Fact]
    public void LineContainsOnlyTheAllowedFields()
    {
        using (var log = new SerilogDiagnosticLog(_directory, diagnosticsEnabled: false))
        {
            log.Write(new DiagnosticEvent(DiagnosticLevel.Error, "volume.set.failed", "Volume", "SetVolume",
                ErrorCode.DeviceUnavailable, TimeSpan.FromMilliseconds(42), typeof(InvalidOperationException)));
        }

        var line = AllText().Trim();
        Assert.Contains("[ERR]", line);
        Assert.Contains("volume.set.failed Volume.SetVolume", line);
        Assert.Contains("code=DeviceUnavailable", line);
        Assert.Contains("ms=42", line);
        Assert.Contains("ex=InvalidOperationException", line);
        Assert.Contains("v=", line);
        Assert.Contains("os=", line);
    }

    [Theory]
    [InlineData(@"C:\Users\alice\Documents\secret plan.docx")]
    [InlineData("alice@example.com")]
    [InlineData("My Home WiFi 5G")]
    [InlineData("/home/alice/notes.txt")]
    [InlineData("Song: Private Title")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("-leading-dash")]
    public void SensitiveOrMalformedTextIsReplacedByRedacted(string hostile)
    {
        using (var log = new SerilogDiagnosticLog(_directory, diagnosticsEnabled: false))
        {
            log.Write(new DiagnosticEvent(DiagnosticLevel.Warning, hostile, hostile, hostile));
        }

        var text = AllText();
        Assert.Contains("redacted.redacted", text);
        foreach (var fragment in new[] { "alice", "secret", "example", "WiFi", "Private", "notes", "Documents" })
            Assert.DoesNotContain(fragment, text);
    }

    [Fact]
    public void OverlongTokenIsRedactedAndLimitLengthIsKept()
    {
        var limit = new string('a', 64);
        var over = new string('a', 65);

        Assert.Equal(limit, SerilogDiagnosticLog.Token(limit));
        Assert.Equal("redacted", SerilogDiagnosticLog.Token(over));
        Assert.Equal("redacted", SerilogDiagnosticLog.Token(null));
        Assert.Equal("a.b_c-1", SerilogDiagnosticLog.Token("a.b_c-1"));
    }

    [Fact]
    public void OnlyTheExceptionTypeNameIsRecordedNeverItsMessage()
    {
        using (var log = new SerilogDiagnosticLog(_directory, diagnosticsEnabled: false))
        {
            // An exception message such as a user's file path cannot be passed at all; only the type can.
            log.Write(new DiagnosticEvent(DiagnosticLevel.Error, "io.failed", "Test", "Read", ErrorCode.IoFailure,
                ExceptionType: typeof(FileNotFoundException)));
            log.Write(new DiagnosticEvent(DiagnosticLevel.Error, "generic.failed", "Test", "Read",
                ExceptionType: typeof(List<string>)));
        }

        var text = AllText();
        Assert.Contains("ex=FileNotFoundException", text);
        Assert.Contains("ex=List v=", text);
        Assert.DoesNotContain("alice", text);
        Assert.DoesNotContain("secret", text);
    }

    [Fact]
    public void WritingNeverThrowsForNullBlankOrDisposedLogs()
    {
        var log = new SerilogDiagnosticLog(_directory, diagnosticsEnabled: false);

        log.Write(null!);
        log.Write(new DiagnosticEvent((DiagnosticLevel)99, null!, null!, null!));
        log.Dispose();
        log.Dispose();
        log.Write(Event(DiagnosticLevel.Error));

        Assert.False(log.IsOperational);
        Assert.True(log.DroppedEvents >= 2);
    }

    [Fact]
    public void UnusableDirectoryDisablesLoggingInsteadOfFailing()
    {
        Directory.CreateDirectory(_directory);
        var blocker = Path.Combine(_directory, "not-a-directory");
        File.WriteAllText(blocker, "x");

        using var log = new SerilogDiagnosticLog(Path.Combine(blocker, "Logs"), diagnosticsEnabled: false);
        log.Write(Event(DiagnosticLevel.Error));

        Assert.False(log.IsOperational);
        Assert.NotNull(log.InitializationFailure);
        Assert.Equal(1, log.DroppedEvents);
    }

    [Fact]
    public void StorageIsBoundedByFileSizeAndFileCount()
    {
        const int sizeLimit = 2048;
        const int retained = 3;
        using (var log = new SerilogDiagnosticLog(_directory, diagnosticsEnabled: false, sizeLimit, retained))
        {
            for (var i = 0; i < 2000; i++)
                log.Write(Event(DiagnosticLevel.Information, "bounded.storage.event"));
        }

        var files = LogFiles();
        Assert.InRange(files.Length, 1, retained);
        Assert.All(files, file => Assert.True(new FileInfo(file).Length <= sizeLimit + MaxLineBytes,
            $"{Path.GetFileName(file)} is {new FileInfo(file).Length} bytes, limit {sizeLimit} + one line"));
    }

    [Fact]
    public async Task ConcurrentWritersLoseNoLines()
    {
        const int threads = 4;
        const int perThread = 100;
        using (var log = new SerilogDiagnosticLog(_directory, diagnosticsEnabled: false))
        {
            await Task.WhenAll(Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < perThread; i++)
                    log.Write(Event(DiagnosticLevel.Information, "concurrent.event"));
            }, TestContext.Current.CancellationToken)));
        }

        var lines = AllText().Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(l => l.Contains("concurrent.event"));
        Assert.Equal(threads * perThread, lines);
    }

    [Fact]
    public void EventsAreOnDiskBeforeDisposeSoACrashLosesNothing()
    {
        using var log = new SerilogDiagnosticLog(_directory, diagnosticsEnabled: false);

        log.Write(Event(DiagnosticLevel.Error, "crash.safe.event"));

        // Read through a shared handle while the logger is still open.
        var file = Assert.Single(LogFiles());
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        Assert.Contains("crash.safe.event", reader.ReadToEnd());
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyTheExactValueOneOptsInToDiagnostics(string? value, bool expected)
    {
        Assert.Equal(expected, LoggingConfiguration.IsDiagnosticsRequested(value));
    }

    [Fact]
    public void DefaultDirectoryIsUnderLocalAppDataAndLimitsAreBounded()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lunar", "Logs");

        Assert.Equal(expected, LoggingConfiguration.DefaultDirectory);
        Assert.Equal(7L * 1024 * 1024, LoggingConfiguration.NormalFileSizeLimit * LoggingConfiguration.RetainedFileCount);
        Assert.Equal(35L * 1024 * 1024, LoggingConfiguration.DiagnosticFileSizeLimit * LoggingConfiguration.RetainedFileCount);
    }
}
