# Sprint 0 — discovery and technical spike results

Updated: 2026-10-27, Asia/Jakarta. Owner/integrator: Codex. Status: DONE/CLOSED for the discovery scope under user-accepted ADR-0005 and explicit closure approval. Deferred NOT_RUN/TIMEOUT evidence remains unverified. No FE/BE ID or Sprint 1 authorization. Technical attempts below retain their historical scope, failures and former gate status; the closure record is at the end.

**Historical scope:** sections below record the initial window/native increment; they are not rewritten to conceal earlier gaps. Current continuation results are in [CONVERTER_DISCOVERY](CONVERTER_DISCOVERY.md), with four revised Proposed ADRs and [exit review](SPRINT_0_EXIT_REVIEW.md). Only the new converter harness was built/run during continuation; original successful prototypes were not repeated. Statements below about conversion not yet being executed describe the initial increment.

## Scope and startup inspection

Read all five initial documents in full. Startup folder contained only AGENTS.md, PRD.md, ARCHITECTURE.md, FE_TASK.md and BE_TASK.md. `git status --short` and `git branch --show-current` each reported not a Git repository (exit 128); no branch/clean-tree claim and no Git initialization performed. `rg --files` found no solution/project/tests. System `dotnet --info` succeeded (exit 0), SDK 9.0.306, Windows x64 build 26300. Prior sprint evidence/contracts/ADRs were absent.

User approved documentation-only reconciliation of canonical architecture location and Sprint 0/BE-01 labels. Exact changed sections and consistency evidence: [documentation review](evidence/sprint-0/DOCUMENTATION_REVIEW.md). This was not approval of ADRs or progression to Sprint 1.

## File-level implementation and boundaries

- `src/Lunar.Desktop/Lunar.Desktop.Spike.csproj`, `App.xaml(.cs)`, `app.manifest`, `Shell/SpikeWindow.xaml(.cs)`, `Shell/TrayController.cs`: temporary presentation/lifetime spike, constructor injection, transparent topmost nonactivating-on-launch pill, idle mouse-activation hook, intentional tray keyboard activation, bounded structured smoke evidence and disposal. No eyes/navigation/settings/product pages.
- `src/Lunar.Windows/Lunar.Windows.Spike.csproj`, `Spike/Program.cs`, `Spike/NativeProbes.cs`: separate internal read-only COM/PInvoke/WinRT discovery executable. Core Audio/WLAN native resources released, no native interop in UI/ViewModels, no production application port or cross-layer reference.
- `scripts/Invoke-Sprint0.ps1`: explicit portable SDK, command evidence, restore/build, bounded child processes, optional visible five-second WPF smoke. Prototype windows and native probes run separately.
- `global.json`: pins observed SDK 10.0.402, no prerelease/roll-forward. `.gitignore`: toolchain, build outputs, local inputs/outputs excluded. Project lock files have no direct package dependency graph; implicit SDK download recorded separately.
- `docs/*`, `docs/ADR/*`, `docs/evidence/sprint-0/*`: status, findings, proposed decisions, limitations, license/fidelity/security risks, exact logs and synthetic WPF render image. Root canonical documentation preserved except user-approved clarifications.

No final production scaffold or public Application contract was introduced. No contract version or settings schema is accepted. No converter, installer, registry startup entry, telemetry, product backend or auto-download feature exists.

## Toolchain provenance

Default-sandbox metadata request failed: network unreachable (exit 1). Approved network-capable tool execution retrieved official [Microsoft release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json), selected SDK 10.0.402 win-x64 archive, downloaded locally, verified SHA-512 and extracted under ignored `.tools/dotnet`. No system SDK install/PATH update was requested. [Provenance and exact SHA-512](evidence/sprint-0/sdk-provenance.json); [observed SDK output](evidence/sprint-0/20261018T181705Z/sdk-info.log).

SDK-managed `Microsoft.Windows.SDK.NET.Ref` 10.0.19041.57 was restored from NuGet to `.tools/nuget`; nuspec points to Microsoft's Windows SDK license and marks license acceptance required. This is not interchangeable with the separate MIT CsWinRT source license. No image/PDF/DOCX library or installer tool was installed. Development restore/bootstrap network access does not introduce runtime conversion networking.

## Commands and actual outcomes

Environment for builds: standard-user Windows x64 10.0.26300.9711, portable SDK 10.0.402 / WindowsDesktop 10.0.13. Timestamps in raw logs are UTC; reporting date uses Asia/Jakarta.

