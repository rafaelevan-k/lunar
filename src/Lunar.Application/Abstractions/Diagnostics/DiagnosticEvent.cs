using Lunar.Domain.Shared;

namespace Lunar.Application.Abstractions.Diagnostics;

/// <summary>
/// One structured log event. There is deliberately no free-text message and no parameter bag, so a caller cannot
/// put a path, SSID, title or document text into a log (ADR-0009). <paramref name="EventId"/>,
/// <paramref name="Component"/> and <paramref name="Operation"/> are short fixed vocabulary tokens
/// (letters, digits, '.', '_', '-', at most 64 characters); anything else is replaced by "redacted" by the adapter.
/// Only the type name of an exception is recorded, never its message or stack.
/// </summary>
public sealed record DiagnosticEvent(
    DiagnosticLevel Level,
    string EventId,
    string Component,
    string Operation,
    ErrorCode? Error = null,
    TimeSpan? Duration = null,
    Type? ExceptionType = null);
