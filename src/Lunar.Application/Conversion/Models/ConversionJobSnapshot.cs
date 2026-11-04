using Lunar.Domain.Shared;

namespace Lunar.Application.Conversion.Models;

/// <summary>Where a queued conversion is in its life. Only <see cref="Succeeded"/>, <see cref="Failed"/> and <see cref="Cancelled"/> are final.</summary>
public enum ConversionJobState
{
    /// <summary>Waiting for a free place to run.</summary>
    Queued,

    /// <summary>Being converted now.</summary>
    Running,

    /// <summary>Cancel was asked and the conversion is stopping and cleaning up; it is not final until it has stopped.</summary>
    Cancelling,

    /// <summary>Finished and its results are in place.</summary>
    Succeeded,

    /// <summary>Ended with an error (or took too long); nothing of it is left behind.</summary>
    Failed,

    /// <summary>Stopped on request; nothing of it is left behind.</summary>
    Cancelled
}

/// <summary>
/// How far a conversion has come, in pages, only when the converter really knows. With no page count the progress is unknown
/// (indeterminate): a number is never made up. <see cref="Fraction"/> is null in that case.
/// </summary>
/// <param name="PagesCompleted">Pages finished so far, or null when not known.</param>
/// <param name="PagesTotal">Pages in all, or null when not known.</param>
public sealed record JobProgress(int? PagesCompleted, int? PagesTotal)
{
    /// <summary>Nothing is known: show activity without a percentage.</summary>
    public static JobProgress Unknown { get; } = new(null, null);

    /// <summary>True when a fraction can be shown honestly.</summary>
    public bool IsDeterminate => PagesCompleted is >= 0 && PagesTotal is > 0;

    /// <summary>Completed over total, kept between 0 and 1; null when <see cref="IsDeterminate"/> is false.</summary>
    public double? Fraction => IsDeterminate ? Math.Clamp((double)PagesCompleted!.Value / PagesTotal!.Value, 0.0, 1.0) : null;
}

/// <summary>
/// An immutable picture of one queued conversion at one moment. <see cref="Error"/> is set only for <see cref="ConversionJobState.Failed"/>;
/// <see cref="OutputPaths"/> only for <see cref="ConversionJobState.Succeeded"/>. A job that did not succeed has no outputs: its
/// working files are gone by the time it is final.
/// </summary>
public sealed record ConversionJobSnapshot(
    int JobId,
    PlannedConversion Job,
    ConversionJobState State,
    JobProgress Progress,
    OperationError? Error,
    IReadOnlyList<string> OutputPaths)
{
    /// <summary>True once the job can no longer change (except by being retried as a new job).</summary>
    public bool IsFinal => State is ConversionJobState.Succeeded or ConversionJobState.Failed or ConversionJobState.Cancelled;
}
