# LUNAR — PRODUCT REQUIREMENTS DOCUMENT
Version: 1.0 | Date: 2026-10-19 | Status: Implementation-ready baseline

## 1. Executive summary
Lunar is a Windows 10/11 x64 floating Dynamic Island utility with an animated eye idle avatar, three horizontally swipeable pages (System Controls, Media, File Converter), and offline-first behavior. Primary aim: fast, polished, accessible system interactions without replacing Windows Settings or promising unsupported OS capabilities.

### 1.1 Product goals
- Characterful idle eye: blink, gaze, hover and click reactions, reduced-motion support.
- Reliable volume, clock, Wi-Fi/Bluetooth status and appropriate actions.
- Discover and control supported Windows media sessions with seeking where supported.
- Local file conversions for PNG, JPG/JPEG, DOCX, PDF with honest capability and fidelity indicators.
- Stable, resource-efficient tray application, installer, logs and recovery.

### 1.2 Non-goals for v1
No voice assistant, cloud account, always-on-top fullscreen overlay, DRM bypass, OCR, AI document reconstruction, browser extension, automatic updates without opt-in, or unrestricted arbitrary Bluetooth pairing.

### 1.3 Audience
Windows laptop/desktop users who want a low-distraction, elegant quick-access widget. English and Indonesian UI planned; v1 English baseline with localization-ready strings.

## 2. Platform and architecture
**Recommended:** C# / .NET 10 LTS (verify installed SDK and target availability), WPF XAML, CommunityToolkit.Mvvm, Microsoft.Extensions.DependencyInjection, Serilog, xUnit. WPF selected for native window control, transparent borderless windows, system integration and lower deployment complexity than an Electron stack. WinUI 3 may be reassessed in a spike if compositor effects outweigh window constraints.

Solution:
```
Lunar.sln
src/
  Lunar.Desktop/              # WPF shell, views, animation, tray, resources
  Lunar.Application/          # use cases, DTOs, service contracts, state
  Lunar.Domain/               # immutable entities, conversion rules
  Lunar.Windows/              # Windows APIs, capability probes
  Lunar.Conversion/           # file converters, sandbox/temp lifecycle
  Lunar.Infrastructure/       # persistence, logging, settings
 tests/
  Lunar.UnitTests/
  Lunar.IntegrationTests/
  Lunar.UITests/
 packaging/                    # installer and manifest
 docs/                         # architecture, API, decisions, test matrix
```
Dependencies point inward. Views -> ViewModels -> application services/interfaces; implementations injected at composition root. No OS P/Invoke in ViewModels. CancellationToken for asynchronous operations; never block UI thread.

### 2.1 Interfaces
```csharp
public interface IVolumeService { Task<VolumeState> GetAsync(CancellationToken ct); Task SetVolumeAsync(float scalar, CancellationToken ct); Task SetMuteAsync(bool muted, CancellationToken ct); IObservable<VolumeState> Changes { get; } }
public interface INetworkService { Task<WifiState> GetWifiAsync(CancellationToken ct); Task<OperationResult> SetWifiEnabledAsync(bool enabled, CancellationToken ct); Task OpenWifiSettingsAsync(); }
public interface IBluetoothService { Task<BluetoothState> GetAsync(CancellationToken ct); Task<OperationResult> SetEnabledAsync(bool enabled, CancellationToken ct); Task OpenSettingsAsync(); }
public interface IMediaService { Task<IReadOnlyList<MediaSession>> GetSessionsAsync(CancellationToken ct); Task<OperationResult> PlayPauseAsync(string id, CancellationToken ct); Task<OperationResult> SeekAsync(string id, TimeSpan position, CancellationToken ct); IObservable<MediaSnapshot> Changes { get; } }
public interface IConversionService { Task<ConversionPlan> PlanAsync(IReadOnlyList<string> files, OutputFormat output, ConversionOptions options, CancellationToken ct); Task<ConversionResult> ExecuteAsync(ConversionPlan plan, IProgress<ConversionProgress> progress, CancellationToken ct); }
```
All operations return structured failures: unsupported, access denied, device absent, transient, invalid input, cancelled, dependency unavailable. Contracts may be refined with ADRs.

