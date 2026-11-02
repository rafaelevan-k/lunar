# Lunar sprint status

## Latest update — FE-03 (2026-11-03)

- Authorization: user assigned FE-03. Only FE-03 was implemented; no ADR (Desktop-only). Sprint 1 gate OPEN.
- Task / owner / status: FE-03 / Claude (sole integrator) / **DONE by user decision** ("Terima, tandai DONE"). Dependencies: FE-02, FE-04, FE-05 DONE.
- Scope: `IslandLayout` pure sizing rules (idle 160×44; expanded min 380×320, capped to the work area); `IslandPositionController` now measures, sizes and places; `IslandViewModel.IsExpandedLayout`; window content-change event, forced real measurement, radius 999/24 by layout; 27 new tests.
- Evidence: [FE-03 evidence](evidence/sprint-1/FE-03.md); locked restore 0, Release build 0 warnings/errors, 437 tests passed (UI suite run three extra times), format 0. Real exe geometry unchanged: 860,10 200×55 at 120 DPI.
- Accepted evidence level: "no clipping at 100/125/150/200% DPI" is shown by a rules matrix and by real windows at the host's 125% only; the expanded layout is not reachable in the running app before Sprint 3. Real 100/150/200% on-screen confirmation stays unverified (validation debt for the Sprint 1 gate and Sprint 3).
- Done tasks: BE-01, BE-03, BE-04, BE-05, BE-06, BE-07, BE-08, FE-01, FE-02, FE-03, FE-04, FE-05, FE-06. Remaining TODO: BE-02. Open validation debt: second monitor / mixed DPI, DPI-free work-area change, real 100/150/200% on-screen check. Next authorized action: WAIT for user approval or assigned task.

## Latest update — FE-04 (2026-11-03)

- Authorization: user assigned FE-04. Only FE-04 was implemented; no ADR (Desktop-only presentation state, no new Application contract, project, package or native call). Sprint 1 gate OPEN.
- Task / owner / status: FE-04 / Claude (sole integrator) / **DONE**. Dependencies: FE-02 DONE.
- Scope: `IslandViewModel`, `IslandVisualState`, `RelayCommand`; transient DI registration; `IslandWindow` DataContext and `ItemStatus` binding; 39 new tests.
- Scoping decision: BE-09 (Sprint 2–3) owns the validated state machine, guards, interruption and time. FE-04 therefore holds only the minimal deterministic presentation transitions (a request during an animation is ignored) and defines no Application type; inline errors never change the state (ARCHITECTURE §8).
- Evidence: [FE-04 evidence](evidence/sprint-1/FE-04.md); locked restore 0, Release build 0 warnings/errors, 410 tests passed (UI suite run three extra times), format 0. Real exe: UI Automation reads ItemStatus "Collapsed" from the window, clean exit.
- Open: nothing calls Toggle/Collapse yet (no animation or click wiring until FE-03 / Sprint 3); BE-09 will replace the transition logic; status text is English only until localization.
- Done tasks: BE-01, BE-03, BE-04, BE-05, BE-06, BE-07, BE-08, FE-01, FE-02, FE-04, FE-05, FE-06. Remaining TODO: BE-02, FE-03. Open validation debt unchanged (second monitor / mixed DPI, DPI-free work-area change). Next authorized action: WAIT for user approval or assigned task.

## Latest update — FE-05 (2026-11-03)

- Authorization: user assigned FE-05. Only FE-05 was implemented; no new ADR (consumes ADR-0010/0011 contracts, no native call). Sprint 1 gate OPEN.
- Task / owner / status: FE-05 / Claude (sole integrator) / **DONE** (user confirmed the pill stayed visible after four real display changes). Dependencies: FE-02, BE-06, BE-08 DONE.
- Scope: `IslandPositionController` (placement on preferred/primary display, follows `DisplaysChanged` and WPF `DpiChanged`, safe fallback), `App` loads settings and starts it, temporary placement removed from `IslandWindow`; 22 new tests.
- Evidence: [FE-05 evidence](evidence/sprint-1/FE-05.md); locked restore 0, Release build 0 warnings/errors, 371 tests passed (UI suite run three extra times), format 0. Real exe: window 860,10 200×55 at 120 DPI, centered in the 1920×1020 work area, clean exit.
- Real-change evidence: the user-run log shows four real changes, each raising WPF `DpiChanged` and then `DisplaysChanged` (about 0.2 s later), no fallback or error.
- Open validation debt for the Sprint 1 gate (ADR-0005): second real monitor (hotplug, mixed DPI, preferred-monitor removal) and a taskbar auto-hide / DPI-free work-area change are not evidenced.
- Done tasks: BE-01, BE-03, BE-04, BE-05, BE-06, BE-07, BE-08, FE-01, FE-02, FE-05, FE-06. Remaining TODO: BE-02, FE-03, FE-04. Next authorized action: WAIT for user approval or assigned task.

