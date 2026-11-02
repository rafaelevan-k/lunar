# ADR-0009: Structured diagnostic logging with privacy by construction (BE-07)

- Status: **Accepted**
- Date: 2026-11-03
- Approver: User (explicit approval in chat: "Terima ADR-0009", 2026-11-03)
- Approval scope requested: one new Application port, a Serilog-based adapter with two new packages, the retention/opt-in policy, a temporary environment-variable opt-in, and replacing the current `Trace` calls.

## Context

BE-07 requires Serilog with bounded rolling files, redaction and opt-in diagnostics; no sensitive data in normal logs; bounded storage. ARCHITECTURE §10 lists the allowed fields (timestamp, event ID, component, operation, duration, result/error code, OS build if privacy policy allows, app version) and forbids SSIDs, media titles, user file names/paths, document content, passwords and sensitive raw exception messages; logs go to `%LocalAppData%/Lunar/Logs` with rotation. PRD §7/§8: no telemetry by default, diagnostics opt-in, no file upload. Today code writes only exception type names via `System.Diagnostics.Trace` (BE-05, FE-06). Application must stay package-free, so Serilog types cannot cross the port. Settings (BE-06) and a settings UI (FE-23) do not exist yet.

## Decision

**Port** (`Lunar.Application/Abstractions/Diagnostics`, no package types):
```csharp
public enum DiagnosticLevel { Debug, Information, Warning, Error }
public sealed record DiagnosticEvent(DiagnosticLevel Level, string EventId, string Component, string Operation,
    ErrorCode? Error = null, TimeSpan? Duration = null, Type? ExceptionType = null);
public interface IDiagnosticLog { void Write(DiagnosticEvent diagnosticEvent); }
```
There is deliberately **no free-text message and no object/parameter bag**: the only strings are `EventId`, `Component`, `Operation`, so a caller cannot put a path, SSID, title or document text into a log by mistake. `Write` never throws and never blocks on I/O faults. `ExceptionType` records the type name only, never the message or stack.

**Redaction as a second layer** (Infrastructure): each of the three strings must match `^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$`; anything else (spaces, `\`, `/`, `:`, `@`, over 64 characters, empty) is replaced by the literal `redacted`. Unit tests feed paths, e-mail addresses, SSID-like and document-like text and assert none of it reaches the file.

**Adapter** (`Lunar.Infrastructure/Logging/SerilogDiagnosticLog.cs`, `LoggingConfiguration.cs`): Serilog 4.3.1 and Serilog.Sinks.File 7.0.0 (both Apache-2.0; Serilog has no extra dependencies on net10.0). One instance logger, no static `Log`, no Serilog self-log to disk. Files: `%LocalAppData%\Lunar\Logs\lunar-YYYYMMDD.log`, daily rolling plus size rolling, shared file access so a short-lived second process cannot fail on a lock. Line format: timestamp, level, event id, component.operation, error code, duration ms, exception type, plus constant app version and OS build (a version number, not personal data). Dispose flushes.

**Bounded storage**: normal mode keeps at most 7 files of at most 1 MiB (≤ 7 MiB); diagnostic mode at most 7 files of 5 MiB (≤ 35 MiB). Older files are deleted by the sink's retention. Nothing else is written, no network sink, no telemetry.

**Opt-in**: normal mode writes `Information`, `Warning`, `Error` events with fixed vocabulary (lifecycle and failures). Diagnostic mode additionally writes `Debug` events and uses the larger bound. Diagnostic mode is off unless the user turns it on. Until BE-06/FE-23 provide a setting, the only switch is the environment variable `LUNAR_DIAGNOSTICS=1` read once at startup in the composition root; it is documented in `docs/PRIVACY.md` and is replaced by the setting later. No log is ever uploaded; an export feature is out of scope and needs its own review.

**Wiring**: `ServiceRegistration` registers `IDiagnosticLog` as a singleton disposed with the container. `App` and `SingleInstanceCoordinator` replace their `Trace` calls with `IDiagnosticLog` events (`app.startup.failed`, `instance.check.failed`, `instance.activation.failed`, …). `docs/PRIVACY.md` is created and states what is and is never logged, where, retention, and how to delete.

## Alternatives

- Microsoft.Extensions.Logging in Application: adds a package to the pure layer and permits free-text messages with arbitrary arguments.
- `ILogger`-style free-text port with a redaction filter only: redaction by pattern matching cannot reliably spot an SSID or a media title.
- `EventSource`/ETW only: not user-reviewable files, no bounded rotation under our control.
- Roll own file writer: more code and more defects than a maintained sink.
- Settings-based opt-in now: depends on BE-06 and FE-23 (not started).

## Consequences

- Logs are less expressive than usual (no messages); diagnosing a field issue relies on event IDs, error codes and durations. This is intentional; richer events can be added by new event IDs under review.
- The environment variable is a developer/tester switch, not a user feature. It is discoverable only through documentation.
- Two new packages enter the Infrastructure dependency graph (Serilog.Sinks.File pulls Serilog 4.2.0 minimum; the graph resolves to 4.3.1) and the license inventory.
- Log content is outside the Application contract's reach to misuse, but adapters and Desktop could still call `Trace` or `Console` directly; reviews and the privacy test below guard against it.

## Migration

None; no previous log files exist. Affected: BE-07, BE-05 (Trace replaced), FE-06, BE-06/FE-23 (opt-in setting later), BE-28 (dependency review), BE-29 (uninstall data retention prompt includes the Logs folder).

## Security/Performance Impact

No network or elevation. Files live in the user's profile with default user ACLs; they hold no personal data by construction. Disk use is capped as above. Logging calls are synchronous but small writes; the sink is not buffered so a crash does not lose the last events. Failure to write is silent by design (logging must never crash the app) and is covered by a test.

## Validation

Unit tests: redaction of hostile strings, no file path/e-mail/SSID/text in output, level filtering in normal vs diagnostic mode, size/count retention using a temporary directory with a tiny size limit, exception type only, never throws on a read-only directory or a null/blank event, flush on dispose. Build, format and the existing architecture tests (Application and Domain stay package-free). A runtime check of the real exe: start/close and the unreachable-primary case produce expected lines, and a scan of the log folder finds no workspace path or user name. Not verified: behavior on a full disk, antivirus locking, other Windows builds.