Accepted decision update (2026-10-27): [ADR-0002](docs/ADR/ADR-0002-windows-capabilities.md) selects `IWifiService`, `MediaSessionSnapshot`, typed command results and separate conversion planner/executor ports. The snippets above are illustrative legacy examples, not a final production API or permission to duplicate contracts. Exact signatures, read/cancellation/subscription semantics still require BE-04 review before implementation; track them in `docs/API_CONTRACTS.md`.

### 2.2 Windows integrations
- Volume: Core Audio endpoint volume interfaces and device-change notifications; range 0–100, mute, feedback loop guard, endpoint unavailable fallback.
- Wi-Fi: Native Wi-Fi API / supported Windows network interfaces for status; radio on/off only when OS permissions/hardware permit. Never claim connected if merely enabled. Offer `ms-settings:network-wifi` fallback; network lists/connection not required v1.
- Bluetooth: supported Windows radio/device APIs with capability detection; radio toggling may be unavailable or restricted. Distinguish enabled, disabled, unavailable, unknown; fallback `ms-settings:bluetooth`.
- Date/time: local system clock/timezone; read-only display; opening Windows Date & Time Settings via `ms-settings:dateandtime`. Do not silently change system time.
- Media: GlobalSystemMediaTransportControlsSessionManager (GSMTC); enumerate sessions, choose OS current session by default, allow manual session switch; timeline and playback controls only if session reports support. Not all apps provide metadata, album art, timeline, or seek. Handle live streams, advertisements, duration=0, stale position, session closure, and source switching.
- Foreground: topmost borderless non-activating where appropriate; click-through only outside hit-test surface; DPI-aware and multi-monitor aware; hide/reposition on fullscreen exclusive apps, taskbar relocation and display changes.

## 3. UX specification
### 3.1 Window states
- `Idle`: default 160x44 DIP pill (configurable), two animated eyes; no large controls.
- `Hover`: subtle glow/eye attention, no auto expansion by default.
- `Expanded`: default 380x320 DIP (content-adaptive; no clipping), three pages; click idle toggles.
- `Busy`: expanded converter progress; may collapse with background conversion ongoing.
- `Notification`: transient compact status, auto-dismiss, no spam.
- `Error`: inline recoverable state, accessible details.
State transitions must be deterministic, cancel overlapping animations, persist page selection only if configured. Auto-collapse on outside click after 300ms debounce except when dialog is open, drag/drop active, accessibility focus inside, or conversion requires confirmation. Escape collapses. Tray menu supports Open, Pause Animations, Launch at Startup, Settings, Exit.

### 3.2 Positioning
Default top-center of primary monitor work area; configurable top-left/top-right, offsets, primary vs selected monitor. Honor taskbar/work area and DPI. Avoid covering fullscreen games/video by default. Clamp to visible bounds after resolution or scaling changes. No permanent invisible topmost window.

### 3.3 Eye animation
Two eyes built with WPF vector shapes (not GIF): natural stochastic blinking every approximately 3–7 seconds, occasional double blink, eyelid closing/opening 120–220ms, gentle gaze drift; pointer tracking only within proximity and capped displacement. Idle at 60Hz maximum; reduce updates when hidden/minimized or battery saver active. States: neutral, curious, blink, sleepy (optional), surprised on click. `Reduce motion` disables continuous gaze and uses simple opacity/state changes. No webcam or face tracking.

### 3.4 Navigation
Three horizontally swipeable pages, visible dots and accessible tabs/buttons. Mouse drag, precision touchpad gesture where recognized, left/right arrow keys when focus in page navigator; mouse wheel must not steal volume slider/scroll interactions. Swipe threshold ~55 DIP or 25% page width, velocity option; rubber-band edges, snap 200–300ms. Focus must be maintained logically across transitions; no accidental swipes while dragging seek or conversion controls.

### 3.5 Visual tokens
Background #0B0C10; surface #181A22; primary text #F6F7FB; secondary #A8ADBE; accent #B6B2FF; error #FF737E; success #86D7B0. Rounded pill radius 999 DIP, expanded radius 24 DIP. Spacing 4/8/12/16/24; typography Segoe UI Variable fallback Segoe UI. Maintain >=4.5:1 text contrast (except large text per WCAG), visible keyboard focus, high contrast compatibility, screen reader names.