## Latest update — BE-06 (2026-11-03)

- Authorization: user started BE-06 ("ya") and accepted ADR-0011. Only BE-06 was implemented; Sprint 1 gate OPEN.
- Task / owner / status: BE-06 / Claude (sole integrator) / **DONE**. Dependencies: BE-03, BE-04, BE-07 DONE.
- Scope: `LunarSettings` + `SettingsRules` + `ISettingsRepository` (Application); `JsonSettingsRepository`, `SettingsMigration`, `SettingsSerializer` (Infrastructure); singleton registration; 106 new tests. No package change.
- Evidence: [BE-06 evidence](evidence/sprint-1/BE-06.md); locked restore 0, Release build 0 warnings/errors, 349 tests passed (Infrastructure suite run three extra times), format 0. Real `%LocalAppData%\Lunar` run: defaults, save, backup, corrupt main restored from backup, then cleaned up.
- Open: no production migration (v1 is the first schema); power loss, redirected `%LocalAppData%`, antivirus locking, Windows 10 22H2 not verified; settings are not consumed by the app yet.
- Done tasks: BE-01, BE-03, BE-04, BE-05, BE-06, BE-07, BE-08, FE-01, FE-02, FE-06. Remaining TODO: BE-02, FE-03, FE-04, FE-05 (FE-05 carries the BE-08 hotplug/DPI obligation). Next authorized action: WAIT for user approval or assigned task.

## Latest update — BE-08 (2026-11-03)

- Authorization: user assigned BE-08 and accepted ADR-0010. Only BE-08 was implemented; Sprint 1 gate OPEN.
- Task / owner / status: BE-08 / Claude (sole integrator) / **DONE by user decision** (2026-11-03, "Tunda ke FE-05"). Dependencies: BE-04, BE-07 DONE.
- Scope: `IWindowEnvironment`, display models, `IslandPlacement` (Application); `MonitorInfoProvider`, `WindowsSettingsLauncher`, `NativeMethods` (Windows); registration; 51 new or adjusted tests.
- Evidence: [BE-08 evidence](evidence/sprint-1/BE-08.md); locked restore 0, Release build 0 warnings/errors, 243 tests passed, format 0 (after a BOM fix), spike builds 0. Real host: one display 1920×1080 at 120 DPI, placement 860,10 200×55 equals the real FE-02 window.
- Deferred validation debt (ADR-0005), explicitly accepted by the user and moved to FE-05: real hotplug, taskbar move or DPI change must raise `DisplaysChanged`; this host has one monitor. `WM_DPICHANGED` cannot be simulated (Windows drops synthetic ones) and is unverified. Still NOT_RUN, still blocks FE-05 and the Sprint 1 gate until observed.
- Done tasks: BE-01, BE-03, BE-04, BE-05, BE-07, BE-08, FE-01, FE-02, FE-06. All others TODO. Next authorized action: WAIT for user approval or assigned task.

## Earlier update — BE-07 (2026-11-03)

- Authorization: user assigned BE-07 and accepted ADR-0009. Only BE-07 was implemented; Sprint 1 gate OPEN.
- Task / owner / status: BE-07 / Claude (sole integrator) / **DONE** (IN_PROGRESS → IN_REVIEW → DONE). Dependencies: BE-04, BE-05 DONE.
- Scope: `IDiagnosticLog` (Application), `SerilogDiagnosticLog` and `LoggingConfiguration` (Infrastructure), registration and `App`/coordinator events, `docs/PRIVACY.md`; packages Serilog 4.3.1 and Serilog.Sinks.File 7.0.0 (Apache-2.0); 27 new or adjusted tests.
- Evidence: [BE-07 evidence](evidence/sprint-1/BE-07.md); locked restore 0, Release build 0 warnings/errors, 187 tests passed, format 0 (after two whitespace fixes in my test file), no vulnerable packages reported, spike build 0. Real-exe logs scanned: no user name, path or document terms.
- Caveats: the real exe now writes `%LocalAppData%\Lunar\Logs` on this machine; multi-day retention, full disk and antivirus cases NOT_RUN; opt-in is an environment variable until BE-06/FE-23; no log export.
- Done tasks: BE-01, BE-03, BE-04, BE-05, BE-07, FE-01, FE-02, FE-06. All others TODO. Next authorized action: WAIT for user approval or assigned task.

