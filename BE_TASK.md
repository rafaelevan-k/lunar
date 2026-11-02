# Lunar — Backend / Windows integration task backlog

> Canonical requirements: `PRD.md`. Canonical architecture: `ARCHITECTURE.md`. This backlog is implementation guidance, not permission to violate either document.

**Total:** 30 tasks (maximum 30). **Platform:** Windows 10/11 x64 · C# / .NET 10 · WPF · MVVM · modular monolith.

## Agent execution contract

1. Read `PRD.md`, `ARCHITECTURE.md`, and this task file before editing. If architecture still names `Lunar_PRD.md`, interpret that reference as `PRD.md` and update it in a separate documentation-only change.
2. Execute **one task ID at a time**. First inspect existing code, then state exact files to create/edit and architectural ownership. Avoid duplicate abstractions and speculative scaffolding.
3. Preserve dependency direction: Domain independent; Application depends on Domain; adapters implement Application contracts; Desktop is composition root. WPF UI and ViewModels must not call Windows APIs or conversion engines directly.
4. Each task requires focused tests, relevant build/test commands, error/empty/loading states, cancellation/disposal as applicable, and an explicit completion report. Never claim tests ran unless they actually ran.
5. When task requires a Windows-only API and agent is not running on Windows, implement only verifiable portable parts, record blocked Windows validation, and do not mark task Done.
6. For architectural changes, propose an ADR first. No silent changes to `ARCHITECTURE.md`. No admin privilege assumptions, fabricated functionality, hidden network uploads, or false file-fidelity promises.

## Tracking conventions

- Status: `TODO` → `IN_PROGRESS` → `BLOCKED` / `IN_REVIEW` → `DONE`. A task is DONE only after acceptance criteria and evidence are recorded.
- Task ID is immutable. Suggested evidence: changed files, build/test commands with exit codes, screenshots (FE) or test logs (BE), and risks.
- Dependencies: backend service contracts and fakes can be built in parallel with UI; live integration follows adapter readiness. Sprint labels map to `PRD.md`.

## Task index

| ID | Sprint | Task | Status |
|---|---|---|---|
| BE-01 | Sprint 1 | Solution scaffold and architecture guardrails | DONE |
| BE-02 | Sprint 1 | CI, scripts and quality gates | TODO |
| BE-03 | Sprint 1 | Domain primitives and result contracts | DONE |
| BE-04 | Sprint 1 | Application interfaces and DI composition | DONE |
| BE-05 | Sprint 1 | App lifecycle and single-instance policy | DONE |
| BE-06 | Sprint 1 | Settings repository and migration | DONE |
| BE-07 | Sprint 1 | Structured diagnostics and privacy | DONE |
| BE-08 | Sprint 1 | Window environment and display provider | DONE |
| BE-09 | Sprint 2–3 | Island state transition use case | TODO |
| BE-10 | Sprint 4 | Core Audio volume adapter | TODO |
| BE-11 | Sprint 4 | Volume use cases and synchronization | TODO |
| BE-12 | Sprint 4 | Native Wi-Fi status adapter | TODO |
| BE-13 | Sprint 4 | Bluetooth capability adapter | TODO |
| BE-14 | Sprint 4 | Clock and settings deep-link services | TODO |
| BE-15 | Sprint 5 | Media session discovery adapter | TODO |
| BE-16 | Sprint 5 | Media transport commands | TODO |
| BE-17 | Sprint 5 | Seek position orchestration | TODO |
| BE-18 | Sprint 5 | Media event lifecycle and session policy | TODO |
| BE-19 | Sprint 6 | Conversion domain and matrix policy | TODO |
| BE-20 | Sprint 6 | Conversion planner and validation | TODO |
| BE-21 | Sprint 6 | Job queue, cancellation and progress | TODO |
| BE-22 | Sprint 6 | Raster image conversion provider | TODO |
| BE-23 | Sprint 6 | Images to PDF and DOCX providers | TODO |
| BE-24 | Sprint 6 | PDF rasterization provider | TODO |
| BE-25 | Sprint 6 | DOCX renderer optional dependency | TODO |
| BE-26 | Sprint 6 | Output transaction and temp cleanup | TODO |
| BE-27 | Sprint 6 | Conversion orchestrator and application use case | TODO |
| BE-28 | Sprint 7 | Security hardening and dependency governance | TODO |
| BE-29 | Sprint 7 | Installer, autostart and rollback | TODO |
| BE-30 | Sprint 7 | Backend integration and release verification | TODO |

