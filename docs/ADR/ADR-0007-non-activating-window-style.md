# ADR-0007: Narrow native exception for the non-activating shell window

- Status: **Accepted**
- Date: 2026-11-03
- Approver: User (explicit approval in chat: "Terima ADR-0007", 2026-11-03; the option itself was chosen earlier the same day)

## Context

ARCHITECTURE §0 rule 3 and §4.6 forbid P/Invoke in View code; native wrappers belong in `Lunar.Windows`. FE-02 requires a pill that does not take activation from the user's current app (ADR-0002 window proposal). Real-run evidence on 2026-11-03 (Windows 11 build 26300, [evidence](../evidence/sprint-1/FE-02.md)): a click on the pill, with `ShowActivated=false` and a `WM_MOUSEACTIVATE` hook returning `MA_NOACTIVATE` (the Sprint 0 approach), left the system with **no foreground window** (`GetForegroundWindow` = 0 for 1.5 s and until it was restored by the test). The same happened with the Sprint 0 spike. Setting `WS_EX_NOACTIVATE` on the window kept the original foreground and focus window unchanged after the same click. WPF exposes no property for this style.

## Decision

Add one `internal` class, `Lunar.Desktop/Shell/NonActivatingWindowStyle.cs`, containing two P/Invokes (`GetWindowLongPtrW`, `SetWindowLongPtrW`) to set/clear `WS_EX_NOACTIVATE` on the shell window handle. It carries no business rules, takes only an `IntPtr` owned by the window, and is called only from `IslandWindow`. The `WM_MOUSEACTIVATE` hook is not used. This is the sole native call permitted in Desktop; any other native need still goes through `Lunar.Windows` and an Application port.

## Alternatives

- Port in Application with an adapter in `Lunar.Windows/Interop`: rule-compliant but adds a public contract that carries a native handle, which ARCHITECTURE §6 discourages, and overlaps BE-08. Rejected by the user for FE-02.
- Keep only the `MA_NOACTIVATE` hook: observed to clear the foreground window.
- No non-activating behavior: contradicts FE-02 and ADR-0002.

## Consequences

Keyboard activation (tray Open, FE-06) needs focus: it must call `NonActivatingWindowStyle.Remove` before `Activate()` and `Apply` afterwards; FE-06 owns that behavior and its evidence. High-DPI, multi-monitor and fullscreen behavior of the style are unverified.

## Migration

None. If the Windows adapter route is chosen later (BE-08 or FE-06), the class is replaced by a port call and removed.

## Security/Performance Impact

No elevation, input capture or network. The style affects only this app's own window. Negligible performance cost.

## Validation

Runtime diagnostic (scratch script, results quoted in FE-02 evidence): foreground and focus unchanged after a real click when the style is set; foreground null after the same click without it. Unit test asserts the style bit on a shown window. Not verified: other Windows builds, touch input, screen-reader behavior.
