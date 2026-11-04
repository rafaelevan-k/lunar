using Lunar.Application.Conversion.Models;
using Lunar.Domain.Shared;

namespace Lunar.Application.Abstractions.Conversion;

/// <summary>
/// Turns a request into a plan without converting anything (BE-20). It reads each input file's start (and, for a Word file,
/// the shape of its package) read-only to confirm the content matches the extension and stays within the limits, checks that
/// the route exists and that what it needs is installed, checks paths, and decides every output name and collision. It never
/// writes, creates or deletes a file.
///
/// A problem with the request as a whole (nothing chosen, too many files, an unusable output folder) is a failed
/// <see cref="Result{T}"/> with <see cref="ErrorCode.InvalidInput"/>. A problem with one file is not a failure of the call:
/// that file is listed in <see cref="ConversionPlan.Rejected"/> with a reason and a message saying what to do, and the other files
/// are planned. Cancellation returns <see cref="Result.Cancelled{T}"/>.
/// </summary>
public interface IConversionPlanner
{
    Task<Result<ConversionPlan>> PlanAsync(ConversionRequest request, CancellationToken ct);
}
