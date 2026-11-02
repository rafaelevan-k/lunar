# ADR-0010: Display environment port, placement rules and settings launcher (BE-08)

- Status: **Accepted**
- Date: 2026-11-03
- Approver: User (explicit approval in chat: "Terima ADR-0010", 2026-11-03)
- Approval scope requested: the `IWindowEnvironment` contract and models, the pure placement rules, the Windows adapter design (native enumeration, hidden notification window), the settings launcher implementation, and how BE-08's hardware-dependent acceptance is treated.

## Context

BE-08 provides monitor/work-area/DPI notifications, positioning-safe abstractions and an OS settings launcher, with "monitor hotplug and DPI events handled" and fallback behavior as acceptance. ADR-0006 deferred `IWindowEnvironment` to this task; ARCHITECTURE §3 lists `Windows/Environment/MonitorInfoProvider.cs` and `WindowsSettingsLauncher.cs`. FE-05 (Desktop positioning) will consume the port. The Windows layer must not reference WPF or WinForms (architecture tests forbid `System.Windows*` types there), so the adapter cannot use `SystemParameters` or `Screen`. Sprint 0 only enumerated one 120-DPI monitor; mixed-monitor, hotplug and 100–200% DPI evidence is deferred by ADR-0005 to FE-05/BE-08 and the Sprint 1 gate, and this machine has one monitor. `ISystemSettingsLauncher` and the `SettingsPage` whitelist already exist (ADR-0006); BE-14 (Sprint 4) also mentions "safe ms-settings deep links".

## Decision

**Port** (`Lunar.Application/Abstractions/Shell/IWindowEnvironment.cs`):
```csharp
public interface IWindowEnvironment : IDisposable {
    Result Start();                                   // begins change notification; call once
    Result<IReadOnlyList<DisplayInfo>> GetDisplays(); // current snapshot, synchronous and fast
    event Action? DisplaysChanged;                    // thread-pool thread; coalesced; re-read with GetDisplays
}
```
`DisplaysChanged` means "layout, work area (taskbar) or DPI may have changed"; it carries no data. Bursts are coalesced into one event after 250 ms of quiet. `GetDisplays` works without `Start` (notifications are optional). Failures are `Result` failures (`DeviceUnavailable` when no display can be enumerated, for example a locked or disconnected session; `IoFailure` when the listener cannot start; `InvalidInput` for a second `Start`). `Dispose` is synchronous, non-blocking and idempotent.

**Models** (`Lunar.Application/Shell/Models`): `PixelRect(X, Y, Width, Height)` in physical pixels (non-negative size); `DisplayInfo(Id, IsPrimary, Bounds, WorkArea, DpiX, DpiY)` with `Scale = DpiX / 96`; `Id` is the display device name (for example `\\.\DISPLAY1`), not personal data, and is the key a later setting may store. Validation rejects blank ids, non-positive DPI and a work area not inside its bounds.

**Placement rules** (`Lunar.Application/Shell/IslandPlacement.cs`, pure and unit-tested):
- `SelectDisplay(displays, preferredId)`: the preferred display if present, otherwise the primary, otherwise the first; none → failure.
- `PlaceTopCenter(display, widthDip, heightDip, topOffsetDip)`: converts DIP to pixels with the display's scale, centers horizontally in the **work area**, offsets from its top, then clamps so the rectangle is entirely inside the work area (shrinking only if it cannot fit).
- `DisplayInfo.Fallback` (1280×720 work area, 96 DPI, primary) for callers when enumeration fails, so the shell never has no position.
These are the "positioning-safe abstractions"; Desktop (FE-05) applies the resulting pixel rectangle.

