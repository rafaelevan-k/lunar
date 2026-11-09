using Lunar.Domain.Shared;
using Windows.Media.Audio;

namespace Lunar.Windows.Audio;

/// <summary>What Windows' audio graph reports, in Lunar's terms. The messages carry no device name and no path.</summary>
internal static class VoiceCaptureErrors
{
    internal static OperationError FromGraph(AudioGraphCreationStatus status) => status switch
    {
        AudioGraphCreationStatus.DeviceNotAvailable => new OperationError(ErrorCode.DeviceUnavailable, "Windows has no audio device available for recording."),
        AudioGraphCreationStatus.FormatNotSupported => new OperationError(ErrorCode.Unsupported, "This audio device's format is not supported."),
        _ => new OperationError(ErrorCode.Unexpected, "Recording could not be started.", retryable: true)
    };

    internal static OperationError FromInput(AudioDeviceNodeCreationStatus status) => status switch
    {
        AudioDeviceNodeCreationStatus.AccessDenied => new OperationError(ErrorCode.AccessDenied, "Windows is not letting apps use the microphone."),
        AudioDeviceNodeCreationStatus.DeviceNotAvailable => new OperationError(ErrorCode.DeviceUnavailable, "The microphone is not available."),
        AudioDeviceNodeCreationStatus.FormatNotSupported => new OperationError(ErrorCode.Unsupported, "This microphone's format is not supported."),
        _ => new OperationError(ErrorCode.Unexpected, "The microphone could not be opened.", retryable: true)
    };

    internal static OperationError FromFile(AudioFileNodeCreationStatus status) => status switch
    {
        AudioFileNodeCreationStatus.FormatNotSupported or AudioFileNodeCreationStatus.InvalidFileType =>
            new OperationError(ErrorCode.DependencyMissing, "This PC has no MP3 encoder. On Windows N editions, install the Media Feature Pack."),
        AudioFileNodeCreationStatus.FileNotFound => new OperationError(ErrorCode.IoFailure, "The recording file could not be created."),
        _ => new OperationError(ErrorCode.Unexpected, "The recording file could not be started.", retryable: true)
    };

    internal static OperationError FromLostGraph(AudioGraphUnrecoverableError error) => error switch
    {
        AudioGraphUnrecoverableError.AudioDeviceLost or AudioGraphUnrecoverableError.AudioSessionDisconnected =>
            new OperationError(ErrorCode.DeviceUnavailable, "Windows lost the audio device during the recording."),
        _ => new OperationError(ErrorCode.Unexpected, "Windows stopped the recording.")
    };
}
