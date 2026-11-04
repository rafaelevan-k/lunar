using Lunar.Application.Conversion.Models;
using Lunar.Domain.Shared;

namespace Lunar.Application.Abstractions.Conversion;

/// <summary>
/// The queue that runs planned conversions in the background (BE-21, PRD FR-CON-03/04). Submitting never blocks and never does the
/// conversion on the caller's thread, so a UI thread may call every member. At most a few conversions run at once; the rest wait
/// in order. Each job can be cancelled alone, all can be cancelled together, and a failed or cancelled job can be retried as a new job.
///
/// A job that is cancelled, fails or times out leaves nothing of itself behind: it is final only after the converter has stopped
/// and cleaned up, so a final job that did not succeed has no output files and no working files. A job that cannot be cancelled
/// in time keeps its place and shows <see cref="ConversionJobState.Cancelling"/> until it has really stopped.
///
/// Observable semantics are those of ADR-0006: <see cref="Changes"/> has no replay (read <see cref="Jobs"/> first), delivers one
/// snapshot per change serialized on an arbitrary thread (marshal to the Dispatcher), never calls <c>OnError</c>, and completes
/// when the queue is disposed. Disposing cancels every job and waits for them to stop. Expected failures are returned as
/// <see cref="Result"/> failures. Thread-safe.
/// </summary>
public interface IConversionQueue : IAsyncDisposable
{
    IObservable<ConversionJobSnapshot> Changes { get; }

    /// <summary>Every job the queue still remembers (waiting, running and recently finished), oldest first.</summary>
    IReadOnlyList<ConversionJobSnapshot> Jobs { get; }

    /// <summary>
    /// Adds jobs from a plan, all or none. Fails with <see cref="ErrorCode.InvalidInput"/> when nothing is given or the queue would
    /// hold more waiting and running jobs than it allows (retryable: try again when some have finished). Returns the ids in order.
    /// </summary>
    Result<IReadOnlyList<int>> Enqueue(IReadOnlyList<PlannedConversion> jobs);

    /// <summary>
    /// Asks a job to stop. A waiting job is cancelled at once and never starts; a running job becomes Cancelling and is final when
    /// it has stopped. Cancelling a final or already cancelling job succeeds and changes nothing. An unknown id fails with
    /// <see cref="ErrorCode.InvalidInput"/>.
    /// </summary>
    Result Cancel(int jobId);

    /// <summary>Asks every waiting and running job to stop.</summary>
    void CancelAll();

    /// <summary>
    /// Queues the same planned conversion again as a new job. Only a failed or cancelled job can be retried
    /// (<see cref="ErrorCode.InvalidInput"/> otherwise). The conversion re-checks the input and the output name as it starts.
    /// </summary>
    Result<int> Retry(int jobId);

    /// <summary>Forgets the final jobs (for a "clear list" button) and returns how many were forgotten.</summary>
    int ClearFinished();

    /// <summary>Completes when no job is waiting, running or cancelling. A cancelled token ends the wait with an <see cref="OperationCanceledException"/>.</summary>
    Task WhenIdleAsync(CancellationToken ct);
}