## Earlier update — BE-05 (2026-11-03)

- Authorization: user assigned BE-05 and accepted ADR-0008. Only BE-05 was implemented; Sprint 1 gate OPEN.
- Task / owner / status: BE-05 / Claude (sole integrator) / **DONE** (IN_PROGRESS → IN_REVIEW → DONE). Dependencies: BE-04, FE-02, FE-06 DONE.
- Scope: `IInstanceCoordinator` (Application), `SingleInstanceCoordinator` (Infrastructure), `ServiceRegistration` registration, `App` lifecycle policy; 14 new tests.
- Evidence: [BE-05 evidence](evidence/sprint-1/BE-05.md) with [two-process smoke](evidence/sprint-1/be-05-20261103/instance-smoke.json); locked restore 0, Release build 0 warnings/errors, 161 tests passed, format 0 (after one import fix), spike build 0. Second launches exit 0 in about 0.4 s, unreachable primary exits 3, restart after clean exit and kill works, no leftover processes.
- Caveats: foreground on activation is best-effort (took foreground in 1 of 2 real runs); no logging until BE-07; per-session scope; RDP/user-switching NOT_RUN.
- Done tasks: BE-01, BE-03, BE-04, BE-05, FE-01, FE-02, FE-06. All others TODO. Next authorized action: WAIT for user approval or assigned task.

## Earlier update — FE-06 (2026-11-03)

- Authorization: user assigned FE-06. Only FE-06 was implemented; Sprint 1 gate OPEN.
- Task / owner / status: FE-06 / Claude (sole integrator) / **DONE** (IN_PROGRESS → IN_REVIEW → DONE) after the user's manual tray check passed (user-reported, no screenshots). Dependencies: FE-02 DONE.
- Scope: `Shell/TrayController`, `IslandWindow.OpenForKeyboard`, App wiring, placeholder icon, 5 tests. Settings and Start with Windows disabled (FE-23, BE-29); no state indicators yet.
- Evidence: [FE-06 evidence](evidence/sprint-1/FE-06.md); locked restore 0, Release build 0 warnings/errors, 147 tests passed, format 0, spike build 0, scripted smoke exits 0.
- Still open: no single-instance guard until BE-05 (two launches give two icons); Settings/Start with Windows disabled until FE-23/BE-29; ghost icon after a crash is normal Windows behavior; Win10 22H2 and taskbar-position cases NOT_RUN.
- Done tasks: BE-01, BE-03, BE-04, FE-01, FE-02, FE-06. All others TODO. Next authorized action: WAIT for user approval or assigned task.

## Earlier update — FE-02 (2026-11-03)

- Authorization: user assigned FE-02 and chose the Spike/ relocation and the narrow native exception. Only FE-02 was implemented; Sprint 1 gate OPEN.
- Task / owner / status: FE-02 / Claude (sole integrator) / **DONE** (IN_PROGRESS → IN_REVIEW → DONE) after ADR-0007 acceptance and the user-reported high-contrast PASS. Dependencies: FE-01, BE-04 DONE.
- Scope: production `App`, `IslandWindow`, `NonActivatingWindowStyle`, manifest, WinExe; discovery sources moved to `src/Lunar.Desktop/Spike/` (hash-verified unchanged); 3 new UI tests.
- Evidence: [FE-02 evidence](evidence/sprint-1/FE-02.md) with [runtime smoke results](evidence/sprint-1/fe-02-20261103/shell-smoke.json); locked restore 0, Release build 0 warnings/errors, 142 tests passed, format 0, spike build 0. Real-run: pill 160×44 DIP, click-through outside the pill, foreground unchanged after a real click, `WM_CLOSE` exit code 0. Failed/inconclusive attempts retained.
- ADR-0007 accepted by the user (2026-11-03). High-contrast on-screen review (carried from FE-01): PASS as reported by the user, no screenshot or theme name recorded. Still open: no user-facing exit until FE-06; mixed-monitor/DPI matrix (FE-03/05, BE-08), Windows 10 22H2 and fullscreen (FE-26/BE-30) NOT_RUN.
- Done tasks: BE-01, BE-03, BE-04, FE-01, FE-02. All others TODO. Next authorized action: WAIT for user approval or assigned task.

