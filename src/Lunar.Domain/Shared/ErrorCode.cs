namespace Lunar.Domain.Shared;

/// <summary>Stable category of an expected, recoverable failure.</summary>
public enum ErrorCode
{
    Unsupported,
    AccessDenied,
    DeviceUnavailable,
    InvalidInput,
    Cancelled,
    Timeout,
    DependencyMissing,
    IoFailure,
    Unexpected
}
