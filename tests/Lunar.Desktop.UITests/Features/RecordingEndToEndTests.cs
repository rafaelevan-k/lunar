using Lunar.Application.Abstractions.Audio;
using Lunar.Application.Abstractions.Settings;
using Lunar.Application.Abstractions.System;
using Lunar.Application.Recording;
using Lunar.Application.Settings;
using Lunar.Application.Settings.Models;
using Lunar.Domain.Recording;
using Lunar.Domain.Shared;
using Lunar.Infrastructure.Recording;
using Lunar.Windows.Audio;
using Xunit;

namespace Lunar.Desktop.UITests.Features;

/// <summary>
/// The real recorder, engine and files together (BE-40): this project is the one that references them all, as the composition root does. These
/// open a real microphone, so they run only with LUNAR_TEST_RECORD=1.
/// </summary>
public sealed class RecordingEndToEndTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("lunar-e2e-").FullName;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Takes => Path.Combine(_root, "Takes");

    private string AppData => Path.Combine(_root, "AppData");

    private static void RequireOptIn()
    {
        if (System.Environment.GetEnvironmentVariable("LUNAR_TEST_RECORD") != "1")
            Assert.Skip("Opens a real microphone; set LUNAR_TEST_RECORD=1 to run it.");
    }

    [Fact]
    public async Task ARealRecordingIsSavedAsAnMp3UnderItsNameWithNothingLeftBehind()
    {
        RequireOptIn();
        using var settings = new SettingsService(new Repository());
        using var catalog = new WindowsAudioInputCatalog();
        using var service = new RecordingService(new WinRtVoiceCaptureEngine(), catalog, new RecordingFileStore(AppData, Takes), settings, new Clock());

        Assert.True((await service.StartAsync(Ct)).IsSuccess, service.Current.Error?.SafeMessage);
        await Task.Delay(2000, Ct);
        Assert.Equal(RecordingPhase.Recording, service.Current.Phase);
        Assert.InRange(service.Recorded.TotalSeconds, 1, 3);
        Assert.True((await service.StopAsync()).IsSuccess, service.Current.Error?.SafeMessage);

        Assert.Equal(RecordingPhase.Idle, service.Current.Phase);
        Assert.Equal(["Recording 2026-11-09 14-30-05.mp3"], Directory.GetFiles(Takes).Select(Path.GetFileName));
        Assert.True(new FileInfo(service.Current.LastSaved!.Path).Length > 10_000);
        Assert.False(File.Exists(Path.Combine(AppData, "recording.journal")));
    }

    [Fact]
    public async Task ARealRecordingThatWasNeverFinishedIsRecoveredAtTheNextStart()
    {
        RequireOptIn();
        var files = new RecordingFileStore(AppData, Takes);
        var part = await files.ReservePartAsync(Takes, "Recording X", Ct);
        var started = await new WinRtVoiceCaptureEngine().StartAsync(null, part.Value, Ct);
        Assert.True(started.IsSuccess, started.Error?.SafeMessage);
        await Task.Delay(2000, Ct);
        await started.Value.DisposeAsync(); // no finish, as if Lunar had stopped here

        var found = await new RecordingFileStore(AppData, Takes).RecoverAsync(Ct);

        Assert.Equal(RecoveryOutcome.Recovered, found.Value!.Outcome);
        Assert.Equal("Recording X (recovered).mp3", Path.GetFileName(found.Value.Path));
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset LocalNow { get; } = new(2026, 11, 9, 14, 30, 5, TimeSpan.Zero);
    }

    private sealed class Repository : ISettingsRepository
    {
        public Task<Result<SettingsLoadResult>> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success(new SettingsLoadResult(LunarSettings.Defaults, SettingsRecovery.None)));

        public Task<Result> SaveAsync(LunarSettings settings, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
    }
}
