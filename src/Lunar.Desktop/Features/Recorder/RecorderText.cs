using System.Globalization;
using Lunar.Application.Abstractions.Audio;
using Lunar.Application.Recording;
using Lunar.Desktop.Resources;
using Lunar.Domain.Shared;

namespace Lunar.Desktop.Features.Recorder;

/// <summary>The recorder's words (ADR-0022). Pure, so every state can be checked.</summary>
internal static class RecorderText
{
    /// <summary><c>mm:ss</c>, and <c>h:mm:ss</c> from one hour. Never rounds up.</summary>
    internal static string Elapsed(TimeSpan time)
    {
        var whole = time < TimeSpan.Zero ? TimeSpan.Zero : time;
        return whole.TotalHours >= 1
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", (int)whole.TotalHours, whole.Minutes, whole.Seconds)
            : string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", (int)whole.TotalMinutes, whole.Seconds);
    }

    internal static string Failure(OperationError error) => error.Code switch
    {
        ErrorCode.AccessDenied => Strings.Get("Records.ErrorBlocked"),
        ErrorCode.DeviceUnavailable => Strings.Get("Records.ErrorNoMicrophone"),
        ErrorCode.DependencyMissing => Strings.Get("Records.ErrorNoEncoder"),
        ErrorCode.IoFailure => Strings.Get("Records.ErrorFolder"),
        ErrorCode.InvalidInput => Strings.Get("Records.ErrorNotSaved"),
        _ => Strings.Get("Records.ErrorOther")
    };

    /// <summary>What to say about a notice, or null for one with nothing to say.</summary>
    internal static string? Notice(RecordingSnapshot snapshot) => snapshot.Notice switch
    {
        RecordingNotice.ChosenInputMissing => Strings.Get("Records.NoticeChosenMissing"),
        RecordingNotice.DeviceLost => Strings.Get("Records.NoticeDeviceLost"),
        RecordingNotice.DiskNearlyFull => Strings.Get("Records.NoticeDiskFull"),
        RecordingNotice.LengthLimitReached => Strings.Get("Records.NoticeLengthLimit"),
        RecordingNotice.RecordingRecovered when snapshot.Recovered?.Outcome == RecoveryOutcome.KeptAsItIs => Strings.Get("Records.NoticeKeptAsItIs"),
        RecordingNotice.RecordingRecovered => Strings.Get("Records.NoticeRecovered"),
        _ => null
    };
}
