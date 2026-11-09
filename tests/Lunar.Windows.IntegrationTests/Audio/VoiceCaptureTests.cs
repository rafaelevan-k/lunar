using Lunar.Application.Abstractions.Audio;
using Lunar.Domain.Shared;
using Lunar.Windows.Audio;
using Windows.Media.Audio;
using Windows.Storage;
using Xunit;

namespace Lunar.Windows.IntegrationTests.Audio;

/// <summary>
/// The microphone adapter (ADR-0022, BE-39). Nothing here opens a microphone unless LUNAR_TEST_RECORD=1 is set for the run: a test suite must
/// not listen to the room on its own.
/// </summary>
public sealed class VoiceCaptureTests : IDisposable
{
    private const string OptIn = "LUNAR_TEST_RECORD";
    private readonly string _folder = Directory.CreateTempSubdirectory("lunar-voice-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Part(string name = "~test.lunar-part") => Path.Combine(_folder, name);

    [Fact]
    public async Task TheRealListOfInputsReadsAndPutsTheWindowsDefaultFirst()
    {
        using var catalog = new WindowsAudioInputCatalog();

        var inputs = await catalog.GetInputsAsync(TestContext.Current.CancellationToken);

        Assert.True(inputs.IsSuccess);
        Assert.All(inputs.Value, i => Assert.False(string.IsNullOrEmpty(i.Id)));
        Assert.True(inputs.Value.Count(i => i.IsWindowsDefault) <= 1);
        if (inputs.Value.Any(i => i.IsWindowsDefault))
            Assert.True(inputs.Value[0].IsWindowsDefault);
    }

    [Fact]
    public async Task ListeningForChangesCanBeStartedAndStoppedAndACancelledReadIsTyped()
    {
        using var catalog = new WindowsAudioInputCatalog();
        void Handler()
        {
        }

        catalog.Changed += Handler;
        catalog.Changed -= Handler;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var result = await catalog.GetInputsAsync(cancelled.Token);

        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
    }

    [Fact]
    public async Task AnInputThatDoesNotExistIsATypedResultAndLeavesNoFile()
    {
        var path = Part();

        var result = await new WinRtVoiceCaptureEngine().StartAsync("not-a-device", path, TestContext.Current.CancellationToken);

        Assert.Equal(ErrorCode.DeviceUnavailable, result.Error!.Code);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative.lunar-part")]
    public async Task APathThatIsNotFullIsRefused(string path)
    {
        var result = await new WinRtVoiceCaptureEngine().StartAsync(null, path, TestContext.Current.CancellationToken);

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
    }

    [Fact]
    public void AudioGraphStatusesBecomeTheTypedFailuresTheUserIsToldAbout()
    {
        Assert.Equal(ErrorCode.AccessDenied, VoiceCaptureErrors.FromInput(AudioDeviceNodeCreationStatus.AccessDenied).Code);
        Assert.Equal(ErrorCode.DeviceUnavailable, VoiceCaptureErrors.FromInput(AudioDeviceNodeCreationStatus.DeviceNotAvailable).Code);
        Assert.Equal(ErrorCode.DeviceUnavailable, VoiceCaptureErrors.FromGraph(AudioGraphCreationStatus.DeviceNotAvailable).Code);
        Assert.Equal(ErrorCode.DependencyMissing, VoiceCaptureErrors.FromFile(AudioFileNodeCreationStatus.FormatNotSupported).Code);
        Assert.Equal(ErrorCode.DependencyMissing, VoiceCaptureErrors.FromFile(AudioFileNodeCreationStatus.InvalidFileType).Code);
        Assert.Equal(ErrorCode.IoFailure, VoiceCaptureErrors.FromFile(AudioFileNodeCreationStatus.FileNotFound).Code);
        Assert.Equal(ErrorCode.DeviceUnavailable, VoiceCaptureErrors.FromLostGraph(AudioGraphUnrecoverableError.AudioDeviceLost).Code);
        Assert.Equal(ErrorCode.Unexpected, VoiceCaptureErrors.FromLostGraph(AudioGraphUnrecoverableError.UnknownFailure).Code);
    }

    [Fact]
    public void SilenceReadsFlat()
    {
        var analyzer = new SpectrumAnalyzer(48000);
        analyzer.Add(new float[SpectrumAnalyzer.FftSize * 2], 2);

        Assert.All(analyzer.Compute(), band => Assert.Equal(0f, band));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(1000)]
    [InlineData(5000)]
    public void AToneLightsItsOwnBandAndHigherFrequenciesLightHigherBands(int hertz)
    {
        const int Rate = 48000;
        var analyzer = new SpectrumAnalyzer(Rate);
        var samples = new float[SpectrumAnalyzer.FftSize];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (float)Math.Sin(2 * Math.PI * hertz * i / Rate);
        analyzer.Add(samples, 1);

        var bands = analyzer.Compute();

        var expected = analyzer.BandOfBin((int)Math.Round((double)hertz * SpectrumAnalyzer.FftSize / Rate));
        Assert.Equal(VoiceLevelBands.Count, bands.Length);
        Assert.InRange(bands[expected], 0.9f, 1f);
        Assert.Equal(expected, Array.IndexOf(bands, bands.Max()));
    }

    [Fact]
    public async Task ARealRecordingIsAValidMp3OfTheRightLengthAcrossAPause()
    {
        if (System.Environment.GetEnvironmentVariable(OptIn) != "1")
            Assert.Skip($"Opens a real microphone; set {OptIn}=1 to run it.");
        var ct = TestContext.Current.CancellationToken;
        var path = Part();
        var engine = new WinRtVoiceCaptureEngine();

        var started = await engine.StartAsync(null, path, ct);
        Assert.True(started.IsSuccess, started.Error?.SafeMessage);
        await using var session = started.Value;
        var readings = new List<VoiceLevels>();
        session.LevelsChanged += levels =>
        {
            lock (readings)
                readings.Add(levels);
        };

        await Task.Delay(2000, ct);
        Assert.True((await session.PauseAsync(ct)).IsSuccess);
        Assert.True((await session.PauseAsync(ct)).IsSuccess); // harmless
        var atPause = session.Recorded;
        await Task.Delay(1500, ct);
        Assert.Equal(atPause, session.Recorded); // a pause adds nothing
        Assert.True((await session.ResumeAsync(ct)).IsSuccess);
        await Task.Delay(2000, ct);
        Assert.True((await session.FinishAsync(ct)).IsSuccess);
        var recorded = session.Recorded;

        Assert.InRange(recorded.TotalSeconds, 3.5, 4.5);
        var (frames, seconds, kilobits) = Mp3.Read(path);
        Assert.True(frames > 100);
        Assert.Equal(192, kilobits);
        Assert.InRange(seconds, recorded.TotalSeconds - 0.4, recorded.TotalSeconds + 0.4);
        lock (readings)
        {
            Assert.InRange(readings.Count, 40, 120); // about 20 a second for 4 seconds, plus flat one on pause
            Assert.All(readings, r => Assert.Equal(VoiceLevelBands.Count, r.Bands.Count));
        }

        var decoded = await DecodedSecondsAsync(path);
        Assert.InRange(decoded, recorded.TotalSeconds - 0.4, recorded.TotalSeconds + 0.4);
        Assert.Equal(ErrorCode.InvalidInput, (await session.PauseAsync(ct)).Error!.Code); // the recording has ended
    }

    [Fact]
    public async Task AFileThatAlreadyExistsIsNeverTakenOver()
    {
        if (System.Environment.GetEnvironmentVariable(OptIn) != "1")
            Assert.Skip($"Needs a real microphone to get as far as the file; set {OptIn}=1 to run it.");
        var path = Part();
        await File.WriteAllTextAsync(path, "keep me", TestContext.Current.CancellationToken);

        var result = await new WinRtVoiceCaptureEngine().StartAsync(null, path, TestContext.Current.CancellationToken);

        Assert.Equal(ErrorCode.IoFailure, result.Error!.Code);
        Assert.Equal("keep me", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    private static async Task<double> DecodedSecondsAsync(string path)
    {
        var created = await AudioGraph.CreateAsync(new AudioGraphSettings(global::Windows.Media.Render.AudioRenderCategory.Media));
        using var graph = created.Graph;
        var file = await StorageFile.GetFileFromPathAsync(path);
        var node = await graph.CreateFileInputNodeAsync(file);
        Assert.Equal(AudioFileNodeCreationStatus.Success, node.Status);
        return node.FileInputNode.Duration.TotalSeconds;
    }

    /// <summary>Walks the MPEG-1 Layer III frames by hand, independent of the encoder and of Windows' decoder.</summary>
    private static class Mp3
    {
        private static readonly int[] Rates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];
        private static readonly int[] Hertz = [44100, 48000, 32000, 0];

        public static (int Frames, double Seconds, int Kilobits) Read(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var i = bytes is [(byte)'I', (byte)'D', (byte)'3', ..]
                ? 10 + ((bytes[6] & 0x7F) << 21 | (bytes[7] & 0x7F) << 14 | (bytes[8] & 0x7F) << 7 | (bytes[9] & 0x7F))
                : 0;
            int frames = 0, hertz = 0, kilobits = 0;
            while (i + 4 <= bytes.Length)
            {
                Assert.True(bytes[i] == 0xFF && (bytes[i + 1] & 0xFE) == 0xFA, $"No MP3 frame at offset {i}.");
                kilobits = Rates[bytes[i + 2] >> 4];
                hertz = Hertz[(bytes[i + 2] >> 2) & 3];
                Assert.True(kilobits > 0 && hertz > 0);
                i += (144 * kilobits * 1000 / hertz) + ((bytes[i + 2] >> 1) & 1);
                frames++;
            }

            return (frames, frames * 1152.0 / hertz, kilobits);
        }
    }
}
