# ADR-0008: Single-instance coordination port and lifecycle policy (BE-05)

- Status: **Accepted**
- Date: 2026-11-03
- Approver: User (explicit approval in chat: "Terima ADR-0008", 2026-11-03)
- Approval scope requested: one new Application port, its Infrastructure adapter and Desktop wiring, and the startup/shutdown policy below.

## Context

BE-05 needs single-instance startup, "second launch activates first", graceful shutdown, tray lifetime and cancellation tokens. ARCHITECTURE §4.6 forbids Desktop from calling OS/IPC details and allows it to reference adapters only for DI; §7.1 says "named mutex/IPC only if needed". Today a second launch creates a second pill and a second tray icon (FE-06 limitation). ADR-0006 deferred shell contracts; no port covers this. AGENTS §5 requires an ADR for a new public contract.

## Decision

**Port** (`Lunar.Application/Abstractions/Shell/IInstanceCoordinator.cs`, no native types):
```csharp
public enum InstanceRole { Primary, Secondary }
public interface IInstanceCoordinator : IDisposable {
    Result<InstanceRole> Start();                                   // synchronous, fast: claims or detects the primary
    Task<Result> RequestPrimaryActivationAsync(CancellationToken ct); // Secondary only; bounded wait for the primary
    event Action? ActivationRequested;                               // Primary only; raised on a thread-pool thread
}
```
Semantics: `Start` is called once. A second `Start` call, or `RequestPrimaryActivationAsync` on a Primary, fails with `InvalidInput`. Failures are `Result` failures (`IoFailure`, `Timeout`, `Cancelled`). `Dispose` is synchronous and non-blocking: it cancels the listener and releases OS handles, is idempotent, and the listener ends on its own shortly afterwards. Handlers of `ActivationRequested` must marshal to the Dispatcher.

**Adapter** (`Lunar.Infrastructure/Startup/SingleInstanceCoordinator.cs`): a per-session named mutex (`Local\…`, created but never waited on; being its creator means Primary; disposed with the process) decides the role. The Primary listens on a per-user named pipe (`PipeOptions.CurrentUserOnly | Asynchronous`, one instance at a time). A Secondary connects with a bounded timeout and writes exactly one fixed byte; the Primary reads at most one byte under a 2-second timeout, ignores anything else, and raises `ActivationRequested` only for that byte. No command-line arguments, file paths or other data cross the pipe, so there is nothing to parse or log. Names are constructor parameters so tests use unique ones.

**Lifecycle policy** (`App.xaml.cs`, startup/lifetime only):
- Startup: build the container; `Start()`. Secondary → request activation (3-second budget), then exit with code 0 on success or 3 if the primary did not answer; no window or tray is created. Primary → show the window, create the tray, subscribe `ActivationRequested` → `OpenForKeyboard` on the Dispatcher. If `Start()` itself fails → exit code 1. Never restart automatically.
- Shutdown paths (tray Exit, window close, `WM_CLOSE`, session end) all reach `OnExit`: cancel the application-lifetime `CancellationTokenSource`, unsubscribe, dispose tray, coordinator and container, in that order, without blocking waits.
- Startup exceptions are caught at the top of `OnStartup`, shut the app down with exit code 1, and record only the exception type name via `Trace` until structured logging exists (BE-07). No dialog and no data leak.

**Registration**: `ServiceRegistration` registers `IInstanceCoordinator` as a singleton factory for the production names; this is the first adapter in the composition root.

## Alternatives

- Mutex and pipe code directly in `App.xaml.cs`: violates ARCHITECTURE §4.6 and is untestable.
- Windows messages (`FindWindow` + `WM_COPYDATA`/registered message): needs native calls, window-title lookup and spoofable targets.
- Named mutex with ownership via `WaitOne`: thread-affine release and abandoned-mutex handling; creation-only ownership avoids both.
- `IAsyncDisposable` with an awaited stop: would force a blocking wait in `OnExit`; synchronous cancel-and-release avoids it.
- Machine-wide (`Global\`) mutex: would block a second logged-on user from running their own instance.

## Consequences

- Second launch no longer creates a second pill/tray icon; it exits 0 after nudging the first.
- Per-session scope: two users, or an RDP session and the console, each get their own instance. A same-session process can pre-create the mutex or pipe name and block startup (local denial of service only; no elevation or data exposure).
- "Activate" means what the tray Open already does (`OpenForKeyboard`). Whether Windows lets the primary take the foreground from a background launch is verified empirically in BE-05 evidence; if it does not, the pill simply remains where it is and the result is reported.
- Exit codes 0/1/3 become part of the process contract for the installer (BE-29) and tests.

## Migration

None; no persisted data. Affected: BE-05, FE-06 (single-icon limitation resolved), BE-29 (installer launch behavior), BE-30.

## Security/Performance Impact

No network, elevation or file access. Pipe is restricted to the current user and carries one constant byte with timeouts; unknown data is ignored. Idle cost is one waiting overlapped pipe read. Pipe/mutex names are not secrets and not logged.

## Validation

Unit/integration tests in `Lunar.Infrastructure.Tests` with unique names (roles, activation delivery, wrong byte ignored, timeout and cancellation, dispose releases the mutex, restart after dispose), DI tests in Desktop, and a two-process smoke on the real exe (second launch exits quickly with 0; one pill, one tray registration). Not verified: other Windows builds, RDP/fast-user-switching, antivirus interaction.
