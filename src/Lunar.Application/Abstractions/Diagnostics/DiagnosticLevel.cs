namespace Lunar.Application.Abstractions.Diagnostics;

public enum DiagnosticLevel
{
    /// <summary>Written only when the user has opted in to diagnostics.</summary>
    Debug,
    Information,
    Warning,
    Error
}