## 4. Page requirements and acceptance criteria
### 4.1 Page 1 — System
FR-SYS-01 Show local date, time, timezone-aware updates (minute or optional seconds) without unnecessary redraw.
FR-SYS-02 Show current default audio endpoint volume, mute status, set scalar 0–100, immediate optimistic UI with authoritative rollback on failure; external changes update slider.
FR-SYS-03 Show Wi-Fi status: connected/available/off/unavailable/unknown, SSID only when available and authorized; toggle when supported, else explain and open Settings.
FR-SYS-04 Show Bluetooth radio status and toggle only if supported; otherwise Settings deep-link; paired-device list is post-v1.
FR-SYS-05 Errors displayed inline and logged without sensitive network metadata.
AC: External keyboard volume change reflected; disabled adapter doesn't crash; airplane mode correctly represented; time updates across midnight; no elevation requested by default.

### 4.2 Page 2 — Media
FR-MED-01 Detect supported sessions, title, artist, optional album art, playback state, app name.
FR-MED-02 Play/pause, previous, next only when advertised by session.
FR-MED-03 Timeline current/duration `mm:ss` or `hh:mm:ss`; seek slider is enabled only for seekable finite timeline; debounce drag and commit on pointer release; prevent polling overwriting thumb during drag; clamp target to valid start/end.
FR-MED-04 Show unavailable, no session, metadata absent, live stream, unsupported seek states.
FR-MED-05 User can choose among multiple sessions; preserve selection until closed, then fallback to current OS session.
AC: Play Spotify/Windows Media Player/browser supported session; dragging seeks within 1s where OS/app supports it; nonseekable stream slider disabled; media session disappears without crash.

### 4.3 Page 3 — Converter
FR-CON-01 Select files through picker or drag-and-drop, validate type using extension AND file signatures/content where practical, size, page count and path.
FR-CON-02 Select output format and show compatibility, mode, expected fidelity, optional dependencies.
FR-CON-03 Batch processing queue with progress per file, cancel, retry, open output folder, choose output location, conflict resolution (rename/overwrite/skip).
FR-CON-04 All conversion is local; temporary files stored in isolated per-job folders and cleaned on completion/cancel/startup recovery.
FR-CON-05 Protect original inputs, never overwrite without explicit confirmation, avoid path traversal and zip bombs in DOCX, impose configurable size/page/pixel limits, avoid running macros or arbitrary embedded content.
FR-CON-06 Optional LibreOffice headless installation detected with user consent; no bundled binary without license compliance review.
AC: Tests for valid/corrupt files, transparent PNG, 100-page PDF, cancellation, long filename, Unicode filename, disk full, password-protected PDF, missing LibreOffice, conversion in progress when UI collapses.

## 5. Conversion matrix (ordered source -> target)
Codes: N = native direct; R = rendered/embedded (not semantically editable); D = dependency required; B = best-effort reconstruction; S = same format/no-op or rename; X = not included in v1. PDF -> DOCX best effort is a stretch goal, not a release blocker.

| Input | PNG | JPG | JPEG | DOCX | PDF |
|---|---|---|---|---|---|
| PNG | S | N | N | R (embed) | R (image page) |
| JPG | N | S | S | R (embed) | R (image page) |
| JPEG | N | S | S | R (embed) | R (image page) |
| DOCX | D (page images) | D (page images) | D (page images) | S | D (layout engine) |
| PDF | R (per page) | R (per page) | R (per page) | B (stretch) | S |

Implementation notes:
- JPG and JPEG are one encoding family; same-encoding output may copy/rename without re-encoding.
- PNG -> JPEG composites alpha against selectable background (default white); preserve orientation/ICC metadata where feasible, disclose lossy conversion.
- PDF -> image yields `name_page_001.png` etc; allow DPI (default 150; 72/150/300), page selection, pixel limits.
- Images -> PDF: page size auto/A4/letter, margin, fit/cover, one page per image; image -> DOCX creates document containing image, not OCR text.
- DOCX -> PDF uses installed LibreOffice headless as optional provider, isolated user profile and timeout; render PDF pages to images afterward. Fonts and layout may differ from Word.
- PDF -> DOCX: if included after v1, use a vetted third-party local converter with redistribution/license review or clearly labeled text/image reconstruction. No false promise of faithful editable layout.
- Encrypted PDF prompts for password in memory only; never persist password or log it. For PDFs with disallowed operations, follow library capabilities and legal restrictions.
- Conversion progress: determinate by page/job where possible, otherwise indeterminate; do not fabricate percentages.