| Command / action | Exit code | Outcome / artifact |
|---|---|---|
| `git status --short`; `git branch --show-current` | 128 each | Not a Git repository; initial inspection, no branch available |
| initial system `dotnet --info` | 0 | SDK 9.0.306; [environment summary](evidence/sprint-0/environment.json) |
| official metadata/download + `Get-FileHash -Algorithm SHA512` + portable `dotnet --info` | 0 | Published/actual hashes matched; [provenance](evidence/sprint-0/sdk-provenance.json) |
| first `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Invoke-Sprint0.ps1 -WindowSmoke` | 1 | Windows build succeeded; Desktop failed WFO0003; [first evidence run](evidence/sprint-0/20261018T181457Z/commands.jsonl) |
| same runner in default restricted sandbox | 1 | MSB4184: Microsoft SDK discovery path access denied; [sandbox failure](evidence/sprint-0/20261018T181601Z/restore-Windows.log) |
| next network-capable runner | 1 | Both builds/probes completed; runner failed to retrieve child ExitCode in PS 5.1; [preserved run](evidence/sprint-0/20261018T181613Z/commands.jsonl) |
| corrected runner with `-WindowSmoke` | 0 | Both Release builds, read-only native probes and window smoke complete; [successful commands](evidence/sprint-0/20261018T181705Z/commands.jsonl) |
| `dotnet restore src/Lunar.Windows/Lunar.Windows.Spike.csproj` | 0 | [Restore output](evidence/sprint-0/20261018T181705Z/restore-Windows.log) |
| `dotnet build src/Lunar.Windows/Lunar.Windows.Spike.csproj -c Release --no-restore` | 0 | 0 warnings/errors; [build output](evidence/sprint-0/20261018T181705Z/build-Windows.log) |
| `dotnet restore src/Lunar.Desktop/Lunar.Desktop.Spike.csproj` | 0 | [Restore output](evidence/sprint-0/20261018T181705Z/restore-Desktop.log) |
| `dotnet build src/Lunar.Desktop/Lunar.Desktop.Spike.csproj -c Release --no-restore` | 0 | 0 warnings/errors; [build output](evidence/sprint-0/20261018T181705Z/build-Desktop.log) |
| `dotnet <Windows Spike DLL>` | 0 | All five real API probe components Available on this host; [JSONL](evidence/sprint-0/20261018T181705Z/native-probes.jsonl). Runner exit 0 alone is not per-component PASS |
| `dotnet <Desktop Spike DLL> --smoke <new-evidence-file>` | 0 | Rendered, inactive at render, five-second shutdown, tray disposed; [JSONL](evidence/sprint-0/20261018T181705Z/window-smoke.jsonl) |
| inline PowerShell documentation consistency assertions | 0 | 11 checks PASS; [output](evidence/sprint-0/documentation-consistency.log) |
| inline PowerShell AST/XML and recorded-evidence assertions | 0 | Five checks PASS; [verification output](evidence/sprint-0/verification.log); source hashes in [manifest](evidence/sprint-0/source-manifest.json) |
| relative Markdown link validation | 0 | All checked local links resolve; [output](evidence/sprint-0/links.log) |
| `dotnet test`, solution architecture tests, `dotnet format` | NOT_RUN | No production solution or test projects/configured format gate in Sprint 0; do not interpret successful build as unit-test coverage |

WFO0003 remediation is narrowly documented in Desktop csproj: WPF owns DPI startup through the PerMonitorV2 manifest; WinForms is only a NotifyIcon implementation. Its WinForms entry-point analyzer is suppressed specifically, not all warnings. [Microsoft diagnostic guidance](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/compiler-messages/wfo0003) describes this diagnostic. Runtime evidence reports 120 DPI. This does not prove mixed-monitor behavior.

The PS 5.1 child process issue was corrected by retaining each process's native handle before waiting so its exit code remains readable. Failed run artifacts were preserved unchanged.

## Observed feasibility

- Core Audio: default render endpoint read succeeds; scalar range valid and mute false at snapshot. No write/notification test.
- Native Wi-Fi: one interface disconnected, software/hardware radio on (six PHY records). Enabled is not connected. No SSID, scans, current_connection query, permission prompt or toggle.
- Bluetooth: one radio On. No access request or state change.
- GSMTC: one OS-current session, pause and position controls advertised, finite timeline. `CanOfferSeek=true` is a capability snapshot only; no actual playback/seek command or media metadata read.
- Monitor/DPI: one 1920×1080 primary monitor, work area height 1020, effective DPI 120. No second monitor available in enumeration.
- WPF: 160×44 DIP, position left 688/top 8, DPI 120, render tier 2; runtime properties transparency/topmost true, taskbar/activation false. Five-second smoke and tray disposal successful. [WPF render snapshot](evidence/sprint-0/20261018T181705Z/window-smoke.png) inspected: pill and text render without clipping at the observed scale. It is not a desktop screenshot or proof of native hit testing/z-order.

