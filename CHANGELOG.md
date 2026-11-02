# Changelog

## Unreleased — 2026-11-03

- BE-02: PowerShell `build.ps1`, `test.ps1` and `package.ps1`, a GitHub Actions workflow on `windows-2022` and `windows-latest` (gate job plus a non-blocking Windows-session job, 14-day artifacts), a pull request template and `docs/CI.md`. The gate suite (291 tests incl. architecture checks) and a failing-test/architecture-violation check pass locally and from a clean clone. Status IN_REVIEW: the workflow has not run on GitHub and merge blocking needs branch protection. Also: UI tests now run sequentially to stop an intermittent concurrent-XAML-parse failure. Repository initialised on `main` with grouped commits.

- FE-03: `IslandLayout` sizing rules (idle 160×44; expanded at least 380×320, growing to fit content, capped to the work area minus margins), content measurement and sizing folded into `IslandPositionController`, `IslandViewModel.IsExpandedLayout`, corner radius 999/24 by layout. 437 tests pass including a 96–192 DPI matrix; real exe geometry unchanged. Status DONE by user decision: real windows checked at 125% only, expanded layout not reachable in the app before Sprint 3, so real 100/150/200% on-screen confirmation remains unverified. No package change, no new native call.

- FE-04: `IslandViewModel` (Idle, Expanding, Expanded, Collapsing, Busy; background-busy flag; inline error that never changes the state) with `RelayCommand`, transient DI registration, and the window's UI Automation ItemStatus bound to the state text. 410 tests pass; real exe exposes "Collapsed". The validated state machine remains BE-09. No package change, no new native call.

- FE-05: `IslandPositionController` places the pill top-center on the preferred (setting) or primary display's work area, follows display/taskbar/DPI changes, and falls back to a safe display; `App` now loads settings at startup; temporary placement code removed. 371 tests pass; real exe starts at the expected pixel position. Status DONE: a user-run check of four real scale/resolution changes raised the notification and the pill stayed visible; second monitor / mixed DPI and a DPI-free work-area change remain unverified gate items. No package change, no new native call.

- BE-06: ADR-0011 accepted; `LunarSettings` model with pure `SettingsRules`, `ISettingsRepository` port and `JsonSettingsRepository` in `%LocalAppData%\Lunar\settings.json` (schema v1, atomic write with `.bak`, corrupt file kept as `settings.corrupt.json`, recovery notice values, newer-version files never overwritten, migration chain). 349 tests pass; real-folder run restored a corrupt file from backup. No package change. Settings are not consumed by the app yet; no production migration exists until the first schema change.

- BE-08: ADR-0010 accepted; `IWindowEnvironment` port with `DisplayInfo`/`PixelRect` models, pure `IslandPlacement` rules, Windows `MonitorInfoProvider` (physical-pixel enumeration, hidden listener window, coalesced `DisplaysChanged`) and `WindowsSettingsLauncher`. 243 tests pass; on this single-monitor host placement matches the real window geometry. Status DONE by user decision ("Tunda ke FE-05"): real hotplug/DPI-change evidence is deferred to FE-05 and stays unverified. No package change.

- BE-07: ADR-0009 accepted; `IDiagnosticLog` port (no free-text fields), Serilog-based bounded rolling logs in `%LocalAppData%\Lunar\Logs` (normal ≤ about 7 MiB, diagnostics ≤ about 35 MiB), redaction of non-token strings, `Trace` replaced by log events, `docs/PRIVACY.md`. Packages Serilog 4.3.1 and Serilog.Sinks.File 7.0.0 (Apache-2.0). Diagnostics opt-in via `LUNAR_DIAGNOSTICS=1` until a setting exists. 187 tests pass; real-exe log scanned for personal data (none).

- BE-05: ADR-0008 accepted; `IInstanceCoordinator` port, `SingleInstanceCoordinator` (mutex plus current-user pipe) in Infrastructure, App lifecycle policy (second launch exits 0 after nudging the primary, 3 if unreachable, 1 on start failure). 161 tests pass; two-process smoke on the real exe passes; foreground on activation is best-effort. Sprint 1 gate stays open.

- FE-06: tray icon with Open and Exit working, Settings and Start with Windows shown disabled until FE-23/BE-29; `IslandWindow.OpenForKeyboard`, placeholder embedded icon, WinForms reference for `NotifyIcon` only. 147 tests pass. Status DONE: the user's manual tray check passed.