## 6. Settings and persistence
Store under `%LocalAppData%/Lunar/` (user-specific); settings.json schema-versioned, atomic write + backup. Settings: startup, topmost behavior, monitor, offset, theme/accent, idle eye animation, reduced motion, auto-collapse, preferred media session, default conversion output folder, privacy/logging, locale. No admin privileges for routine use. Avoid storing Wi-Fi passwords, file contents, music metadata histories. Sensitive tokens are not needed.

## 7. Nonfunctional requirements
- Performance budgets (release target, measured on baseline Windows 11 x64 16GB laptop): idle CPU <1% averaged 5min; idle RAM <150MB aspirational; open latency <150ms before animation; animation 60fps target; no busy-loop polling.
- Reliability: 99.5% crash-free test sessions, recover from missing devices and disconnected adapters, no conversion file corruption on cancel.
- Security: local conversion, no telemetry by default, sanitize file names, limit resources, do not execute DOCX macros or shell command injection, validate LibreOffice executable path, protect temp files, no unnecessary elevation.
- Accessibility: tab order, focus ring, narrator labels, high contrast, keyboard control, reduce motion, 200% DPI, localized time formats.
- Compatibility: Windows 10 22H2 / Windows 11 x64; multi-monitor, DPI 100–200%, dark/light OS themes, sleep/resume, remote desktop sessions.
- Privacy: clear privacy policy, diagnostics opt-in if ever implemented; no file upload.
- Packaging: signed installer if signing certificate available, per-user install option, uninstall removes executable but asks before deleting settings; Windows startup opt-in.

## 8. Risks and mitigations
1. Wi-Fi/Bluetooth toggle restricted by OS or driver: capability probe, honest disabled UI and Settings fallback.
2. GSMTC seek missing for some apps: capability-based disabled seek; no simulation by keyboard shortcuts.
3. DOCX rendering fidelity varies: optional LibreOffice dependency, warn about font/layout changes, golden file tests.
4. PDF -> DOCX poor fidelity: stretch only; mark best-effort and provide preview/limitations.
5. WPF transparent topmost focus/graphics edge cases: windowing spike, DPI tests, full-screen detection.
6. Third-party library license obligations: record versions, licenses, attribution, redistributability before release.
7. Memory/CPU cost of PDF rasterization: streaming pages, caps, cancellation and worker isolation.
8. Windows Defender false positives on unsigned installer: standard packaging, signing and reproducible builds.

## 9. Delivery timeline (8 sprints, indicative 8–10 weeks)
Every sprint has deliverables, test gates, and an agent prompt. The AI agent must not mark tasks complete without actual build/test evidence.

### Sprint 0 — Discovery and technical spike (3–5 days)
Deliverables: architecture ADRs, Windows API feasibility matrix, conversion dependency/license review, window prototype, risk register, build setup. Exit: app starts as transparent borderless topmost window on the available Windows host, volume/media API probe succeeds or gracefully reports unsupported; actual Windows build and scoped runtime evidence are recorded. The full Windows 10/11 compatibility target remains unchanged and requires actual target evidence before release.

Approved evidence timing, 2026-10-27: [ADR-0005](docs/ADR/ADR-0005-sprint-0-validation-deferral.md) closes discovery with the current host limitations and these explicit deferred obligations:

- Actual mouse nonactivation, outside hit testing, tray Open/Exit and keyboard focus: FE-02/05/06 and BE-05/08 before relevant task acceptance; Sprint 1 shell smoke gate remains mandatory.
- Mixed monitors, DPI 100/150/200%, work-area changes/hotplug: FE-05/BE-08 before relevant task and Sprint 1 acceptance; complete regression in FE-26/BE-30.
- Windows 10 22H2/stable supported Windows 11 matrix and fullscreen/sleep/RDP: FE-26/BE-30 before Sprint 7 release approval; earlier feature gates retain available-Windows integration/manual requirements.
- DOCX layout/reference and LibreOffice-versus-reference fidelity: follow-up discovery before enabling/promising DOCX rendering in BE-25/BE-27/FE-20–22; required for relevant Sprint 6 acceptance. Missing dependencies remain explicit DependencyUnavailable; Word is reference-only.