**Windows adapter** (`Lunar.Windows/Environment/MonitorInfoProvider.cs`, `Lunar.Windows/Interop`): `EnumDisplayMonitors`, `GetMonitorInfoW`, `GetDpiForMonitor` (effective DPI), evaluated under a temporary per-monitor-v2 DPI thread context so values are physical pixels whatever the host process's DPI awareness (the previous context is restored). Notifications come from a hidden, never-shown top-level window on one dedicated thread with its own message loop, handling `WM_DISPLAYCHANGE`, `WM_SETTINGCHANGE` with `SPI_SETWORKAREA`, and `WM_DPICHANGED`; nothing in `lParam` is read. Window, class and thread are cleaned up by that thread after `Dispose` posts `WM_CLOSE`. All native handles stay inside `Windows/Interop`. Optional `IDiagnosticLog` events (`display.enumerate.failed`, `display.listener.failed`, error code and type only).

**Settings launcher** (`Lunar.Windows/Environment/WindowsSettingsLauncher.cs`): implements `ISystemSettingsLauncher` for the three whitelisted pages only (`ms-settings:network-wifi`, `ms-settings:bluetooth`, `ms-settings:dateandtime`) through the shell with a fixed URI; any launch failure is a failed `Result`, never an exception. BE-08 delivers this class; BE-14 keeps the locale-aware clock and the real-device verification of the links and does not re-implement the launcher.

**Registration**: `ServiceRegistration` registers `IWindowEnvironment` and `ISystemSettingsLauncher` as singletons. `App` does not start the environment yet; FE-05 does.

**Acceptance and evidence policy**: hotplug and mixed-DPI behavior cannot be exercised on this single-monitor machine. BE-08 is tested with (a) pure placement tests covering mixed scales, negative-coordinate secondary monitors, taskbar on every edge, missing preferred monitor and empty lists, (b) enumeration on the real host compared with an independent probe, and (c) real window messages posted to the hidden window to prove the notification, coalescing and disposal path. These do **not** prove that Windows delivers the messages for a real hotplug or DPI change. BE-08 therefore stays IN_REVIEW until the user supplies hardware evidence (a second monitor or DPI change) or explicitly defers it, as ADR-0005 anticipated.

## Alternatives

- `System.Windows.Forms.Screen` / `SystemParameters` in the adapter: forbidden by the layer rules and not DPI-correct.
- `Microsoft.Win32.SystemEvents` (needs a framework reference or a new package; does not report work-area changes reliably).
- Notification window in Desktop (WPF `HwndSource` hook): puts OS knowledge in the View layer.
- Event with data payload: couples consumers to a snapshot that may already be stale; re-reading is cheap and simpler.
- Polling: forbidden by ARCHITECTURE §1 (event-driven).

## Consequences

- One extra thread (idle in `GetMessage`) for the app's lifetime once started.
- DPI-change delivery for a secondary monitor relies on Windows broadcasting `WM_DISPLAYCHANGE`/`WM_SETTINGCHANGE`; if it does not, FE-05 must also handle WPF's per-window `DpiChanged` (covered in FE-05, to be verified on hardware).
- Placement is physical pixels; Desktop converts to DIPs only for WPF properties (FE-05).
- The ADR-0005 obligation for BE-08 remains open until real hardware evidence exists.

## Migration

None. Affected: BE-08, FE-05, FE-03 (scale), BE-14 (launcher not re-implemented), FE-26/BE-30 (full matrix), `docs/API_CONTRACTS.md`.

## Security/Performance Impact

No network, elevation or file access. The hidden window ignores message data. The launcher accepts only the enum, never a caller URI. Idle cost is one blocked thread; coalescing bounds re-reads to about four per second under a storm.

## Validation

Unit tests: placement, models, event coalescing, `Start` rules, disposal (listener thread ends), launcher mapping and failure containment (with an injected starter; the real Settings app is not opened by tests), architecture tests (Windows layer references no WPF/WinForms). Real host: enumeration versus an independent probe (1 monitor, 120 DPI expected), posted-message run, exe unaffected. Not verified: hotplug, mixed DPI 100/150/200%, taskbar on other edges, RDP, Windows 10 22H2.