## Task specifications and ready-to-send prompts

### BE-01 — Solution scaffold and architecture guardrails

- **Sprint:** Sprint 1
- **Expected location:** `Lunar.sln; src/*; tests/*; Directory.Build.props`
- **Objective:** Create Domain, Application, Windows, Conversion, Infrastructure, Desktop and test projects with allowed project references; SDK pin, analyzers.
- **Acceptance criteria:** dotnet restore/build/test on Windows; no forbidden dependency direction.
- **Status:** DONE — [evidence](docs/evidence/sprint-1/BE-01.md), 2026-10-27

**Prompt for AI coding agent**

```text
TASK: BE-01 — Solution scaffold and architecture guardrails
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and BE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY BE-01 (Sprint 1).
Architecture owner / relevant paths: Lunar.sln; src/*; tests/*; Directory.Build.props.
Functional scope: Create Domain, Application, Windows, Conversion, Infrastructure, Desktop and test projects with allowed project references; SDK pin, analyzers.
Acceptance requirements: dotnet restore/build/test on Windows; no forbidden dependency direction.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### BE-02 — CI, scripts and quality gates

- **Sprint:** Sprint 1
- **Expected location:** `scripts; .github/workflows/ci.yml`
- **Objective:** Create PowerShell build/test/package scripts, Windows CI matrix, formatting, analyzers and artifact retention.
- **Acceptance criteria:** PR build blocks failing tests and architecture checks.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: BE-02 — CI, scripts and quality gates
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and BE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY BE-02 (Sprint 1).
Architecture owner / relevant paths: scripts; .github/workflows/ci.yml.
Functional scope: Create PowerShell build/test/package scripts, Windows CI matrix, formatting, analyzers and artifact retention.
Acceptance requirements: PR build blocks failing tests and architecture checks.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### BE-03 — Domain primitives and result contracts

- **Sprint:** Sprint 1
- **Owner / expected location:** `src/Lunar.Domain/Shared`
- **Objective:** Define Result/ErrorCode and typed failures, cancellation semantics and immutable value objects.
- **Acceptance criteria:** Unit tests cover success/failure; Domain has no platform dependencies.
- **Status:** DONE — [evidence](docs/evidence/sprint-1/BE-03.md), 2026-11-03

**Prompt for AI coding agent**

```text
TASK: BE-03 — Domain primitives and result contracts
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and BE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY BE-03 (Sprint 1).
Architecture owner / relevant paths: Lunar.Domain/Shared.
Functional scope: Define Result/ErrorCode and typed failures, cancellation semantics and immutable value objects.
Acceptance requirements: Unit tests cover success/failure; Domain has no platform dependencies.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### BE-04 — Application interfaces and DI composition

- **Sprint:** Sprint 1
- **Owner / expected location:** `src/Lunar.Application/Abstractions; Lunar.Desktop/Composition`
- **Objective:** Define contracts for system, media, conversion, settings and shell; register concrete adapters only in composition root.
- **Acceptance criteria:** Mocks usable in tests; scopes and disposal verified.
- **Status:** DONE — [evidence](docs/evidence/sprint-1/BE-04.md), 2026-11-03. Scope per accepted ADR-0006: system and media ports plus DI root; conversion, settings and shell contracts deferred to BE-19/20/24, BE-06 and BE-08.

**Prompt for AI coding agent**

```text
TASK: BE-04 — Application interfaces and DI composition
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and BE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY BE-04 (Sprint 1).
Architecture owner / relevant paths: Lunar.Application/Abstractions; Lunar.Desktop/Composition.
Functional scope: Define contracts for system, media, conversion, settings and shell; register concrete adapters only in composition root.
Acceptance requirements: Mocks usable in tests; scopes and disposal verified.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### BE-05 — App lifecycle and single-instance policy

- **Sprint:** Sprint 1
- **Owner / expected location:** `src/Lunar.Desktop/App.xaml.cs; Lunar.Infrastructure`
- **Objective:** Implement startup, mutex/single instance activation, graceful shutdown, tray lifetime, cancellation tokens.
- **Acceptance criteria:** Second launch activates first; clean process exit.
- **Status:** DONE — [evidence](docs/evidence/sprint-1/BE-05.md), 2026-11-03. ADR-0008 accepted. Activation request is delivered and handled; taking the foreground is best-effort (1 of 2 real runs).

**Prompt for AI coding agent**

```text
TASK: BE-05 — App lifecycle and single-instance policy
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and BE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY BE-05 (Sprint 1).
Architecture owner / relevant paths: Lunar.Desktop/App.xaml.cs; Lunar.Infrastructure.
Functional scope: Implement startup, mutex/single instance activation, graceful shutdown, tray lifetime, cancellation tokens.
Acceptance requirements: Second launch activates first; clean process exit.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### BE-06 — Settings repository and migration