Manual Windows verification: NOT_RUN. No human mouse/keyboard/tray/fullscreen interaction, hardware removal or target OS matrix result is claimed. See [compatibility matrix and steps](COMPATIBILITY.md).

## Gate and next required work

Build and read-only feasibility have evidence. Sprint 0 remains open: accepted consequential ADRs, mixed-DPI/multi-monitor/Win10+11 shell interaction evidence, real control/seek acceptance, selected converter licensing plus PDF/DOCX fidelity experiment, and concrete installer/deployment decision are missing. A custom-path LibreOffice install has not been ruled out; conventional installs are absent.

No ADR was self-approved and no feature/task was marked DONE. Continue only Sprint 0 review and remaining experiments; obtain explicit user approval before any consequential production decision and before Sprint 1. BE-01 remains TODO.

Minimum next inputs for completing the gate: an interactive target-OS/multi-monitor validation environment and evidence; an evidence-backed accepted converter/provider and installer/deployment decision; synthetic PDF/DOCX fixtures plus rendering/reference environment. These remain Sprint 0 work. The current handoff reports initial feasibility, not sprint closure.

## Authorized continuation — 2026-10-19

Additional discovery: pinned/signature-verified converter packages, scoped PNG/JPEG/EXIF/PDF and DOCX embedding experiments, four PDF raster visual/pixel checks and concrete ADR decisions. Success and new failures are preserved in `docs/evidence/sprint-0/converter-20261019/`. No original successful prototype rerun, public contract or Sprint 1 task.

Native PDF pixel geometry is calibrated on the 125% host, but PNG density metadata remains ~120 DPI; DOCX reference export did not complete and LibreOffice remains undetected. Native aggregate component/license applicability is a redistribution hold. Four ADRs are ready for user review and remain Proposed. The exit review classifies missing discovery evidence separately from later feature/release tests and recommends BLOCKED for closure/progression.

## User acceptance recorded — 2026-10-27

The user accepted ADR-0001–0004 in response to the technical decision approval request. See the [approval record](evidence/sprint-0/adr-acceptance-20261027/APPROVAL.md). Earlier statements that ADRs were Proposed describe their 2026-10-19 state; historical results/logs are unchanged. Strategy approval resolves that governance item only. Remaining target/manual shell and DOCX fidelity evidence is still missing; native redistribution/tooling/metadata conditions remain open. No build, native probe, converter or Sprint 1 task was run to record this approval.

## Completion attempt — 2026-10-27

Authorized scope: finish available Sprint 0 discovery, preserve accepted ADRs and all historical evidence; no production feature/task. The user confirmed no additional target OS environment or second monitor is available. [ADR-0005](ADR/ADR-0005-sprint-0-validation-deferral.md) proposes explicit deferral deadlines; it is NOT accepted/effective. Current gate remains BLOCKED until missing evidence is supplied or the proposed gate change and closure are explicitly approved.

Startup: working directory confirmed; `git status --short` and `git branch --show-current` each reported no Git repository (128). System `dotnet --info` reports SDK 9.0.306, Windows build 26300. Portable SDK remains 10.0.402. No solution/test project exists. Canonical architecture link in docs/README remains `../ARCHITECTURE.md`.

| Executed command | Environment / exit | Actual outcome / artifact |
|---|---|---|
| `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Invoke-Sprint0.ps1 -WindowSmoke` in sandbox | Windows x64 / 1 | Restore MSB4184: denied SDK discovery path; [commands](evidence/sprint-0/20261026T195257Z/commands.jsonl), [error](evidence/sprint-0/20261026T195257Z/restore-Windows.log) |
| Same runner with SDK-path access | Windows x64 build 26300 / SDK 10.0.402 / 0 | Both restores and Release builds PASS, zero warnings/errors; [commands](evidence/sprint-0/20261026T195334Z/commands.jsonl) |
| Native probe and WPF smoke invoked by runner | Same / 0 each | Five actual read-only probes Available; WPF rendered inactive, 160×44 DIP at 120 DPI, transparent/topmost properties true, five-second shutdown/tray disposal; [native](evidence/sprint-0/20261026T195334Z/native-probes.jsonl), [window](evidence/sprint-0/20261026T195334Z/window-smoke.jsonl) |
| `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Invoke-Sprint0Continuation.ps1 -WordReference` | Same / 1 overall | Locked converter restore and Release build exit 0, zero warnings/errors; generator exit 0. Overall failure is the bounded Word TIMEOUT, not a passing reference test. [commands](evidence/sprint-0/continuation-20261026T195608Z/commands.jsonl) |
| Converter generator inside continuation runner | Same / 0 | Existing synthetic image/PDF/DOCX assertions pass plus four PNG resolution/pixel-equality assertions. 5,849 ms, peak working set 415,744,000 bytes, not product performance acceptance. [JSONL](evidence/sprint-0/continuation-20261026T195608Z/generate.jsonl) |
| Guarded Word reference inside continuation runner | Same / TIMEOUT at 60 seconds | Created/proved owned process; opened read-only DOCX; computed pages; stalled at `ExportSyntheticPdf`. No PDF. Exact owned process stopped; original DOCX unchanged. [phases](evidence/sprint-0/continuation-20261026T195608Z/word-reference.jsonl), [result](evidence/sprint-0/continuation-20261026T195608Z/word-reference-result.json) |
| UI tool discovery / bounded interactive prototype launch | Same / NOT_RUN for interactions | No targetable Lunar window returned in repeated UI discovery; no mouse/tray/focus/hit-test PASS. Three session-owned prototype launches were cleaned by exact PID/name/UTC start time; [cleanup](evidence/sprint-0/continuation-20261026T195608Z/interactive-cleanup.json) |
| Unit/integration/UI suites / stable OS and mixed-monitor matrix | NOT_RUN | No production test projects; no extra target device/monitor. No fabricated test count or compatibility claim |

