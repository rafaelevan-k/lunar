namespace Lunar.Domain.Shared;

/// <summary>
/// Immutable description of an expected failure. <see cref="SafeMessage"/> is user-displayable and must not
/// contain file paths, document content, SSIDs, media titles, credentials or raw exception text.
/// </summary>
public sealed record OperationError
{
    public const string CancelledMessage = "The operation was cancelled.";

    public OperationError(ErrorCode code, string safeMessage, bool retryable = false)
    {
        if (!Enum.IsDefined(code))
            throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown error code.");
        ArgumentException.ThrowIfNullOrWhiteSpace(safeMessage);

        Code = code;
        SafeMessage = safeMessage;
        Retryable = retryable;
    }

    public ErrorCode Code { get; }

    public string SafeMessage { get; }

    public bool Retryable { get; }

    public bool IsCancellation => Code == ErrorCode.Cancelled;

    /// <summary>Cancellation is an expected outcome, not a fault; it is never retryable by itself.</summary>
    public static OperationError Cancelled() => new(ErrorCode.Cancelled, CancelledMessage);
}