## Earlier update — FE-01 (2026-11-03)

- Authorization: user assigned FE-01. Only FE-01 was implemented; Sprint 1 gate OPEN, no Sprint 2 authorization.
- Task / owner / status: FE-01 / Claude (sole integrator) / **DONE** (IN_PROGRESS → IN_REVIEW → DONE) on the user's explicit decision to close with the on-screen high-contrast review deferred to FE-02. Dependencies: none.
- Scope: `src/Lunar.Desktop/Themes/*` (Colors.Dark/Light/HighContrast, Typography, Spacing, Components), `docs/DESIGN_TOKENS.md`, 16 tests in `tests/Lunar.Desktop.UITests/Themes`.
- Evidence: [FE-01 evidence](evidence/sprint-1/FE-01.md); locked restore 0, Release build 0 warnings/errors, 139 tests passed, format 0 (after one whitespace fix), spike build 0. Manual Windows checks NOT_RUN.
- Light palette approved by the user (2026-11-03). Carry-over: on-screen high-contrast review is an obligation of FE-02 (unverified until then; can reopen FE-01 tokens if it fails).
- Done tasks: BE-01, BE-03, BE-04, FE-01. All others TODO. Next authorized action: WAIT for user approval or assigned task.

## Earlier update — BE-04 (2026-11-03)

- Authorization: user assigned BE-04 and accepted ADR-0006 (exact port contracts, DI package, deferrals). Only BE-04 was implemented; Sprint 1 gate OPEN, no Sprint 2 authorization.
- Task / owner / status: BE-04 / Claude (sole integrator) / DONE for the ADR-0006 scope. Dependencies: BE-01, BE-03 DONE.
- Scope: system and media ports/models, `Lunar.Desktop/Composition/ServiceRegistration`, `Microsoft.Extensions.DependencyInjection` 10.0.13 (Desktop only). Deferred and still open: conversion ports (BE-19/20/24), `ISettingsRepository` (BE-06), `IWindowEnvironment` (BE-08).
- Evidence: [BE-04 evidence](evidence/sprint-1/BE-04.md); locked restore 0, Release build 0 warnings/errors, 123 tests passed, format 0, no vulnerable packages reported. Manual Windows checks NOT_RUN (no OS integration or UI added).
- Interfaces/ADR: ADR-0006 Accepted; [API_CONTRACTS](API_CONTRACTS.md) updated; new package recorded in THIRD_PARTY_NOTICES.
- Done tasks: BE-01, BE-03, BE-04. All others TODO. Next authorized action: WAIT for user approval or assigned task.

## Earlier update — BE-03 (2026-11-03)

- Authorization: user assigned BE-03 ("Ya, BE-03"). Only BE-03 was implemented; Sprint 1 gate OPEN, no Sprint 2 authorization.
- Task / owner / status: BE-03 / Claude (sole integrator) / DONE (IN_PROGRESS → IN_REVIEW → DONE). Dependencies: BE-01 DONE.
- Changed files: `src/Lunar.Domain/Shared/{ErrorCode,OperationError,Result}.cs`, `tests/Lunar.Domain.Tests/Shared/{OperationErrorTests,ResultTests}.cs`, `BE_TASK.md` status, `CHANGELOG.md`, this file.
- Evidence: [BE-03 evidence](evidence/sprint-1/BE-03.md); locked restore 0, Release build 0 warnings/errors, 92 tests passed (19 new), format check 0. Manual Windows checks NOT_RUN (not applicable).
- Interfaces/ADR: new public Domain types only; no ADR, no dependency change. Port signatures remain BE-04.
- Done tasks: BE-01, BE-03. All others TODO. Next authorized action: WAIT for user approval or assigned task.

## Earlier update — Sprint 1 / BE-01