- **Sprint:** Sprint 1
- **Owner / expected location:** `src/Lunar.Infrastructure/Settings; Storage`
- **Objective:** Persist versioned settings to LocalAppData with atomic writes, backup, validation, schema migration and defaults.
- **Acceptance criteria:** Corrupt settings recover without crashing; tests for migration.
- **Status:** DONE — [evidence](docs/evidence/sprint-1/BE-06.md), 2026-11-03. ADR-0011 accepted. Migration mechanism is tested with test steps only; v1 is the first schema, so no production migration exists yet. Not yet used by the app (FE-05/FE-23 consume it).

**Prompt for AI coding agent**

```text
TASK: BE-06 — Settings repository and migration
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and BE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY BE-06 (Sprint 1).
Architecture owner / relevant paths: Lunar.Infrastructure/Settings; Storage.
Functional scope: Persist versioned settings to LocalAppData with atomic writes, backup, validation, schema migration and defaults.
Acceptance requirements: Corrupt settings recover without crashing; tests for migration.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### BE-07 — Structured diagnostics and privacy

- **Sprint:** Sprint 1
- **Owner / expected location:** `src/Lunar.Infrastructure/Logging; docs/PRIVACY.md`
- **Objective:** Add Serilog with bounded rolling files, redaction and opt-in diagnostics; never log sensitive file contents/passwords.
- **Acceptance criteria:** No raw sensitive data in normal logs; bounded storage.
- **Status:** DONE — [evidence](docs/evidence/sprint-1/BE-07.md), 2026-11-03. ADR-0009 accepted. Opt-in is the `LUNAR_DIAGNOSTICS=1` environment variable until BE-06/FE-23; multi-day retention not observed.

**Prompt for AI coding agent**

```text
TASK: BE-07 — Structured diagnostics and privacy
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and BE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY BE-07 (Sprint 1).
Architecture owner / relevant paths: Lunar.Infrastructure/Logging; docs/PRIVACY.md.
Functional scope: Add Serilog with bounded rolling files, redaction and opt-in diagnostics; never log sensitive file contents/passwords.
Acceptance requirements: No raw sensitive data in normal logs; bounded storage.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### BE-08 — Window environment and display provider

- **Sprint:** Sprint 1
- **Owner / expected location:** `src/Lunar.Application/Abstractions/Shell; Lunar.Windows/Environment`
- **Objective:** Provide monitor/work area/DPI change notifications, positioning-safe abstractions and OS settings launcher.
- **Acceptance criteria:** Monitor hotplug and DPI events handled; fallback behavior.
- **Status:** DONE by user decision — [evidence](docs/evidence/sprint-1/BE-08.md), 2026-11-03. ADR-0010 accepted. Code and simulated-message tests pass. The user explicitly deferred real hotplug/DPI-change evidence (ADR-0005) to FE-05 ("Tunda ke FE-05"). FE-05 then observed four real scale/resolution changes (notification raised, pill stayed visible); monitor hotplug, mixed DPI and a DPI-free work-area change remain unverified Sprint 1 gate items. The settings launcher is delivered here; BE-14 keeps the clock and device verification.

**Prompt for AI coding agent**