The user approved ADR-0005 and Sprint 0 closure in [the approval record](docs/evidence/sprint-0/closure-20261027/APPROVAL.md). NOT_RUN/TIMEOUT results remain unverified; affected later tasks may still be blocked. PRD §7/13/16, later sprint gates, security/privacy, native redistribution and WiX tooling conditions remain in force. No production feature/task or Sprint 1 start is authorized by this closure.

**Agent prompt:**
> Act as a senior Windows desktop architect. Keep root `ARCHITECTURE.md` as the sole canonical architecture document; link to it from `docs/README.md` using `../ARCHITECTURE.md` instead of creating a duplicate. Create `docs/ADR/`, `docs/COMPATIBILITY.md`, `docs/CONVERSION_MATRIX.md`, `docs/SECURITY.md` and a minimal temporary .NET 10 WPF prototype for Lunar. Confirm SDK/OS/API availability rather than guessing. Spike transparent non-activating floating window, per-monitor DPI, tray, Core Audio, GSMTC, Wi-Fi and Bluetooth capability probes. Evaluate image/PDF/DOCX conversion libraries and redistribution licenses. Implement DI, structured logging and safe error handling. Do not build product features yet. Record commands and actual outputs in `docs/SPIKE_RESULTS.md`; list unsupported APIs and fallbacks. Do not claim success without running a Windows build.

Sprint 0 is discovery, feasibility validation, technical spikes and temporary prototypes only. Official numbered implementation starts in Sprint 1, including BE-01, which remains TODO during Sprint 0. Prototype code may be reused only after review, refactoring where needed, and verification against BE-01 acceptance criteria; prototype evidence does not automatically complete BE-01.

### Sprint 1 — Project skeleton, shell and tray (1 week)
Deliverables: solution structure, MVVM, DI, settings, tray icon, shell states, positioning and monitor handling. Exit: clean Release build, no duplicated window, tray Exit works, settings persist.

**Agent prompt:**
> Implement Lunar solution with `Desktop`, `Application`, `Domain`, `Windows`, `Conversion`, `Infrastructure` and tests. Build a borderless rounded transparent WPF window pinned to configurable top-center work area. Add Idle/Expanded/Busy/Notification/Error state machine, tray Open/Settings/Exit, launch-at-startup opt-in, per-monitor DPI support, taskbar-aware positioning, and atomic versioned JSON settings. Implement ViewModels with CommunityToolkit.Mvvm and DI; no OS calls in XAML code-behind except view concerns. Write unit tests for state transitions and settings migration. Build Release, fix warnings, provide file list, test results and manual validation checklist.

### Sprint 2 — Eye character and animation (1 week)
Deliverables: two vector eyes, randomized blink, gaze response, reduced-motion mode, interaction animation. Exit: no frozen eyelids, no continuous high-CPU timer when hidden, click opens panel.

**Agent prompt:**
> Implement Lunar idle character using WPF vector primitives. Create two eyes with pupil highlights, random blink interval approximately 3–7s, occasional double blink, eyelid timing 120–220ms, subtle gaze drift, pointer proximity tracking with capped pupil displacement, hover curious expression, click surprise transition and smooth expansion. Keep deterministic animation state separate from rendering; inject time/random sources for tests. Honor Windows reduced-motion and Lunar pause-animation settings; stop or throttle timers when hidden or battery saver is active. Avoid GIF assets, unnecessary frame polling and memory leaks. Add tests for animation transitions and visual manual test instructions.

### Sprint 3 — Expansion, swiper and visual system (1 week)
Deliverables: 3 page container, swipe and keyboard navigation, design tokens, adaptive panel, focus management. Exit: no accidental swipe on sliders, no clipped pages at 200% scaling.

