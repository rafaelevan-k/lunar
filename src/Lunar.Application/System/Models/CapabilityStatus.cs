namespace Lunar.Application.System.Models;

/// <summary>Whether the OS offers a capability to this app, independent of its current value.</summary>
public enum CapabilityStatus
{
    Supported,
    Unsupported,
    PermissionDenied,
    Unavailable,
    Unknown
}