```text
TASK: BE-08 — Window environment and display provider
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and BE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY BE-08 (Sprint 1).
Architecture owner / relevant paths: Lunar.Application/Abstractions/Shell; Lunar.Windows/Environment.
Functional scope: Provide monitor/work area/DPI change notifications, positioning-safe abstractions and OS settings launcher.
Acceptance requirements: Monitor hotplug and DPI events handled; fallback behavior.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### BE-09 — Island state transition use case

- **Sprint:** Sprint 2–3
- **Owner / expected location:** `src/Lunar.Application/Shell`
- **Objective:** Implement validated state transitions and events, interruption/reentrancy rules and time abstraction.
- **Acceptance criteria:** Unit tests cover rapid expand/collapse and invalid transitions.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: BE-09 — Island state transition use case
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and BE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY BE-09 (Sprint 2–3).
Architecture owner / relevant paths: Lunar.Application/Shell.
Functional scope: Implement validated state transitions and events, interruption/reentrancy rules and time abstraction.
Acceptance requirements: Unit tests cover rapid expand/collapse and invalid transitions.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### BE-10 — Core Audio volume adapter

- **Sprint:** Sprint 4
- **Owner / expected location:** `src/Lunar.Windows/Audio/CoreAudioVolumeService.cs`
- **Objective:** Implement master volume read/set/mute and external change notifications with COM lifetime handling.
- **Acceptance criteria:** Test real device plus missing endpoint and disposal.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: BE-10 — Core Audio volume adapter
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and BE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY BE-10 (Sprint 4).
Architecture owner / relevant paths: Lunar.Windows/Audio/CoreAudioVolumeService.cs.
Functional scope: Implement master volume read/set/mute and external change notifications with COM lifetime handling.
Acceptance requirements: Test real device plus missing endpoint and disposal.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### BE-11 — Volume use cases and synchronization

- **Sprint:** Sprint 4
- **Owner / expected location:** `src/Lunar.Application/System`
- **Objective:** Implement SetVolume, ToggleMute, subscribe snapshots, clamp and cancellation, with coalescing where needed.
- **Acceptance criteria:** No feedback loops; unit tests with fake adapter.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: BE-11 — Volume use cases and synchronization
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and BE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY BE-11 (Sprint 4).
Architecture owner / relevant paths: Lunar.Application/System.
Functional scope: Implement SetVolume, ToggleMute, subscribe snapshots, clamp and cancellation, with coalescing where needed.
Acceptance requirements: No feedback loops; unit tests with fake adapter.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### BE-12 — Native Wi-Fi status adapter

- **Sprint:** Sprint 4
- **Owner / expected location:** `src/Lunar.Windows/Network/NativeWifiService.cs`
- **Objective:** Read Wi-Fi adapter/radio/connectivity state using supported Windows APIs; gate toggle behind validated capability.
- **Acceptance criteria:** Unknown/disabled/no-adapter handled; no admin assumptions.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: BE-12 — Native Wi-Fi status adapter
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and BE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY BE-12 (Sprint 4).
Architecture owner / relevant paths: Lunar.Windows/Network/NativeWifiService.cs.
Functional scope: Read Wi-Fi adapter/radio/connectivity state using supported Windows APIs; gate toggle behind validated capability.
Acceptance requirements: Unknown/disabled/no-adapter handled; no admin assumptions.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### BE-13 — Bluetooth capability adapter

- **Sprint:** Sprint 4
- **Owner / expected location:** `src/Lunar.Windows/Bluetooth/WindowsBluetoothService.cs`
- **Objective:** Read Bluetooth availability/radio state and implement permitted controls only; otherwise expose Settings fallback.
- **Acceptance criteria:** Unsupported, denied and no-radio states distinguishable.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: BE-13 — Bluetooth capability adapter
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and BE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY BE-13 (Sprint 4).
Architecture owner / relevant paths: Lunar.Windows/Bluetooth/WindowsBluetoothService.cs.
Functional scope: Read Bluetooth availability/radio state and implement permitted controls only; otherwise expose Settings fallback.
Acceptance requirements: Unsupported, denied and no-radio states distinguishable.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### BE-14 — Clock and settings deep-link services

- **Sprint:** Sprint 4
- **Owner / expected location:** `src/Lunar.Windows/Environment`
- **Objective:** Provide locale-aware clock abstraction and safe ms-settings deep links with failure handling.
- **Acceptance criteria:** Clock injectable; link failure does not crash app.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: BE-14 — Clock and settings deep-link services
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and BE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY BE-14 (Sprint 4).
Architecture owner / relevant paths: Lunar.Windows/Environment.
Functional scope: Provide locale-aware clock abstraction and safe ms-settings deep links with failure handling.
Acceptance requirements: Clock injectable; link failure does not crash app.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### BE-15 — Media session discovery adapter

- **Sprint:** Sprint 5
- **Owner / expected location:** `src/Lunar.Windows/Media/GsmtcMediaService.cs`
- **Objective:** Discover GSMTC sessions, current media metadata, artwork, playback/timeline and capability flags.
- **Acceptance criteria:** No session and multiple sessions handled; subscriptions cleaned.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: BE-15 — Media session discovery adapter
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and BE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY BE-15 (Sprint 5).
Architecture owner / relevant paths: Lunar.Windows/Media/GsmtcMediaService.cs.
Functional scope: Discover GSMTC sessions, current media metadata, artwork, playback/timeline and capability flags.
Acceptance requirements: No session and multiple sessions handled; subscriptions cleaned.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### BE-16 — Media transport commands

- **Sprint:** Sprint 5
- **Owner / expected location:** `src/Lunar.Application/Media; Lunar.Windows/Media`
- **Objective:** Implement play/pause/next/previous with capability validation and async command outcomes.
- **Acceptance criteria:** Commands disabled when unsupported; real-app smoke test.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: BE-16 — Media transport commands
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and BE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY BE-16 (Sprint 5).
Architecture owner / relevant paths: Lunar.Application/Media; Lunar.Windows/Media.
Functional scope: Implement play/pause/next/previous with capability validation and async command outcomes.
Acceptance requirements: Commands disabled when unsupported; real-app smoke test.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### BE-17 — Seek position orchestration

- **Sprint:** Sprint 5
- **Owner / expected location:** `src/Lunar.Application/Media/UseCases/SeekMedia.cs`
- **Objective:** Convert UI position to supported units, clamp to timeline, commit seek only when supported, reconcile returned timeline.
- **Acceptance criteria:** Test zero/unknown duration, paused and session change during seek.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: BE-17 — Seek position orchestration
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and BE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY BE-17 (Sprint 5).
Architecture owner / relevant paths: Lunar.Application/Media/UseCases/SeekMedia.cs.
Functional scope: Convert UI position to supported units, clamp to timeline, commit seek only when supported, reconcile returned timeline.
Acceptance requirements: Test zero/unknown duration, paused and session change during seek.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### BE-18 — Media event lifecycle and session policy

- **Sprint:** Sprint 5
- **Owner / expected location:** `src/Lunar.Application/Media; Lunar.Windows/Media`
- **Objective:** Choose active session deterministically, support user override if feasible, throttle events and detach stale sessions.
- **Acceptance criteria:** No stale event handlers; reconnect after player exit.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: BE-18 — Media event lifecycle and session policy
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and BE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY BE-18 (Sprint 5).
Architecture owner / relevant paths: Lunar.Application/Media; Lunar.Windows/Media.
Functional scope: Choose active session deterministically, support user override if feasible, throttle events and detach stale sessions.
Acceptance requirements: No stale event handlers; reconnect after player exit.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### BE-19 — Conversion domain and matrix policy

- **Sprint:** Sprint 6
- **Owner / expected location:** `src/Lunar.Domain/Conversion; docs/CONVERSION_MATRIX.md`
- **Objective:** Model FileFormat, ConversionRoute, fidelity, same-format behavior and unsupported PDF->DOCX stretch route.
- **Acceptance criteria:** All 25 ordered format pairs explicitly classified and tested.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: BE-19 — Conversion domain and matrix policy
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and BE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY BE-19 (Sprint 6).
Architecture owner / relevant paths: Lunar.Domain/Conversion; docs/CONVERSION_MATRIX.md.
Functional scope: Model FileFormat, ConversionRoute, fidelity, same-format behavior and unsupported PDF->DOCX stretch route.
Acceptance requirements: All 25 ordered format pairs explicitly classified and tested.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### BE-20 — Conversion planner and validation

- **Sprint:** Sprint 6
- **Owner / expected location:** `src/Lunar.Conversion/Planning; Validation`
- **Objective:** Validate file signatures, extensions, sizes, route prerequisites, page limits, path safety and output collisions.
- **Acceptance criteria:** Malicious/corrupt input rejected with actionable errors.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: BE-20 — Conversion planner and validation
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and BE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY BE-20 (Sprint 6).
Architecture owner / relevant paths: Lunar.Conversion/Planning; Validation.
Functional scope: Validate file signatures, extensions, sizes, route prerequisites, page limits, path safety and output collisions.
Acceptance requirements: Malicious/corrupt input rejected with actionable errors.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### BE-21 — Job queue, cancellation and progress

- **Sprint:** Sprint 6
- **Owner / expected location:** `src/Lunar.Conversion/Pipeline`
- **Objective:** Implement bounded concurrency, per-job cancellation, progress events, timeouts and nonblocking execution.
- **Acceptance criteria:** Cancel leaves no partial output; no UI thread blocking.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: BE-21 — Job queue, cancellation and progress
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and BE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY BE-21 (Sprint 6).
Architecture owner / relevant paths: Lunar.Conversion/Pipeline.
Functional scope: Implement bounded concurrency, per-job cancellation, progress events, timeouts and nonblocking execution.
Acceptance requirements: Cancel leaves no partial output; no UI thread blocking.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### BE-22 — Raster image conversion provider

- **Sprint:** Sprint 6
- **Owner / expected location:** `src/Lunar.Conversion/Providers/ImageConversionProvider.cs`
- **Objective:** Implement PNG/JPEG encode/decode, alpha compositing, EXIF orientation, configurable quality and limits.
- **Acceptance criteria:** Golden tests for transparency, orientation, Unicode filenames.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: BE-22 — Raster image conversion provider
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and BE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY BE-22 (Sprint 6).
Architecture owner / relevant paths: Lunar.Conversion/Providers/ImageConversionProvider.cs.
Functional scope: Implement PNG/JPEG encode/decode, alpha compositing, EXIF orientation, configurable quality and limits.
Acceptance requirements: Golden tests for transparency, orientation, Unicode filenames.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### BE-23 — Images to PDF and DOCX providers

- **Sprint:** Sprint 6
- **Owner / expected location:** `src/Lunar.Conversion/Providers/PdfCreationProvider.cs; DocxEmbeddingProvider.cs`
- **Objective:** Create PDF image pages and DOCX documents embedding images; support page layout and explicit non-OCR semantics.
- **Acceptance criteria:** Output opens in standard viewers; no false editable-text claims.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: BE-23 — Images to PDF and DOCX providers
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and BE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY BE-23 (Sprint 6).
Architecture owner / relevant paths: Lunar.Conversion/Providers/PdfCreationProvider.cs; DocxEmbeddingProvider.cs.
Functional scope: Create PDF image pages and DOCX documents embedding images; support page layout and explicit non-OCR semantics.
Acceptance requirements: Output opens in standard viewers; no false editable-text claims.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### BE-24 — PDF rasterization provider

- **Sprint:** Sprint 6
- **Owner / expected location:** `src/Lunar.Conversion/Providers/PdfRasterizationProvider.cs`
- **Objective:** Render selected PDF pages to PNG/JPEG with DPI, password handling in memory and pixel/page caps.
- **Acceptance criteria:** Multi-page naming, encrypted/invalid PDFs and 100-page test.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: BE-24 — PDF rasterization provider
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and BE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY BE-24 (Sprint 6).
Architecture owner / relevant paths: Lunar.Conversion/Providers/PdfRasterizationProvider.cs.
Functional scope: Render selected PDF pages to PNG/JPEG with DPI, password handling in memory and pixel/page caps.
Acceptance requirements: Multi-page naming, encrypted/invalid PDFs and 100-page test.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### BE-25 — DOCX renderer optional dependency

- **Sprint:** Sprint 6
- **Owner / expected location:** `src/Lunar.Conversion/Providers/LibreOfficeRendererProvider.cs`
- **Objective:** Detect user-installed LibreOffice, invoke headless in isolated profile with safe args, timeout and cleanup; DOCX->PDF->image.
- **Acceptance criteria:** Missing dependency is explained; subprocess cleaned on cancel.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: BE-25 — DOCX renderer optional dependency
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and BE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY BE-25 (Sprint 6).
Architecture owner / relevant paths: Lunar.Conversion/Providers/LibreOfficeRendererProvider.cs.
Functional scope: Detect user-installed LibreOffice, invoke headless in isolated profile with safe args, timeout and cleanup; DOCX->PDF->image.
Acceptance requirements: Missing dependency is explained; subprocess cleaned on cancel.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### BE-26 — Output transaction and temp cleanup

- **Sprint:** Sprint 6
- **Owner / expected location:** `src/Lunar.Conversion/Output; Pipeline/TempWorkspace.cs`
- **Objective:** Write to temporary workspace, validate output, atomically commit where possible, resolve collisions and cleanup.
- **Acceptance criteria:** Disk full and interrupted jobs preserve originals.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: BE-26 — Output transaction and temp cleanup
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and BE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY BE-26 (Sprint 6).
Architecture owner / relevant paths: Lunar.Conversion/Output; Pipeline/TempWorkspace.cs.
Functional scope: Write to temporary workspace, validate output, atomically commit where possible, resolve collisions and cleanup.
Acceptance requirements: Disk full and interrupted jobs preserve originals.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### BE-27 — Conversion orchestrator and application use case

- **Sprint:** Sprint 6
- **Owner / expected location:** `src/Lunar.Conversion/Pipeline/ConversionOrchestrator.cs; Lunar.Application/Conversion`
- **Objective:** Coordinate planner, providers, progress, cancel and output; expose typed result without UI references.
- **Acceptance criteria:** Integration tests cover supported routes and failure recovery.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: BE-27 — Conversion orchestrator and application use case
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and BE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY BE-27 (Sprint 6).
Architecture owner / relevant paths: Lunar.Conversion/Pipeline/ConversionOrchestrator.cs; Lunar.Application/Conversion.
Functional scope: Coordinate planner, providers, progress, cancel and output; expose typed result without UI references.
Acceptance requirements: Integration tests cover supported routes and failure recovery.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### BE-28 — Security hardening and dependency governance

- **Sprint:** Sprint 7
- **Expected location:** `docs/SECURITY.md; THIRD_PARTY_NOTICES.md`
- **Objective:** Audit input resource exhaustion, zip bombs, symlinks/path traversal, external binaries, licenses, updates and secrets.
- **Acceptance criteria:** Document threat model and verified mitigations.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: BE-28 — Security hardening and dependency governance
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and BE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY BE-28 (Sprint 7).
Architecture owner / relevant paths: docs/SECURITY.md; THIRD_PARTY_NOTICES.md.
Functional scope: Audit input resource exhaustion, zip bombs, symlinks/path traversal, external binaries, licenses, updates and secrets.
Acceptance requirements: Document threat model and verified mitigations.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### BE-29 — Installer, autostart and rollback

- **Sprint:** Sprint 7
- **Expected location:** `packaging/wix; Lunar.Infrastructure/Startup`
- **Objective:** Build signed-if-available installer, uninstall, startup registration, upgrade/migration, prerequisites and rollback.
- **Acceptance criteria:** Fresh install/upgrade/uninstall smoke tested on Windows.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: BE-29 — Installer, autostart and rollback
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and BE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY BE-29 (Sprint 7).
Architecture owner / relevant paths: packaging/wix; Lunar.Infrastructure/Startup.
Functional scope: Build signed-if-available installer, uninstall, startup registration, upgrade/migration, prerequisites and rollback.
Acceptance requirements: Fresh install/upgrade/uninstall smoke tested on Windows.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### BE-30 — Backend integration and release verification

- **Sprint:** Sprint 7
- **Expected location:** `tests/*; docs/TEST_REPORT.md; docs/RELEASE_CHECKLIST.md`
- **Objective:** Run end-to-end integration, stress, cancellation, failure injection, OS compatibility, resource measurement and package validation.
- **Acceptance criteria:** Release checklist with actual evidence; known gaps documented.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: BE-30 — Backend integration and release verification
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and BE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY BE-30 (Sprint 7).
Architecture owner / relevant paths: tests/*; docs/TEST_REPORT.md; docs/RELEASE_CHECKLIST.md.
Functional scope: Run end-to-end integration, stress, cancellation, failure injection, OS compatibility, resource measurement and package validation.
Acceptance requirements: Release checklist with actual evidence; known gaps documented.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

## Final release gate

- All task IDs have reviewable evidence; no open P0/P1 defects.
- Cross-check all feature behaviors and conversion fidelity against `PRD.md` and boundaries against `ARCHITECTURE.md`.
- Complete Windows 10/11 smoke tests, DPI/multi-monitor, cancellation, failure handling, packaging, privacy and licensing checks.
- Update docs/TEST_REPORT.md, docs/RELEASE_CHECKLIST.md and user-facing limitations.
