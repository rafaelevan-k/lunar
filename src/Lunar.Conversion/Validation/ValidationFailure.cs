using Lunar.Application.Conversion.Models;
using Lunar.Domain.Shared;

namespace Lunar.Conversion.Validation;

/// <summary>
/// Why one input was refused: a stable reason, the error category and a message that tells the user what to do. The message
/// never contains a path, a file name or anything read from the file.
/// </summary>
internal sealed record ValidationFailure(RejectionReason Reason, ErrorCode Code, string Message)
{
    public OperationError ToError() => new(Code, Message);

    public static ValidationFailure Invalid(RejectionReason reason, string message) => new(reason, ErrorCode.InvalidInput, message);
}