- Updated: 2026-10-27, Asia/Jakarta.
- Authorization: user explicitly assigned “kerjakan id task BE-01” after Sprint 0 closure. This authorizes BE-01 in Sprint 1 only; the broad Sprint 1 prompt is context, not assignment of every task.
- Current sprint: Sprint 1 — Foundation, gate OPEN; no Sprint 2 authorization.
- Task / owner / status: BE-01 / Codex (sole integrator, serialized edits) / DONE (IN_PROGRESS → IN_REVIEW → DONE, 2026-10-27).
- Dependencies: accepted ADR-0001 six-layer/solution/SDK direction and user-approved Sprint 0 closure; both satisfied. ADR-0005 deferred shell/display evidence remains required at its later deadlines.
- Scope: six production and six test projects, SDK pin, analyzer/build policy and architecture guardrails. Desktop is a WPF library until FE-02/BE-05 supplies its production entry point.
- Changed files / test evidence / manual checklist: [BE-01 evidence](evidence/sprint-1/BE-01.md); locked restore, Release build (0 warnings/errors), 73 architecture tests, format, negative-reference rejection and three spike regression builds passed. Production/spike lockfile isolation and baseline preservation verified. Interactive checks NOT_RUN; no production shell introduced.
- Environment: Windows x64 build 26300; local SDK 10.0.402. System SDK 9.0.306 is insufficient. No `.git` metadata, so branch/working-tree status unavailable.
- Blockers: none for BE-01; Sprint 1 shell/mixed-monitor evidence remains open under ADR-0005. Only BE-01 is DONE; all other FE/BE task rows remain TODO.
- Interfaces/settings schema/ADR changes: none. No feature contract or discovery implementation promoted.
- Next authorized action: WAIT for user approval or assigned task. BE-02 is the next backend backlog item, not started.

## Historical Sprint 0 closure snapshot

The following records the state at closure, before the subsequent BE-01 assignment.

- Updated: 2026-10-27, Asia/Jakarta (raw technical evidence retains original timestamps)
- Current sprint: Sprint 0 — Discovery & Technical Spike — CLOSED; no next sprint active
- Authorization: user accepted ADR-0001–0005 and explicitly approved Sprint 0 closure with current limitations on 2026-10-27; [closure approval](evidence/sprint-0/closure-20261027/APPROVAL.md). No Sprint 1/BE-01 approval.
- Task ID: none (Sprint 0 checklist, no invented FE/BE ID)
- Owner / integrator: Codex, one agent, serialized edits
- Current discovery increment: DONE for the amended discovery scope (IN_PROGRESS → IN_REVIEW → DONE); all three spike projects have observed Windows builds. Deferred checks remain NOT_RUN/TIMEOUT.
- Sprint gate: READY_FOR_USER_APPROVAL → user approved → CLOSED with accepted limitations under [ADR-0005](ADR/ADR-0005-sprint-0-validation-deferral.md). Later acceptance obligations remain open; this is not a full compatibility/fidelity or release PASS.
- Repository: no `.git` metadata; branch/working-tree state unavailable, not claimed clean
- Backlog: all FE-01–26 and BE-01–30 remain TODO; BE-01 now consistently Sprint 1
- Evidence: [results and command index](SPIKE_RESULTS.md), [environment](evidence/sprint-0/environment.json), [successful run](evidence/sprint-0/20261018T181705Z/commands.jsonl)

