namespace Lunar.Application.Abstractions.Diagnostics;

public interface IDiagnosticLog
{
    /// <summary>
    /// Records an event if its level is enabled. Never throws and never blocks on I/O faults: a log that
    /// cannot be written is dropped, because logging must not crash the app.
    /// </summary>
    void Write(DiagnosticEvent diagnosticEvent);
}
