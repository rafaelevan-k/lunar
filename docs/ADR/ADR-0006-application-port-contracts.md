# ADR-0006: Application port contract semantics (BE-04)

- Status: **Accepted**
- Date: 2026-11-03
- Approver: User (explicit approval in chat: "Terima ADR-0006", 2026-11-03, as proposed including the deferrals; the package license/advisory check remains a condition before restore)
- Approval scope requested: exact signatures and semantics for the system and media ports, the composition-root DI package, and the deferrals listed below. ADR-0002 already accepted the naming/result strategy; this ADR supplies the exact shapes it said BE-04 must present.

## Context

BE-04 must define Application ports and DI composition. ADR-0002 requires BE-04 to present read-result shapes, cancellation, and subscription replay/threading/disposal semantics before implementation. PRD §2.1 and ARCHITECTURE §6 differ and are illustrative only. BE-03 (DONE) provides `Result`, `Result<T>`, `OperationError`, `ErrorCode` in Domain.

## Decision

**Results and cancellation**
- Reads return `Task<Result<T>>`; commands return `Task<Result>`. Expected failures (unsupported, denied, device absent, timeout, cancelled) are `Result` failures, never exceptions.
- Every async method takes a `CancellationToken`. Ports convert an expected cancellation to `Result.Cancelled()`; unexpected faults stay exceptions and are caught at the use-case/composition boundary.
- A successful command means the OS confirmed it. Callers re-read state to see the authoritative value.

**Capability and state vocabulary** (`Lunar.Application.System.Models`, no Windows types)
- `CapabilityStatus { Supported, Unsupported, PermissionDenied, Unavailable, Unknown }` (from ARCHITECTURE §6).
- `VolumeState(float Scalar /*0..1*/, bool IsMuted, CapabilityStatus Capability)`.
- `RadioState { Unknown, Unavailable, Disabled, Enabled }`; `WifiState(CapabilityStatus Capability, RadioState Radio, WifiConnectivity Connectivity /*Unknown, Disconnected, Connected*/, bool CanToggle)`; `BluetoothState(CapabilityStatus Capability, RadioState Radio, bool CanToggle)`. SSID is not part of the contract in this ADR (privacy; optional later by its own review).

**Ports** (`Lunar.Application.Abstractions.*`)
```csharp
// System
public interface IVolumeService : IAsyncDisposable {
    Task<Result<VolumeState>> GetAsync(CancellationToken ct);
    Task<Result> SetVolumeAsync(float scalar, CancellationToken ct);   // 0..1, adapter rejects out-of-range as InvalidInput
    Task<Result> SetMuteAsync(bool muted, CancellationToken ct);
    IObservable<VolumeState> Changes { get; }
}
public interface IWifiService : IAsyncDisposable {
    Task<Result<WifiState>> GetAsync(CancellationToken ct);
    Task<Result> SetEnabledAsync(bool enabled, CancellationToken ct);
    IObservable<WifiState> Changes { get; }
}
public interface IBluetoothService : IAsyncDisposable {
    Task<Result<BluetoothState>> GetAsync(CancellationToken ct);
    Task<Result> SetEnabledAsync(bool enabled, CancellationToken ct);
    IObservable<BluetoothState> Changes { get; }
}
public interface IClock { DateTimeOffset LocalNow { get; } }
public enum SettingsPage { Wifi, Bluetooth, DateAndTime }              // whitelist for ms-settings URIs
public interface ISystemSettingsLauncher { Task<Result> OpenAsync(SettingsPage page, CancellationToken ct); }

// Media
public interface IMediaService : IAsyncDisposable {
    Task<Result<IReadOnlyList<MediaSessionSnapshot>>> GetSessionsAsync(CancellationToken ct);
    Task<Result> PlayPauseAsync(string sessionId, CancellationToken ct);
    Task<Result> NextAsync(string sessionId, CancellationToken ct);
    Task<Result> PreviousAsync(string sessionId, CancellationToken ct);
    Task<Result> SeekAsync(string sessionId, TimeSpan position, CancellationToken ct);
    IObservable<IReadOnlyList<MediaSessionSnapshot>> Changes { get; }   // full session list; a missing id means the session closed
}
```
- `MediaSessionSnapshot(string Id, string? AppName, string? Title, string? Artist, MediaPlaybackStatus Status, TimeSpan? Position, TimeSpan? Duration, MediaCapabilities Capabilities, bool IsCurrent, ReadOnlyMemory<byte> Artwork)`; `MediaCapabilities(bool CanPlayPause, bool CanNext, bool CanPrevious, bool CanSeek)`. Artwork is an immutable byte buffer, at most 2 MiB, empty when absent, so no stream disposal crosses the port. Seeking requires a finite duration and `CanSeek`; otherwise the adapter returns `Unsupported`.