- FE-02: production `App.xaml` and `Shell/IslandWindow` (transparent pill, single view host, non-activating); `Lunar.Desktop` is now a WinExe. Discovery files moved unchanged to `src/Lunar.Desktop/Spike/`. Real-run smoke: click leaves the foreground window unchanged, closes with exit code 0. ADR-0007 (accepted) covers the one native call (`WS_EX_NOACTIVATE`). 142 tests pass. Status DONE: the high-contrast review (carried from FE-01) passed per the user's manual check; no user-facing exit until FE-06.

- FE-01: added `Lunar.Desktop/Themes` (Dark/Light/HighContrast colors, Typography, Spacing, Components) and `docs/DESIGN_TOKENS.md`, with 16 token/contrast/resource tests (139 tests pass in the solution). Status DONE by user decision: light palette approved; the on-screen high-contrast review is deferred to FE-02 and remains unverified. No package or reference change.

- BE-04: accepted ADR-0006 and added system/media ports and models to `Lunar.Application`, `ServiceRegistration` (validated DI container) in `Lunar.Desktop`, and the MIT package `Microsoft.Extensions.DependencyInjection` 10.0.13 (Desktop only). 123 tests pass. Conversion, settings and shell contracts are deferred to BE-19/20/24, BE-06 and BE-08. Sprint 1 gate remains open.

- BE-03: added `ErrorCode`, `OperationError`, `Result` and `Result<T>` to `Lunar.Domain.Shared` with 19 unit tests (92 tests pass in the solution). No new references, packages or ADR. Sprint 1 gate remains open; BE-04 not started.

## Unreleased — 2026-10-27

- Started the explicitly assigned BE-01 Sprint 1 scaffold: Lunar.sln, six production and six test projects, central test packages/lockfiles, SDK analyzer policy and architecture fitness tests. Production Desktop remains a WPF library; no discovery feature is implicitly promoted.
- Isolated production/discovery build inputs and outputs; added BE-01 verification, test-scope documentation and manual checklist. Sprint 1 gate remains open; later tasks are not started. Entries below retain earlier discovery history.

- User explicitly accepted ADR-0005 and closed Sprint 0 discovery with current limitations. Updated AGENTS §4 / PRD §9 evidence timing and tracked deferred obligations in existing Sprint 1/6/7 tasks.
- Closure action changes documentation and evidence-checker accepted-state validation only. Missing tests remain NOT_RUN/TIMEOUT; no new integration PASS, redistribution/EULA acceptance, numbered task completion or Sprint 1 authorization.
- Retained pre-approval entries below as history; their former IN_REVIEW/BLOCKED/Proposed state is superseded by the explicit closure record, not by a new test result.

- Revalidated all three temporary spike projects on Windows: Release builds without warnings/errors, native/WPF runtime probes and synthetic converter assertions pass.
- Added separate sRGB PNG density-normalization experiments with complete pixel equality and a bounded continuation/reference runner. Word opens/counts the synthetic DOCX but PDF export times out; owned process cleanup verified.
- Recorded unavailable target OS/monitor environment and unverified UI interactions; proposed ADR-0005 evidence deferral for user review. No gate waiver, closure or Sprint 1 work is authorized by the proposal.

- Recorded explicit user acceptance of ADR-0001–0004, preserving the reviewed decisions and all validation/redistribution/tooling conditions.
- Synchronized canonical decision references, live tracking and exit review. Sprint 0 remains IN_REVIEW with closure BLOCKED by missing evidence; BE-01 and all 56 numbered tasks remain TODO.
- The ADR-0001–0004 acceptance-recording action itself was documentation-only; the separate technical continuation above changes spike/scripts and adds actual evidence. No package change or EULA acceptance.

## Unreleased — 2026-10-19

- Began Sprint 0 with temporary .NET 10 WPF window/tray and read-only Windows capability probes; Release build and instrumented smoke evidence recorded.
- Clarified root architecture source of truth and BE-01 starting in Sprint 1 with explicit user approval; numbered tasks remain TODO.
- Added initial compatibility, conversion/license/security/risk review and proposed ADRs. Sprint 0 acceptance is incomplete; no release or Sprint 1 approval.
- Continued unfinished Sprint 0 only: exact converter package graph/signatures/advisory snapshot, synthetic PNG/JPEG/EXIF/PDF and DOCX-embedding experiments, visual/pixel evidence and concrete selections in four Proposed ADRs.
- Recorded PDF DIP/pixel behavior and unresolved PNG density metadata; DOCX reference timeout and owned-process cleanup preserved as failed/unverified evidence.
- Prepared Sprint 0 exit review with missing discovery evidence separated from later release tasks. Original prototypes were not rerun; all numbered tasks remain TODO.