**Agent prompt:**
> Build the expanded Lunar island with 3 horizontally swipeable pages and animated pagination indicators. Add pointer drag, touch gesture where supported, arrow-key navigation, accessible page tabs, Escape-to-collapse and click-outside behavior. Use threshold ~55 DIP and velocity-aware snap; prevent navigation while dragging sliders, selecting text or dropping files. Define theme resources for colors, typography, spacing and radius; support high contrast and reduced motion. Keep page ViewModels alive without leaking event handlers. Implement UI tests or documented manual steps for mouse, keyboard, DPI, monitor changes and focus.

### Sprint 4 — System Control page (1–1.5 weeks)
Deliverables: volume, mute, local clock, Wi-Fi/Bluetooth status, capability-gated toggles, Settings fallbacks. Exit: OS external changes reflected; unavailable devices handled.

**Agent prompt:**
> Implement System Control page using interface-based Windows services. Wire Core Audio default endpoint volume/mute and external change notifications. Add local date/time with correct locale and midnight rollover. Query Wi-Fi connected/disabled/unavailable status and SSID only if allowed; implement radio toggle only if supported and return structured unsupported result otherwise. Implement Bluetooth state similarly; provide deep-links to Windows Settings for restricted operations. Never request admin privileges for routine actions. Avoid false connected labels, prevent slider feedback loops, handle device change, airplane mode, sleep/resume and missing adapters. Mock services in unit tests and document hardware-dependent manual cases.

### Sprint 5 — Media page (1–1.5 weeks)
Deliverables: session discovery, album art, controls, seek timeline, unsupported states, session picker. Exit: supported seek works, live/nonseekable streams cannot seek, session closure stable.

**Agent prompt:**
> Integrate Windows GSMTC through IMediaService. Enumerate sessions, select OS current session by default, allow manual session selection and recover when selected session closes. Show title, artist, source app, artwork, playback state, elapsed and duration. Add play/pause, previous/next and seek only when reported supported. Implement seek slider with drag-lock: don't overwrite thumb from incoming timeline while dragging, clamp to valid bounds, seek once on release, show a recoverable error if rejected. Disable seek for live streams, missing duration or unsupported controls. Handle artwork loading asynchronously, cancellation, stale metadata and session change. Test mocked timelines, zero duration, disappearing sessions, and manual app compatibility.

### Sprint 6 — Converter engine and UI (2 weeks)
Deliverables: format matrix, converters, drag/drop, queue, progress, cancel, safe output, optional dependency detection. Exit: supported matrix passes golden-file tests; original input never modified.

**Agent prompt:**
> Implement a local-only, cancellable conversion pipeline for PNG/JPG/JPEG/DOCX/PDF. First create a typed conversion graph with modes native, rasterize, embed, external-renderer, best-effort and unsupported. Use a license-reviewed image library for image transformations, PDF library for image-to-PDF and PDF page rasterization, Open XML SDK for image-to-DOCX, and optional detected LibreOffice headless for DOCX-to-PDF then page images. Do not promise editable OCR for image-to-DOCX. Treat JPG/JPEG as same encoding. Mark PDF-to-DOCX as unsupported in v1 unless a separately reviewed best-effort implementation is explicitly approved. Build file picker, drag/drop, output chooser, quality/DPI/page settings, queue, progress, cancellation, conflict handling and open-folder. Validate file signatures and zip/XML resource limits, never overwrite input, use per-job temp folders and cleanup. Test corrupt/encrypted files, Unicode paths, 100-page PDF, missing renderer, disk-full, cancellation, alpha blending and output integrity. Show per-conversion fidelity warnings and never invent progress percentages.

### Sprint 7 — QA, optimization, installer and release (1–1.5 weeks)
Deliverables: release builds, test report, accessibility review, privacy notice, installer, user guide, changelog. Exit: all P0/P1 bugs fixed, supported Windows acceptance tests pass, conversion originals intact.

