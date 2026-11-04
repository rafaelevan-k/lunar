using Lunar.Domain.Shared;

namespace Lunar.Application.Abstractions.Conversion;

/// <summary>
/// Shows a finished result to the user in the file manager (FE-22, PRD FR-CON-03 "open output folder"): a file is shown selected in its
/// folder, a folder is opened. It only shows; it never opens the file itself, runs it, or changes it. The caller passes a full path
/// that a conversion just produced; a path that is not a full local path or no longer exists is refused with
/// <see cref="ErrorCode.InvalidInput"/>, and a failure to start the file manager is a failed <see cref="Result"/>, never an exception.
/// </summary>
public interface IOutputRevealer
{
    Task<Result> RevealAsync(string path, CancellationToken ct);
}