Only `PdfExperiments.cs` changed in the converter source: native PNGs remain untouched, separate `*-normalized.png` outputs encode requested density using the OS bitmap encoder. All decoded BGRA pixels, widths/heights and DPI values are asserted. Results: 149.9870 DPI and 299.9994 DPI (PNG integer pixels/metre quantization), tolerance 0.02 DPI; 1275×1650 and 2550×3300 pixels unchanged. Both normalized 150-DPI pages were visually inspected: text, quadrants, transparent stripe and vector overlap render correctly. All four normalized outputs have complete pixel equality against their native counterparts. This resolves the metadata discrepancy for these synthetic sRGB fixtures only; other OS/scales/ICC remain unverified. [Microsoft SetPixelData documentation](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.imaging.bitmapencoder.setpixeldata) specifies explicit resolution and sRGB behavior; no arbitrary-profile preservation is claimed.

New `Invoke-Sprint0Continuation.ps1` captures locked restore/build/generator/reference evidence, uses new synthetic workspaces, 60-second process bounds and proven Word ownership cleanup. `Export-WordDiscoveryReference.ps1` adds safe phase records without document text or raw Office errors. No package graph/public port/production DI change, no renderer install, no device/media state writes, no private screenshot saved. Framework DI/logging stay the accepted constructor-injection/JSONL discovery design in ADR-0001, with production tasks still TODO.

Verification: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test-Sprint0Evidence.ps1` first exited 1 because PowerShell 5.1 wrapped a deserialized JSON array; [failed check log](evidence/sprint-0/continuation-20261026T195608Z/verification.log) retained. Corrected array enumeration; next invocation exit 0: build/runtime evidence, four density records, all 15 synthetic artifact hashes, four scripts' syntax, three spike reference graphs, 56 TODO statuses, canonical architecture and 172 relative Markdown file links checked. [Passing log](evidence/sprint-0/continuation-20261026T195608Z/verification-final.log). This is an evidence/static check, not a unit/UI/fidelity suite. [Current source manifest](evidence/sprint-0/continuation-20261026T195608Z/source-manifest.json).

## User-approved discovery closure — 2026-10-27

After reviewing ADR-0005, the user explicitly stated: “ya saya setuju, dan tutup sprint 0 dengan kondisi yg saat ini masih terbatas”. [Approval record](evidence/sprint-0/closure-20261027/APPROVAL.md). ADR-0005 is now Accepted; AGENTS §4 and PRD §9 implement its evidence-timing exception and named Sprint 1/6/7 deadlines. Sprint 0 discovery transitions IN_REVIEW → DONE and its approved gate is CLOSED with accepted limitations. No test outcome changed; original blocked recommendations above describe the pre-approval state.

This closure action edits documentation and the evidence checker only. Existing source/projects/packages, architecture/backlogs, runners and raw evidence are hash-checked against the [pre-closure baseline](evidence/sprint-0/closure-20261027/preserved-before.json). No new build/restore/native/UI/conversion/renderer/license/performance result is claimed. `dotnet --info` again reports system SDK 9.0.306; `./.tools/dotnet/dotnet.exe --version` returns 10.0.402. Both Git status/branch checks still report not a repository, exit 128.

`powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test-Sprint0Evidence.ps1` is the closure evidence/static check, not an integration test. Its current accepted-state checks and original technical-record checks are recorded in [closure commands](evidence/sprint-0/closure-20261027/commands.jsonl) and [final verification](evidence/sprint-0/closure-20261027/verification-final.log). The first checker failure on equivalent FE-05/BE-08 shorthand is retained; the comparison was corrected. All 56 numbered tasks remain TODO. Next authorized action: WAIT for separate task/Sprint 1 authorization; no repeat closure approval needed.