**Agent prompt:**
> Perform a production hardening pass on Lunar. Run unit, integration and manual acceptance suites on Windows 10 22H2 and Windows 11 x64 where machines are available; clearly mark unavailable environments as untested. Profile 5-minute idle CPU/RAM, animation smoothness, expand latency, conversion memory and cancellation. Audit keyboard/Narrator/high-contrast/reduced-motion, DPI 100/150/200%, multi-monitor, taskbar placement, fullscreen apps, sleep/resume and hardware absent states. Fix P0/P1 defects. Build per-user installer, optional startup setting, uninstall flow, version metadata, third-party notices and privacy policy; sign binaries only if certificate is available. Produce `docs/TEST_REPORT.md`, `docs/RELEASE_CHECKLIST.md`, `CHANGELOG.md`, `README.md` and reproducible build instructions. Never claim a test passed without logs or a manual result.

## 10. Backlog and priority
P0: stable shell/tray; eye click/expand; volume/clock; media playback with capability-aware seek; image<->image, image->PDF, PDF->image; safe conversion and no input overwrite; installer.
P1: Wi-Fi/Bluetooth status/fallback; supported toggles; image->DOCX; optional LibreOffice DOCX->PDF/images; batch jobs; accessibility; settings.
P2: PDF->DOCX best-effort, advanced device pairing, extra themes, advanced eye emotions, auto-update.

## 11. Definition of done
Feature is done only when: acceptance criteria satisfied; no P0/P1 known bug; unit/integration tests exist for relevant logic; no secrets or user document contents in logs; errors/cancel states tested; accessible keyboard path; Windows Release build verified; documentation and changelog updated; limitations explicitly documented. Hardware-only features must have manual test evidence or marked unverified.

## 12. Test matrix
| Area | Positive | Negative / boundary |
|---|---|---|
| Shell | click expands, Escape closes, tray | fullscreen, monitor unplug, taskbar move |
| Eye | blink, gaze, click reaction | reduced motion, sleep, high CPU |
| Swipe | mouse drag, tabs, keyboard | seek drag, text selection, edge swipe |
| Volume | adjust, mute, external change | device removed, no endpoint |
| Network | connected status, settings | no Wi-Fi, airplane mode, restricted toggle |
| Bluetooth | on/off status, settings | missing adapter, denied toggle |
| Media | seekable local file, pause | live stream, zero duration, session lost |
| Converter | each supported format pair | corrupt, encrypted, huge, disk full, cancel |
| Packaging | install, startup opt-in, uninstall | no admin, Defender prompt, update settings |

## 13. Release acceptance scenario
1. Install on Windows 11 x64 standard user, launch and observe idle eyes.
2. Click eye, swipe pages via pointer and keyboard, Escape collapse.
3. Change Windows volume outside Lunar and confirm slider sync; set mute in Lunar.
4. Observe Wi-Fi/Bluetooth state and try supported action/fallback.
5. Play seekable media, inspect metadata, drag seek and verify actual playback time.
6. Play live/nonseekable stream and verify slider disabled.
7. Convert PNG->JPEG, JPEG->PDF, PDF->PNG, PNG->DOCX and DOCX->PDF if dependency installed; inspect outputs.
8. Cancel a multi-page conversion, confirm originals intact and temp cleanup.
9. Switch DPI/monitor, sleep/resume, relaunch; verify settings persist.
10. Exit tray, verify process exits and no background workers remain.

## 14. Suggested documentation inventory
`README.md`, `PRD.md`, `SRS.md`, `ARCHITECTURE.md`, `docs/ADR/*.md`, `docs/UX_SPEC.md`, `docs/DESIGN_TOKENS.md`, `docs/API_CONTRACTS.md`, `docs/CONVERSION_MATRIX.md`, `docs/SECURITY.md`, `docs/PRIVACY.md`, `docs/COMPATIBILITY.md`, `docs/TEST_PLAN.md`, `docs/TEST_REPORT.md`, `docs/RELEASE_CHECKLIST.md`, `docs/USER_GUIDE.md`, `THIRD_PARTY_NOTICES.md`, `CHANGELOG.md`.