**Observable semantics** (documented on each port)
- No replay: subscribe, then call `GetAsync` for the initial value.
- Delivery is on an arbitrary thread; ViewModels marshal to the Dispatcher. Adapters serialize `OnNext` calls.
- `OnError` is never called; failures are represented by state (`Capability`/`Radio`) or are logged. `OnCompleted` is called when the service is disposed.
- Disposing a subscription is idempotent and thread-safe; after service disposal methods return `Unavailable`/throw `ObjectDisposedException` only for programmer error.
- `IObservable<T>` is BCL; no Rx package is added.

**Composition (DI)**
- Add `Microsoft.Extensions.DependencyInjection` (MIT, Microsoft) to `Lunar.Desktop` only, central version pinned and locked after a license/advisory check. Application, Domain and adapters stay package-free (the existing architecture tests enforce this).
- `Lunar.Desktop/Composition/ServiceRegistration.cs` exposes `AddLunar(this IServiceCollection)`. Container is built with `ValidateScopes` and `ValidateOnBuild`. Adapter registrations are added by the tasks that create the adapters; BE-04 registers none because none exist.
- Lifetimes follow ARCHITECTURE §8: OS services singleton and disposed with the container; use cases transient.

**Deferrals (explicit amendments to BE-04's wording "system, media, conversion, settings and shell")**
- Conversion ports (`IConversionPlanner`, `IConversionService`, PDF rasterization port): deferred to BE-19/20/24, because their models depend on Domain conversion types (BE-19) and the ADR-0003 stream/DTO review. Defining them now would require placeholder models.
- `ISettingsRepository` and `LunarSettings`: deferred to BE-06; the settings schema needs its own review and ADR.
- `IWindowEnvironment` and display models: deferred to BE-08, which already owns `Application/Abstractions/Shell`.
- Island state (`IslandState`, `TransitionIsland`): BE-09.

## Alternatives

- Exceptions for expected failures: rejected (ARCHITECTURE §4.2, ADR-0002).
- Repeat of PRD signatures (`Task` commands, `INetworkService`): rejected by ADR-0002.
- `Stream` for artwork: needs disposal rules across the port; byte buffer is simpler and bounded.
- Event/async-stream instead of `IObservable<T>`: acceptable per ARCHITECTURE §6; observable kept because the illustrative contracts use it and it needs no package.
- Ship placeholder conversion/settings/shell interfaces now: rejected (AGENTS §5, no placeholder production features).

## Consequences

- FE and later BE tasks can fake every system and media port with plain classes (no mocking library needed).
- Deferred ports mean BE-04's acceptance ("mocks usable in tests; scopes and disposal verified") is demonstrated on the system/media ports and DI container only; the deferrals are tracked in BE-06/08/19–24.
- Contract changes after acceptance need a new ADR and notice to FE.

## Migration

No production API exists yet. Affected: BE-04, BE-10–18, BE-11 use cases, FE-13–19. Updates on acceptance: `docs/API_CONTRACTS.md`, and PRD §2.1/ARCHITECTURE §6 illustrative notes.

## Security/Performance Impact

No new network, privilege or persistence. SSID and file paths are not in contracts. Artwork is bounded to 2 MiB. `Microsoft.Extensions.DependencyInjection` adds one MIT-licensed managed dependency to Desktop; license and advisory check is part of BE-04 before restore.
