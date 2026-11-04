using Lunar.Application.Conversion.Models;
using Lunar.Domain.Shared;

namespace Lunar.Conversion.Pipeline;

/// <summary>
/// Does the work of one planned conversion (the orchestrator of BE-27 implements it from the providers and the output
/// transaction). The queue calls it on a background thread, at most a few at a time, and guarantees only what it can see; what the
/// executor owes the queue is part of this contract:
/// - Stop promptly when <paramref name="ct"/> is cancelled, and before returning remove everything the job wrote, so a job that
///   ends cancelled or failed leaves no partial output. The queue treats the job as final only after this method has returned.
/// - Return the full paths of the files produced on success. Return a failed <see cref="Result{T}"/> with a message that has no
///   path or content for an expected failure; <see cref="ErrorCode.Cancelled"/> (or an <see cref="OperationCanceledException"/>) when stopped.
/// - Report progress in pages only when the page count is known; never report a made-up number.
/// - Never modify the input file.
/// </summary>
public interface IConversionJobExecutor
{
    Task<Result<IReadOnlyList<string>>> ExecuteAsync(PlannedConversion job, IProgress<JobProgress> progress, CancellationToken ct);
}