## 15. Global prompt for coding agent
> You are the principal Windows desktop engineer for Lunar. Read `PRD.md` and all existing ADRs before changes. Implement only the requested sprint, preserve architecture boundaries, and prefer Windows-native documented APIs over guessed commands. Use C#/.NET 10/WPF/MVVM and DI, safe async cancellation, logging without personal file data, and local-only processing. Confirm compatibility and third-party license before introducing dependencies. For each change: provide a concise plan, file list, code, tests, exact build/test commands with outputs, manual verification instructions, known limitations, and documentation updates. Do not fabricate passing tests, OS permissions, supported seek operations or conversion fidelity. If working outside Windows, write code and tests but clearly mark Windows-only build/integration tests unverified. Do not silently expand scope beyond the active sprint. Treat security, accessibility and original-file preservation as release blockers.

## 16. Delivery governance and agent orchestration (v1.1)

The repository root contains five normative documents: `PRD.md` (product requirements and sprint gates), `ARCHITECTURE.md` (project/layer ownership and architecture), `AGENTS.md` (mandatory agent execution protocol), `FE_TASK.md` (26 frontend task IDs), and `BE_TASK.md` (30 backend task IDs). All agents must read `AGENTS.md` before making changes. Task prompts supplement these documents; they never override product scope or architectural safety constraints.

**Sprint 0:** discovery and technical spikes are a mandatory prerequisite to Sprint 1, although the numbered FE and BE task backlogs begin with Sprint 1. Record Sprint 0 findings in `docs/SPRINT_STATUS.md` and relevant ADRs; do not invent FE-00 or BE-00. Sprint 1–7 implementation is tracked by the existing 56 immutable task IDs. One agent may own one task at a time. Parallel FE/BE work is permitted only when interfaces are agreed, ownership does not overlap, and integration gates are satisfied.

**Sprint gate:** complete all mandatory tasks assigned to that sprint, demonstrate their acceptance criteria, record executed build/test/manual Windows evidence and unresolved blockers, and obtain explicit human approval before advancing. No Windows-only behavior is considered verified on a non-Windows runner. A task can be `DONE` only after evidence; otherwise `IN_REVIEW` or `BLOCKED`. Update `docs/SPRINT_STATUS.md` after each task and at each gate; the FE/BE backlog status tables remain synchronized.

**Source-of-truth precedence:** security/privacy and architecture invariants in `ARCHITECTURE.md` must not be bypassed; `PRD.md` defines scope and behavior; `ARCHITECTURE.md` defines ownership and dependency direction; `AGENTS.md` defines workflow; task files define executable work units. If they conflict, stop the affected work, report the conflict, and propose a documentation change/ADR rather than silently choosing. Maintain a task-to-requirement traceability entry for affected features.

**Conversion release policy:** implement only conversion routes marked supported by the canonical conversion matrix. PDF→DOCX reconstruction is not a mandatory v1 release gate; unsupported/experimental routes must be visibly disabled or clearly labeled and must never be advertised as lossless. A fallback to an external dependency must be disclosed, explicitly checked for installation, and never downloaded or executed silently.

**Change control:** any change to project boundaries, cross-layer interfaces, security, persistence schema, packaging, or conversion semantics needs a documented impact analysis and, when consequential, an accepted ADR. No agent may self-approve an ADR or advance a sprint. Preserve user files and avoid silent destructive operations.

## 17. Open decisions

Decision record, 2026-10-27: the user accepted [ADR-0001–0004](docs/ADR/README.md), resolving the listed direction choices to optional separately installed LibreOffice, x64 first, capability-gated best-effort radio controls with Settings fallbacks, and PDF→DOCX disabled in v1. Selected provider/package and per-user self-contained MSI details/conditions are in those ADRs. The bullets below retain the original questions for traceability; visual/brand details remain open. This approval changes no task ID, acceptance criterion, route scope or sprint gate and proves no additional compatibility/fidelity result.
- Whether to ship a bundled LibreOffice-compatible renderer or require a separately installed dependency (recommended: optional external install for v1).
- Whether the v1 installer targets only x64 or also ARM64 (recommended: x64 first).
- Exact eye design, window dimensions, language baseline, app icon and brand assets.
- Whether Wi-Fi/Bluetooth toggles are mandatory or best-effort (recommended: best-effort with clear fallback).
- Whether PDF->DOCX is v1 acceptance scope (recommended: post-v1 stretch due fidelity and licensing).

---
This PRD is a specification, not a claim that Windows APIs, dependencies, conversion paths, or performance targets have already been verified on a target machine.
