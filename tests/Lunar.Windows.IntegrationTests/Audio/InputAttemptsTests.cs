using Lunar.Domain.Shared;
using Lunar.Windows.Audio;
using Windows.Media.Audio;
using Xunit;

namespace Lunar.Windows.IntegrationTests.Audio;

// The order in which the microphone is opened. A friend's laptop with an Intel Smart Sound Technology microphone array could not record
// (2026-11-12): the speech mode was refused and Lunar said "No microphone was found". The real refusal cannot be made on this PC; the rule is
// tested here, and a real recording still runs through the first attempt in VoiceCaptureTests.
public sealed class InputAttemptsTests
{
    private sealed class Node;

    private static InputAttempt<Node> Refused(string name, AudioDeviceNodeCreationStatus status, List<string> tried) =>
        new(name, () =>
        {
            tried.Add(name);
            return Task.FromResult<(Node?, AudioDeviceNodeCreationStatus)>((null, status));
        });

    private static InputAttempt<Node> Opens(string name, Node node, List<string> tried) =>
        new(name, () =>
        {
            tried.Add(name);
            return Task.FromResult<(Node?, AudioDeviceNodeCreationStatus)>((node, AudioDeviceNodeCreationStatus.Success));
        });

    [Fact]
    public async Task WhenTheSpeechModeIsRefusedThePlainModeIsUsed()
    {
        var tried = new List<string>();
        var logged = new List<string>();
        var plain = new Node();

        var result = await InputAttempts.FirstAsync(
            [Refused("speech.device", AudioDeviceNodeCreationStatus.DeviceNotAvailable, tried), Opens("other.device", plain, tried), Opens("speech.default", new Node(), tried)],
            (name, status) => logged.Add(name + "." + status.ToString().ToLowerInvariant()));

        Assert.True(result.IsSuccess);
        Assert.Same(plain, result.Value);
        Assert.Equal(["speech.device", "other.device"], tried); // stops at the first that works
        Assert.Equal(["speech.device.devicenotavailable"], logged);
    }

    [Fact]
    public async Task TheFirstThatWorksIsKeptAndNothingIsLogged()
    {
        var tried = new List<string>();
        var logged = new List<string>();

        var result = await InputAttempts.FirstAsync([Opens("speech.device", new Node(), tried), Opens("other.device", new Node(), tried)], (n, _) => logged.Add(n));

        Assert.True(result.IsSuccess);
        Assert.Equal(["speech.device"], tried);
        Assert.Empty(logged);
    }

    [Fact]
    public async Task WhenEveryWayIsRefusedThePrivacyRefusalIsWhatTheUserHears()
    {
        var tried = new List<string>();
        var logged = new List<string>();

        var result = await InputAttempts.FirstAsync(
            [
                Refused("speech.device", AudioDeviceNodeCreationStatus.DeviceNotAvailable, tried),
                Refused("other.device", AudioDeviceNodeCreationStatus.AccessDenied, tried),
                Refused("speech.default", AudioDeviceNodeCreationStatus.UnknownFailure, tried)
            ],
            (n, _) => logged.Add(n));

        Assert.Equal(ErrorCode.AccessDenied, result.Error!.Code);
        Assert.Equal(3, tried.Count); // every way was tried
        Assert.Equal(tried, logged); // and every refusal is in the log
    }

    [Theory]
    [InlineData(AudioDeviceNodeCreationStatus.FormatNotSupported, AudioDeviceNodeCreationStatus.DeviceNotAvailable, ErrorCode.Unsupported)]
    [InlineData(AudioDeviceNodeCreationStatus.UnknownFailure, AudioDeviceNodeCreationStatus.DeviceNotAvailable, ErrorCode.DeviceUnavailable)]
    [InlineData(AudioDeviceNodeCreationStatus.UnknownFailure, AudioDeviceNodeCreationStatus.UnknownFailure, ErrorCode.Unexpected)]
    public async Task TheMostTellingRefusalIsReported(AudioDeviceNodeCreationStatus first, AudioDeviceNodeCreationStatus second, ErrorCode expected)
    {
        var tried = new List<string>();
        var result = await InputAttempts.FirstAsync([Refused("a", first, tried), Refused("b", second, tried)], (_, _) => { });
        Assert.Equal(expected, result.Error!.Code);
    }

    [Fact]
    public async Task NoWayToTryMeansNoMicrophone()
    {
        var result = await InputAttempts.FirstAsync<Node>([], (_, _) => { });
        Assert.Equal(ErrorCode.DeviceUnavailable, result.Error!.Code);
    }
}