| Checklist (no task IDs) | Status / evidence | Dependencies / blockers |
|---|---|---|
| Read five canonical documents / reconcile two conflicts | DONE for documentation subcheck; [approved clarification + sections](evidence/sprint-0/DOCUMENTATION_REVIEW.md); [11 consistency checks](evidence/sprint-0/documentation-consistency.log) | No architecture/scope change; explicit user approval recorded |
| Verify .NET 10 toolchain and build prototype | Evidence collected; two Release builds, zero reported warnings/errors | Portable SDK verified; system default remains 9; restricted sandbox cannot discover Windows SDK paths |
| Transparent/topmost/nonactivating WPF + tray | Scoped runtime evidence recorded; [runtime smoke/render](evidence/sprint-0/20261026T195334Z/window-smoke.jsonl); missing interactions deferred by ADR-0005 | Actual activation/hit testing/tray/z-order NOT_RUN; FE-02/05/06, BE-05/08 acceptance and Sprint 1 shell smoke still require actual proof |
| Monitor/work area/per-monitor DPI | One 120-DPI monitor enumerated, WPF matches; unavailable configurations deferred by ADR-0005 | Mixed-DPI/hotplug/100–200%/work-area NOT_RUN: FE-05/BE-08 before Sprint 1 acceptance; full matrix/fullscreen/RDP/sleep-resume FE-26/BE-30 before release |
| Core Audio | Read-only actual COM probe Available | Write/external notifications/removal NOT_RUN |
| Native Wi-Fi | Actual enumeration/radio query Available; disconnected, radios On | Toggle/denied/absent/airplane mode NOT_RUN; no SSID queries |
| Bluetooth | Actual WinRT radio read Available, On | Toggle permissions/absent/denied NOT_RUN |
| GSMTC | Manager/current session/capabilities read Available | Actual play/pause/seek/nonseekable/session closure NOT_RUN |
| Converter/license and PDF/DOCX fidelity | Scoped [converter run](evidence/sprint-0/continuation-20261026T195608Z/generate.jsonl); four normalized sRGB PNGs pass density/geometry/full pixel equality; DOCX rendering deferred by ADR-0005 | Word opens/counts fixture but export TIMEOUT; LibreOffice undetected. Renderer/reference proof before enabling/promising DOCX routes and relevant Sprint 6 acceptance; native redistribution and other OS/DPI/ICC open |
| Installer strategy | Technical strategy accepted in ADR-0004 on 2026-10-27: WiX SDK 7.0.0 per-user MSI, self-contained win-x64 multi-file | Tooling terms/eligibility remain pending; full release tests remain BE-29/30 work |
| Technical ADRs and discovery closure | ADR-0001–0005 Accepted by user, 2026-10-27; discovery closure explicitly approved | ADR-0005 changes evidence timing only; no automatic later-task, EULA, redistribution or Sprint 1 approval |
| Windows 10/11 target matrix | NOT_RUN; deferred by accepted ADR-0005 | Current build 26300 host only; FE-26/BE-30 proof before Sprint 7 release; no Win10 22H2/stable Win11 claim |

Historical increments: initial root clarifications, Desktop/Windows prototypes/build setup, then the disposable converter harness, reference script and discovery evidence. Their source changes/builds and failures retain their original report scope; see CONVERTER_DISCOVERY and TASK_REPORT_SPRINT_0. No numbered task was implemented.

Build/test evidence: existing Windows/Desktop and converter Release builds exit 0 with zero warnings/errors; actual read probes, WPF instrumentation and scoped synthetic converter assertions pass. Prior package-signature/advisory snapshots retain their dates. Word reference TIMEOUT at export after 60 seconds, owned process cleaned; DOCX layout/fidelity unverified. Unit/solution/UI suites and target manual checks NOT_RUN. No mock used as native integration proof. No build/API/converter rerun for this docs-only closure.

Decision references: approved documentation clarification (no ADR required); ADR-0001–0004 Accepted by user on 2026-10-27; [approval record](evidence/sprint-0/adr-acceptance-20261027/APPROVAL.md). Contract naming/result/port strategy is accepted, while exact production signatures remain subject to BE-04/BE-24 review. Risks and limitations remain in RISK_REGISTER.

Remaining converter risks: native DLL redistribution/component applicability, cross-DPI/OS geometry/ICC and DOCX renderer comparison. PNG metadata normalization is demonstrated for four synthetic sRGB outputs at the observed host scale; production acceptance remains unverified. Full hostile/cancellation/transaction tests belong to Sprint 6/7.

Approval-recording action (2026-10-27): documentation only; ADR metadata/index, canonical decision references and live tracking synchronized. No source/package/task status or historical test evidence changed. Build/native/manual/converter tests NOT_RUN for this action.

Completion attempt (2026-10-27): repeated Windows/Desktop restores, Release builds, native/WPF runtime probes pass; new converter build/generator pass. Word reference TIMEOUT at export, exact owned process cleaned, source unchanged; interactive UI and missing target environments remain NOT_RUN. Evidence/commands: [current results](SPIKE_RESULTS.md#completion-attempt--2026-10-27). Changed source: PdfExperiments.cs; scripts: phase instrumentation, new bounded continuation runner; docs and new evidence. No dependency/public contract/task ID changes or files deleted.

Closure action (2026-10-27): user explicitly accepted ADR-0005 and instructed closing Sprint 0 under the current limitations. AGENTS §4 and PRD §9 now record the approved evidence deadlines. Documentation/status and evidence-checker state synchronized; production source, packages, architecture, backlogs and historical raw evidence preserved. [Approval/checks](evidence/sprint-0/closure-20261027/APPROVAL.md).

Next authorized action: WAIT for a separately assigned task or explicit Sprint 1 authorization. No repeat approval of ADR-0005 or Sprint 0 closure is needed. BE-01 and all other numbered tasks remain TODO.
