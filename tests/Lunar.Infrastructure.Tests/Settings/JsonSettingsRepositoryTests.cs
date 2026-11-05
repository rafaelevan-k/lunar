using System.Text;
using System.Text.Json.Nodes;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Settings;
using Lunar.Application.Settings.Models;
using Lunar.Domain.Shared;
using Lunar.Infrastructure.Settings;
using Xunit;

namespace Lunar.Infrastructure.Tests.Settings;

// Real files in a temporary directory; nothing touches the user's %LocalAppData%\Lunar.
public sealed class JsonSettingsRepositoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Lunar.Tests." + Guid.NewGuid().ToString("N"));
    private readonly RecordingLog _log = new();
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables)
            disposable.Dispose();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string MainPath => Path.Combine(_directory, JsonSettingsRepository.FileName);

    private string BackupPath => Path.Combine(_directory, JsonSettingsRepository.BackupName);

    private string TempPath => Path.Combine(_directory, JsonSettingsRepository.TempName);

    private string CorruptPath => Path.Combine(_directory, JsonSettingsRepository.CorruptName);

    private JsonSettingsRepository Create(SettingsMigration? migration = null)
    {
        var repository = new JsonSettingsRepository(_directory, _log, migration ?? new SettingsMigration());
        _disposables.Add(repository);
        return repository;
    }

    private void WriteRaw(string path, string text) => WriteRaw(path, Encoding.UTF8.GetBytes(text));

    private void WriteRaw(string path, byte[] bytes)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(path, bytes);
    }

    private static string OutputFolder() => Path.Combine(Path.GetTempPath(), "LunarOutput");

    private static LunarSettings Custom(int offset = 30) => new()
    {
        LaunchAtStartup = true,
        Locale = "id-ID",
        Island = new IslandSettings { PreferredMonitorId = "\\\\.\\DISPLAY2", TopOffsetDip = offset },
        Appearance = new AppearanceSettings
        {
            Theme = ThemePreference.Dark,
            ReduceMotion = true,
            EyeAnimationEnabled = false,
            AutoCollapse = false
        },
        Media = new MediaSettings { PreferredSessionId = "app.music" },
        Conversion = new ConversionSettings { OutputDirectory = OutputFolder() },
        Privacy = new PrivacySettings { DiagnosticsEnabled = true },
        Usage = new UsageSettings { Enabled = true }
    };

    private static string ValidJson(int offset = 12) => $$"""
        { "schemaVersion": 1, "island": { "topOffsetDip": {{offset}} } }
        """;

    [Fact]
    public async Task MissingFileGivesDefaultsAndWritesNothing()
    {
        var result = await Create().LoadAsync(Token);

        Assert.True(result.IsSuccess);
        Assert.Equal(SettingsRecovery.CreatedDefaults, result.Value.Recovery);
        Assert.Equal(LunarSettings.Defaults, result.Value.Settings);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task SavedSettingsRoundTripWithoutRecovery()
    {
        var repository = Create();

        var saved = await repository.SaveAsync(Custom(), Token);
        var loaded = await repository.LoadAsync(Token);

        Assert.True(saved.IsSuccess);
        Assert.True(loaded.IsSuccess);
        Assert.Equal(SettingsRecovery.None, loaded.Value.Recovery);
        Assert.Equal(Custom(), loaded.Value.Settings);
        Assert.False(File.Exists(TempPath));
    }

    [Fact]
    public async Task FileHasVersionOneAndNoByteOrderMark()
    {
        await Create().SaveAsync(Custom(), Token);

        var bytes = await File.ReadAllBytesAsync(MainPath, Token);
        var root = (JsonObject)JsonNode.Parse(bytes)!;

        Assert.False(bytes.AsSpan().StartsWith("\uFEFF"u8));
        Assert.Equal(1, (int)root["schemaVersion"]!);
        Assert.Equal("Dark", (string)root["appearance"]!["theme"]!);
    }

    [Fact]
    public async Task TheUsagePageIsOffInAFileThatPredatesItAndAWrongTypeIsRepairedToOff()
    {
        // An older file has no "usage" section: additive, so the default (off) applies and nothing is reported as repaired.
        WriteRaw(MainPath, ValidJson());
        var older = await Create().LoadAsync(Token);
        Assert.False(older.Value.Settings.Usage.Enabled);
        Assert.Equal(SettingsRecovery.None, older.Value.Recovery);

        // A value of the wrong type never turns the network feature on.
        WriteRaw(MainPath, """{ "schemaVersion": 1, "usage": { "enabled": "yes" } }""");
        var wrong = await Create().LoadAsync(Token);
        Assert.False(wrong.Value.Settings.Usage.Enabled);
        Assert.Equal(SettingsRecovery.ValuesReset, wrong.Value.Recovery);
    }

    [Fact]
    public async Task SecondSaveKeepsThePreviousFileAsBackup()
    {
        var repository = Create();
        await repository.SaveAsync(Custom(10), Token);
        await repository.SaveAsync(Custom(20), Token);

        var text = await File.ReadAllTextAsync(BackupPath, Token);
        Assert.Contains("\"topOffsetDip\": 10", text, StringComparison.Ordinal);
        Assert.Contains("\"topOffsetDip\": 20", await File.ReadAllTextAsync(MainPath, Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveNormalizesBeforeWriting()
    {
        var repository = Create();
        var settings = new LunarSettings { Island = new IslandSettings { TopOffsetDip = 5000 } };

        Assert.True((await repository.SaveAsync(settings, Token)).IsSuccess);
        var loaded = await repository.LoadAsync(Token);

        Assert.Equal(SettingsRecovery.None, loaded.Value.Recovery);
        Assert.Equal(8, loaded.Value.Settings.Island.TopOffsetDip);
    }

    [Fact]
    public async Task SaveRejectsNullSettings() =>
        await Assert.ThrowsAsync<ArgumentNullException>(() => Create().SaveAsync(null!, Token));

    public static TheoryData<string> CorruptContents => new()
    {
        "",
        "   ",
        "not json at all",
        "{ \"schemaVersion\": 1, ",
        "[]",
        "42",
        "null",
        "{ }",
        "{ \"schemaVersion\": 0 }",
        "{ \"schemaVersion\": \"1\" }",
        "{ \"schemaVersion\": 1 } trailing",
        "\0\0\0\u0001\u0002binary"
    };

    [Theory]
    [MemberData(nameof(CorruptContents))]
    public async Task CorruptMainFileRecoversToDefaultsAndIsKept(string content)
    {
        WriteRaw(MainPath, content);

        var result = await Create().LoadAsync(Token);

        Assert.True(result.IsSuccess);
        Assert.Equal(SettingsRecovery.ResetToDefaults, result.Value.Recovery);
        Assert.Equal(LunarSettings.Defaults, result.Value.Settings);
        Assert.Equal(content, await File.ReadAllTextAsync(CorruptPath, Token));
        Assert.Contains(_log.Events, e => e.EventId == "settings.load.reset");
    }

    [Fact]
    public async Task OversizedFileIsTreatedAsCorrupt()
    {
        WriteRaw(MainPath, new string(' ', JsonSettingsRepository.MaxFileBytes) + ValidJson());

        var result = await Create().LoadAsync(Token);

        Assert.Equal(SettingsRecovery.ResetToDefaults, result.Value.Recovery);
    }

    [Fact]
    public async Task FileAtTheSizeLimitIsStillRead()
    {
        var json = ValidJson(15);
        WriteRaw(MainPath, json + new string(' ', JsonSettingsRepository.MaxFileBytes - json.Length));
        Assert.Equal(JsonSettingsRepository.MaxFileBytes, new FileInfo(MainPath).Length);

        var result = await Create().LoadAsync(Token);

        Assert.Equal(SettingsRecovery.None, result.Value.Recovery);
        Assert.Equal(15, result.Value.Settings.Island.TopOffsetDip);
    }

    [Fact]
    public async Task DeeplyNestedJsonIsTreatedAsCorrupt()
    {
        WriteRaw(MainPath, "{\"schemaVersion\":1,\"x\":" + new string('[', 200) + new string(']', 200) + "}");

        var result = await Create().LoadAsync(Token);

        Assert.True(result.IsSuccess);
        Assert.Equal(SettingsRecovery.ResetToDefaults, result.Value.Recovery);
    }

    [Fact]
    public async Task CorruptMainFileFallsBackToTheBackup()
    {
        var repository = Create();
        await repository.SaveAsync(Custom(10), Token);
        await repository.SaveAsync(Custom(20), Token);
        WriteRaw(MainPath, "{ broken");

        var result = await repository.LoadAsync(Token);

        Assert.Equal(SettingsRecovery.RestoredFromBackup, result.Value.Recovery);
        Assert.Equal(10, result.Value.Settings.Island.TopOffsetDip);
        Assert.Equal("{ broken", await File.ReadAllTextAsync(CorruptPath, Token));
    }

    [Fact]
    public async Task MissingMainFileFallsBackToTheBackup()
    {
        WriteRaw(BackupPath, ValidJson(17));

        var result = await Create().LoadAsync(Token);

        Assert.Equal(SettingsRecovery.RestoredFromBackup, result.Value.Recovery);
        Assert.Equal(17, result.Value.Settings.Island.TopOffsetDip);
        Assert.False(File.Exists(CorruptPath));
    }

    [Fact]
    public async Task SavingOverACorruptFileKeepsTheLastGoodBackup()
    {
        var repository = Create();
        await repository.SaveAsync(Custom(10), Token);
        await repository.SaveAsync(Custom(20), Token); // backup now holds 10
        WriteRaw(MainPath, "{ broken");

        Assert.True((await repository.SaveAsync(Custom(30), Token)).IsSuccess);

        Assert.Contains("\"topOffsetDip\": 10", await File.ReadAllTextAsync(BackupPath, Token), StringComparison.Ordinal);
        var loaded = await repository.LoadAsync(Token);
        Assert.Equal(SettingsRecovery.None, loaded.Value.Recovery);
        Assert.Equal(30, loaded.Value.Settings.Island.TopOffsetDip);
    }

    [Fact]
    public async Task WrongTypedValueIsResetAndSiblingsAreKept()
    {
        WriteRaw(MainPath, """
            { "schemaVersion": 1, "launchAtStartup": "yes",
              "island": { "topOffsetDip": 22, "preferredMonitorId": 5 },
              "appearance": { "reduceMotion": true, "theme": "Light" } }
            """);

        var result = await Create().LoadAsync(Token);

        Assert.Equal(SettingsRecovery.ValuesReset, result.Value.Recovery);
        var settings = result.Value.Settings;
        Assert.False(settings.LaunchAtStartup);
        Assert.Null(settings.Island.PreferredMonitorId);
        Assert.Equal(22, settings.Island.TopOffsetDip);
        Assert.True(settings.Appearance.ReduceMotion);
        Assert.Equal(ThemePreference.Light, settings.Appearance.Theme);
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 1, "island": 7 }""")]
    [InlineData("""{ "schemaVersion": 1, "appearance": [] }""")]
    [InlineData("""{ "schemaVersion": 1, "island": { "topOffsetDip": 9999 } }""")]
    [InlineData("""{ "schemaVersion": 1, "island": { "topOffsetDip": 1.5 } }""")]
    [InlineData("""{ "schemaVersion": 1, "appearance": { "theme": "banana" } }""")]
    [InlineData("""{ "schemaVersion": 1, "appearance": { "theme": "1" } }""")]
    [InlineData("""{ "schemaVersion": 1, "appearance": { "theme": 1 } }""")]
    [InlineData("""{ "schemaVersion": 1, "locale": "en_US" }""")]
    [InlineData("""{ "schemaVersion": 1, "conversion": { "outputDirectory": "relative\\folder" } }""")]
    [InlineData("""{ "schemaVersion": 1, "conversion": { "outputDirectory": "\\\\server\\share\\out" } }""")]
    [InlineData("""{ "schemaVersion": 1, "conversion": { "outputDirectory": "//server/share/out" } }""")]
    public async Task InvalidValuesAreReportedAsValuesReset(string json)
    {
        WriteRaw(MainPath, json);

        var result = await Create().LoadAsync(Token);

        Assert.Equal(SettingsRecovery.ValuesReset, result.Value.Recovery);
        Assert.Equal(LunarSettings.Defaults, result.Value.Settings);
        Assert.Contains(_log.Events, e => e.EventId == "settings.load.values.reset");
    }

    [Fact]
    public async Task AbsoluteOutputFolderIsAccepted()
    {
        var folder = OutputFolder();
        WriteRaw(MainPath, new JsonObject
        {
            ["schemaVersion"] = 1,
            ["conversion"] = new JsonObject { ["outputDirectory"] = folder }
        }.ToJsonString());

        var result = await Create().LoadAsync(Token);

        Assert.Equal(SettingsRecovery.None, result.Value.Recovery);
        Assert.Equal(folder, result.Value.Settings.Conversion.OutputDirectory);
    }

    [Fact]
    public async Task MissingFieldsMeanDefaultsWithoutRecovery()
    {
        WriteRaw(MainPath, """{ "schemaVersion": 1 }""");

        var result = await Create().LoadAsync(Token);

        Assert.Equal(SettingsRecovery.None, result.Value.Recovery);
        Assert.Equal(LunarSettings.Defaults, result.Value.Settings);
    }

    [Fact]
    public async Task ExplicitNullsMeanDefaultsWithoutRecovery()
    {
        WriteRaw(MainPath, """{ "schemaVersion": 1, "locale": null, "island": { "preferredMonitorId": null } }""");

        var result = await Create().LoadAsync(Token);

        Assert.Equal(SettingsRecovery.None, result.Value.Recovery);
        Assert.Equal(LunarSettings.Defaults, result.Value.Settings);
    }

    [Fact]
    public async Task UnknownFieldsAreIgnoredAndDroppedOnTheNextSave()
    {
        WriteRaw(MainPath, """{ "schemaVersion": 1, "somethingNew": { "a": 1 }, "launchAtStartup": true }""");
        var repository = Create();

        var loaded = await repository.LoadAsync(Token);
        await repository.SaveAsync(loaded.Value.Settings, Token);

        Assert.Equal(SettingsRecovery.None, loaded.Value.Recovery);
        Assert.True(loaded.Value.Settings.LaunchAtStartup);
        Assert.DoesNotContain("somethingNew", await File.ReadAllTextAsync(MainPath, Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileWithByteOrderMarkIsRead()
    {
        WriteRaw(MainPath, [.. "\uFEFF"u8, .. Encoding.UTF8.GetBytes(ValidJson(44))]);

        var result = await Create().LoadAsync(Token);

        Assert.Equal(SettingsRecovery.None, result.Value.Recovery);
        Assert.Equal(44, result.Value.Settings.Island.TopOffsetDip);
    }

    [Fact]
    public async Task NewerVersionIsNeitherReadNorRewritten()
    {
        const string newer = """{ "schemaVersion": 2, "launchAtStartup": true, "future": 1 }""";
        WriteRaw(MainPath, newer);
        var repository = Create();

        var loaded = await repository.LoadAsync(Token);
        var saved = await repository.SaveAsync(Custom(), Token);

        Assert.Equal(SettingsRecovery.NewerVersionIgnored, loaded.Value.Recovery);
        Assert.Equal(LunarSettings.Defaults, loaded.Value.Settings);
        Assert.True(saved.IsFailure);
        Assert.Equal(ErrorCode.Unsupported, saved.Error!.Code);
        Assert.Equal(newer, await File.ReadAllTextAsync(MainPath, Token));
        Assert.False(File.Exists(BackupPath));
        Assert.False(File.Exists(CorruptPath));
    }

    [Fact]
    public async Task LeftoverTemporaryFileIsRemovedOnLoad()
    {
        WriteRaw(MainPath, ValidJson());
        WriteRaw(TempPath, "half written");

        await Create().LoadAsync(Token);

        Assert.False(File.Exists(TempPath));
    }

    [Fact]
    public async Task LeftoverTemporaryFileDoesNotBlockSave()
    {
        WriteRaw(TempPath, "half written");

        var saved = await Create().SaveAsync(Custom(), Token);

        Assert.True(saved.IsSuccess);
        Assert.False(File.Exists(TempPath));
    }

    [Fact]
    public async Task LockedMainFileFailsLoadAndSaveWithoutChangingIt()
    {
        WriteRaw(MainPath, ValidJson(12));
        var repository = Create();

        using (new FileStream(MainPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var loaded = await repository.LoadAsync(Token);
            var saved = await repository.SaveAsync(Custom(), Token);

            Assert.True(loaded.IsFailure);
            Assert.Equal(ErrorCode.IoFailure, loaded.Error!.Code);
            Assert.True(saved.IsFailure);
        }

        Assert.Equal(ValidJson(12), await File.ReadAllTextAsync(MainPath, Token));
        Assert.False(File.Exists(CorruptPath));
        Assert.Contains(_log.Events, e => e.EventId == "settings.load.failed" && e.Error == ErrorCode.IoFailure);
    }

    [Fact]
    public async Task FailedSaveLeavesTheOldFileIntact()
    {
        var repository = Create();
        await repository.SaveAsync(Custom(10), Token);
        var before = await File.ReadAllTextAsync(MainPath, Token);

        Result saved;
        using (new FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            saved = await repository.SaveAsync(Custom(20), Token);
        }

        Assert.True(saved.IsFailure);
        Assert.Equal(ErrorCode.IoFailure, saved.Error!.Code);
        Assert.Equal(before, await File.ReadAllTextAsync(MainPath, Token));
        Assert.Contains(_log.Events, e => e.EventId == "settings.save.failed");
    }

    [Fact]
    public async Task UnwritableFolderGivesAFailedSaveNotAnException()
    {
        // A file where the folder should be makes Directory.CreateDirectory fail.
        var blocker = Path.Combine(Path.GetTempPath(), "Lunar.Tests.Blocker." + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(blocker, "x", Token);
        try
        {
            using var repository = new JsonSettingsRepository(Path.Combine(blocker, "sub"), _log);

            var saved = await repository.SaveAsync(Custom(), Token);
            var loaded = await repository.LoadAsync(Token);

            Assert.True(saved.IsFailure);
            Assert.True(loaded.IsSuccess);
            Assert.Equal(SettingsRecovery.CreatedDefaults, loaded.Value.Recovery);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public async Task CancelledTokenCancelsBothOperationsWithoutTouchingDisk()
    {
        var repository = Create();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var loaded = await repository.LoadAsync(cancelled.Token);
        var saved = await repository.SaveAsync(Custom(), cancelled.Token);

        Assert.True(loaded.IsCancelled);
        Assert.True(saved.IsCancelled);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task ConcurrentSavesNeverLeaveAPartialFile()
    {
        var repository = Create();
        var offsets = Enumerable.Range(0, 25).Select(i => i * 7).ToArray();

        var results = await Task.WhenAll(offsets.Select(o => repository.SaveAsync(Custom(o), Token)));
        var loaded = await repository.LoadAsync(Token);

        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(SettingsRecovery.None, loaded.Value.Recovery);
        Assert.Contains(loaded.Value.Settings.Island.TopOffsetDip, offsets);
        Assert.False(File.Exists(TempPath));
    }

    [Fact]
    public async Task OlderVersionIsMigratedAndPersistedWithTheOriginalAsBackup()
    {
        var migration = new SettingsMigration(2, new Dictionary<int, Action<JsonObject>>
        {
            [1] = root =>
            {
                var legacy = (string)root["legacyTheme"]!;
                root.Remove("legacyTheme");
                root["appearance"] = new JsonObject { ["theme"] = legacy };
            }
        });
        const string v1 = """{ "schemaVersion": 1, "legacyTheme": "Light", "launchAtStartup": true }""";
        WriteRaw(MainPath, v1);
        var repository = Create(migration);

        var first = await repository.LoadAsync(Token);
        var second = await repository.LoadAsync(Token);

        Assert.Equal(SettingsRecovery.None, first.Value.Recovery);
        Assert.Equal(ThemePreference.Light, first.Value.Settings.Appearance.Theme);
        Assert.True(first.Value.Settings.LaunchAtStartup);
        Assert.Equal(first.Value.Settings, second.Value.Settings);
        Assert.Equal(2, (int)JsonNode.Parse(await File.ReadAllTextAsync(MainPath, Token))!["schemaVersion"]!);
        Assert.Equal(v1, await File.ReadAllTextAsync(BackupPath, Token));
    }

    [Fact]
    public async Task FailingMigrationStepIsRecoveredLikeACorruptFile()
    {
        var migration = new SettingsMigration(2, new Dictionary<int, Action<JsonObject>>
        {
            [1] = _ => throw new InvalidOperationException()
        });
        WriteRaw(MainPath, ValidJson());

        var result = await Create(migration).LoadAsync(Token);

        Assert.True(result.IsSuccess);
        Assert.Equal(SettingsRecovery.ResetToDefaults, result.Value.Recovery);
    }

    [Fact]
    public async Task LogEventsCarryNoValuesOrPaths()
    {
        WriteRaw(MainPath, """
            { "schemaVersion": 1, "media": { "preferredSessionId": 5 }, "conversion": { "outputDirectory": "C:\\secret-folder" } }
            """);
        var repository = Create();
        await repository.LoadAsync(Token);
        WriteRaw(MainPath, "{ broken secret-folder");
        await repository.LoadAsync(Token);

        Assert.NotEmpty(_log.Events);
        foreach (var e in _log.Events)
        {
            var text = e.ToString();
            Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(_directory, text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Settings", e.Component);
        }
    }

    [Fact]
    public async Task WorksWithoutALog()
    {
        using var repository = new JsonSettingsRepository(_directory);
        WriteRaw(MainPath, "{ broken");

        var result = await repository.LoadAsync(Token);

        Assert.Equal(SettingsRecovery.ResetToDefaults, result.Value.Recovery);
    }

    [Fact]
    public void DefaultDirectoryIsLunarUnderLocalAppData()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lunar");

        Assert.Equal(expected, JsonSettingsRepository.DefaultDirectory);
    }

    private sealed class RecordingLog : IDiagnosticLog
    {
        private readonly List<DiagnosticEvent> _events = [];

        public IReadOnlyList<DiagnosticEvent> Events
        {
            get
            {
                lock (_events)
                    return [.. _events];
            }
        }

        public void Write(DiagnosticEvent diagnosticEvent)
        {
            lock (_events)
                _events.Add(diagnosticEvent);
        }
    }
}
