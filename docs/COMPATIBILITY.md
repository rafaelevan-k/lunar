# Compatibility discovery and remaining manual matrix

Date: 2026-10-19, Asia/Jakarta. Evidence: [environment](evidence/sprint-0/environment.json), [successful run](evidence/sprint-0/20261018T181705Z/commands.jsonl), [native probes](evidence/sprint-0/20261018T181705Z/native-probes.jsonl), [WPF smoke](evidence/sprint-0/20261018T181705Z/window-smoke.jsonl).

Observed host: Windows NT 10.0.26300.9711 x64, registry DisplayVersion 26H2; registry ProductName says Windows 10 Home Single Language. That product-name field is not sufficient to claim a Windows 10 22H2 test. Standard-user token, interactive session, PowerShell 5.1.26100.9711. One monitor 1920×1080, work area 1920×1020, effective DPI 120 (125%).

Initial system SDK: 9.0.306. Verified portable SDK: 10.0.402; .NET/WindowsDesktop runtime 10.0.13. Projects target .NET 10. Windows probe uses Windows SDK target 10.0.19041.0; SDK-managed reference pack restored as 10.0.19041.57. A target framework version is not evidence that older OS behavior has been tested.

2026-10-27 update: repeat native/WPF runtime checks on the same build/one 120-DPI monitor pass; all three Release builds succeed. Separate normalized PNGs now pass 150/300 density, dimensions and complete pixel equality for synthetic sRGB fixtures. Word export again TIMEOUT, now isolated to `ExportAsFixedFormat` after successful fixture opening/pagination. UI discovery exposed no targetable prototype window; interactive/manual cases remain NOT_RUN. The user confirmed no additional target OS device or monitor, accepted [ADR-0005](ADR/ADR-0005-sprint-0-validation-deferral.md), and approved Sprint 0 discovery closure with limitations. [Approval](evidence/sprint-0/closure-20261027/APPROVAL.md), [technical evidence](SPIKE_RESULTS.md#completion-attempt--2026-10-27). This is not compatibility PASS. Actual shell interaction and mixed-DPI/work-area proof remain mandatory for relevant Sprint 1 tasks/gate; the full target OS/fullscreen/sleep/RDP matrix is required for FE-26/BE-30 before release. Original matrix below retains historical observations.

| Surface | Observed host result | Boundary / fallback still to verify |
|---|---|---|
| WPF window | Instrumented runtime PASS: rendered 160×44 DIP, transparent/topmost properties true, taskbar false, inactive at render, tier 2; five-second exit 0; tray disposed | Visual snapshot is WPF render output, not a desktop screenshot proving z-order/hit testing. Mouse nonactivation and keyboard path NOT_RUN |
| DPI/work area | One primary monitor at 120 DPI; WPF reports same DPI and top-center position | 100/150/200%, mixed-DPI, hotplug, relocation, taskbar changes NOT_RUN |
| Core Audio | Actual COM default render endpoint activated; finite scalar within 0–1; mute readable | Writes, endpoint removal, callbacks, external keyboard change NOT_RUN; unavailable typed fallback in production |
| Native Wi-Fi | Open/enumerate/query radio succeeds; one disconnected interface, six PHY entries with software/hardware on | Not connected despite radio on. No SSID/scan/current_connection. Toggle, airplane mode, absent adapter, permission denial NOT_RUN; Settings fallback |
| Bluetooth | WinRT enumerates one Bluetooth radio, state On | No RequestAccessAsync, toggle/pairing/event test. Enumeration does not prove control permission; Settings fallback |
| GSMTC | Manager acquired; one current session; finite timeline; position control advertised; pause advertised, play not advertised in snapshot | Actual commands/seek acceptance and position accuracy NOT_RUN; no metadata collected; disabled UI needed for unsupported/live sessions |
| Conversion | Disposable harness with SkiaSharp 4.153.1/Open XML 3.5.1 and OS PDF rendering tested; scoped results in CONVERTER_DISCOVERY | Not a production provider; DOCX layout, full matrix, hostile/cancellation/transaction tests unverified; native redistribution review pending |
| PDF render DPI | Correct 1275×1650 / 2550×3300 pixels after normalization on host 125% | PNG density metadata remains ~120; other scales/monitors/rotation/crop/OS unverified |
| LibreOffice / reference | No standard path/PATH/App Paths match; isolated Word reference export timed out at 60 s, no PDF produced, owned process cleaned | Custom installation not exhaustively searched; no LibreOffice run; DOCX fidelity NOT_RUN |
| Installer | User accepted ADR-0004 strategy on 2026-10-27: WiX 7.0.0 per-user MSI/self-contained win-x64 | No tooling EULA/restore/install/publish/signing/rollback validation; strategy acceptance only |

## Manual cases required before relevant gate claims

All below: NOT_RUN; no human interaction evidence was collected in this session.

1. On Windows 10 22H2 and stable supported Windows 11 x64: launch the prototype, inspect transparency/rounded corners/z-order, ensure clicks outside pill reach underlying app. Use another app in foreground; click pill and verify focus is retained.
2. Open tray → Open prototype; verify intentional keyboard focus, Escape close, tray Exit and no lingering prototype process/icon.
3. At 100/125/150/200%, move across mixed-DPI monitors, change primary monitor, unplug/reconnect, relocate taskbar; verify bounds/clipping. Prototype currently handles initial primary placement only, so dynamic repositioning is a known gap, not a tested feature.
4. Fullscreen game/video, sleep/resume and RDP: record overlay visibility and bounds. Fullscreen hide/reposition policy is not implemented in prototype.
5. Real system controls: missing endpoint/adapters, external volume/mute, airplane mode and denied radio control. Only execute writes during explicitly scoped interactive validation; restore original state.
6. Media: local seekable file, supported browser/player session and live/nonseekable stream; verify controls, actual seek within 1 second, disabled unsupported controls, session closure and switching. Current probe only reads capabilities.
7. Synthetic conversion fixtures and installer scenarios described in DEPENDENCY_REVIEW and ADR-0004.

Current vendor support and product compatibility are separate. [Microsoft's .NET 10 supported OS policy](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md) must be reconciled with PRD's Windows 10 22H2 target before release claims; no target was changed during discovery.
