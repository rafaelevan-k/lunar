using Lunar.Domain.Shared;
using Windows.Media.Audio;

namespace Lunar.Windows.Audio;

/// <summary>One way of opening the microphone, named for the log (for example <c>speech.device</c>).</summary>
internal sealed record InputAttempt<T>(string Name, Func<Task<(T? Value, AudioDeviceNodeCreationStatus Status)>> Open)
    where T : class;

/// <summary>
/// Tries the ways of opening a microphone in order and keeps the first that Windows accepts. When none does, the failure says the most telling
/// thing Windows reported: a refusal by the privacy settings first, then a format problem, then "not available".
/// </summary>
internal static class InputAttempts
{
    public static async Task<Result<T>> FirstAsync<T>(IReadOnlyList<InputAttempt<T>> attempts, Action<string, AudioDeviceNodeCreationStatus> refused)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(attempts);
        ArgumentNullException.ThrowIfNull(refused);
        var statuses = new List<AudioDeviceNodeCreationStatus>();
        foreach (var attempt in attempts)
        {
            var (value, status) = await attempt.Open().ConfigureAwait(false);
            if (status == AudioDeviceNodeCreationStatus.Success && value is not null)
                return Result.Success(value);
            refused(attempt.Name, status);
            statuses.Add(status);
        }

        if (statuses.Count == 0)
            return Result.Failure<T>(new OperationError(ErrorCode.DeviceUnavailable, "No microphone was found."));
        var telling = statuses.Contains(AudioDeviceNodeCreationStatus.AccessDenied) ? AudioDeviceNodeCreationStatus.AccessDenied
            : statuses.Contains(AudioDeviceNodeCreationStatus.FormatNotSupported) ? AudioDeviceNodeCreationStatus.FormatNotSupported
            : statuses.Contains(AudioDeviceNodeCreationStatus.DeviceNotAvailable) ? AudioDeviceNodeCreationStatus.DeviceNotAvailable
            : statuses[^1];
        return Result.Failure<T>(VoiceCaptureErrors.FromInput(telling));
    }
}
