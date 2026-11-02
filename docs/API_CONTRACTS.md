# API contract discovery — Sprint 0

Status: discovery-only API strategy; no accepted production contract version. BE-01 adds the Application project scaffold without introducing public types, ports or settings schemas. Exact contracts remain BE-04 work.
PRD §2.1 and ARCHITECTURE §6 contain illustrative, differing signatures. Do not implement both.

| Topic | Existing discrepancy / required decision | Impact before implementation |
|---|---|---|
| Wi-Fi | PRD `INetworkService` vs architecture `IWifiService` | Normalize through accepted ADR before BE-04/BE-12 and FE-15 |
| Volume writes | PRD returns `Task`; architecture returns typed `OperationResult` | Define recoverable outcomes, rollback and disposal before BE-04/BE-10/BE-11, FE-14 |
| Media | `MediaSession` / `MediaSnapshot` vs `MediaSessionSnapshot`; play/pause omitted in architecture example | One capability-neutral snapshot/command contract for BE-04, BE-15–18, FE-17–19 |
| Conversion | Plan on `IConversionService` vs separate planner | Resolve once for BE-04/BE-19–27, FE-20–22 |
| Streams | `IObservable<T>` examples lack replay/threading/failure semantics | Document ownership, dispatcher delivery and unsubscription before production |

Accepted strategy (ADR-0002, 2026-10-27): `IWifiService`, typed command results, cancellation and explicit lifetime; exact signatures/read/subscription semantics still require BE-04 review before implementation. No COM/WinRT/WPF types may cross the Application boundary. This does not accept a complete production API version.

Temporary probe records are internal to `Lunar.Windows.Spike`; the executable emits privacy-minimized JSONL for evidence. Desktop does not reference the probe executable. These are not public application contracts, versioned persistence or production services.

## Accepted production contracts (BE-04, 2026-11-03)

[ADR-0006](ADR/ADR-0006-application-port-contracts.md) (Accepted) fixes these `Lunar.Application` contracts; they supersede the illustrative PRD §2.1 / ARCHITECTURE §6 snippets for the listed ports. Implemented in `src/Lunar.Application/Abstractions/{System,Media}` with models in `System/Models` and `Media/Models`; evidence in [BE-04](evidence/sprint-1/BE-04.md).

- Reads return `Task<Result<T>>`; commands return `Task<Result>`; cancellation is `Result.Cancelled()`; success of a command means the OS confirmed it.
- `IVolumeService` (0..1 scalar), `IWifiService`, `IBluetoothService` (state plus `SetEnabledAsync`, `Changes`), `IClock.LocalNow`, `ISystemSettingsLauncher.OpenAsync(SettingsPage)` with the whitelist `Wifi`, `Bluetooth`, `DateAndTime`, `IMediaService` (sessions, `PlayPauseAsync`, `NextAsync`, `PreviousAsync`, `SeekAsync`, `Changes` emitting the full session list).
- `Changes` observables: no replay, serialized delivery on an arbitrary thread, no `OnError`, `OnCompleted` on dispose, idempotent unsubscribe. Artwork is a byte buffer of at most 2 MiB. SSID is not in any contract.
- Version: unversioned until first release; changes need a new ADR and FE notice.

Later accepted contracts: [ADR-0008](ADR/ADR-0008-single-instance-coordination.md) `IInstanceCoordinator` (BE-05), [ADR-0009](ADR/ADR-0009-diagnostic-logging.md) `IDiagnosticLog` with a free-text-free `DiagnosticEvent` (BE-07), and [ADR-0010](ADR/ADR-0010-display-environment-port.md) `IWindowEnvironment` with `DisplayInfo`/`PixelRect` and the pure `IslandPlacement` rules (BE-08, `DisplaysChanged` carries no data and is coalesced).

[ADR-0011](ADR/ADR-0011-settings-store.md) `ISettingsRepository` (`LoadAsync` returns usable settings plus a `SettingsRecovery` notice for any file content and fails only on cancellation or an unreadable file; `SaveAsync` normalizes and writes atomically, `Unsupported` for a file from a newer Lunar) with the immutable `LunarSettings` v1 field set and pure `SettingsRules` (BE-06). The file is `%LocalAppData%\Lunar\settings.json`, `schemaVersion` 1; additive fields need no version bump, breaking changes need a migration step, a fixture and a new ADR.

Not yet defined (deferred in ADR-0006; `IWindowEnvironment` was delivered by BE-08 and `ISettingsRepository`/`LunarSettings` by BE-06): conversion planner/executor and PDF rasterization ports (BE-19/20/24), island state (BE-09). The Sprint 0 discrepancy table above still applies to those.

## Continuation decision package

ADR-0002 was accepted by the user on 2026-10-27: `IWifiService`, `MediaSessionSnapshot`, typed command results and separate conversion planner/executor strategy. Required media transports are retained. This does **not** constitute an accepted full C# API version. Exact read-result/error/cancellation and stream replay/threading/disposal semantics must be presented for review before BE-04 implements them. No contract signature or production project was added by recording acceptance.

ADR-0003 was accepted on 2026-10-27: an Application-owned PDF rasterization port, native engine in Windows, orchestration in Conversion and composition in Desktop. No Windows↔Conversion reference or WinRT PDF/stream type may leak across the port. The disposable CLI must not be promoted as that adapter. BE-04/BE-24 must review bounded BCL stream/DTO lifetime and cancellation shapes before implementation; no task dependency/acceptance criteria changed.
