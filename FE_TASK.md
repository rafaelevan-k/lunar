# Lunar — Frontend task backlog

> Canonical requirements: `PRD.md`. Canonical architecture: `ARCHITECTURE.md`. This backlog is implementation guidance, not permission to violate either document.

**Total:** 26 tasks (maximum 30). **Platform:** Windows 10/11 x64 · C# / .NET 10 · WPF · MVVM · modular monolith.

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
| FE-01 | Sprint 1 | UI foundation and design tokens | DONE |
| FE-02 | Sprint 1 | WPF application shell | DONE |
| FE-03 | Sprint 1 | Shell layout and adaptive sizing | DONE |
| FE-04 | Sprint 1 | Island view model and state binding | DONE |
| FE-05 | Sprint 1 | Top-center monitor positioning | DONE |
| FE-06 | Sprint 1 | System tray interface | DONE |
| FE-07 | Sprint 2 | Eyes vector artwork | TODO |
| FE-08 | Sprint 2 | Blink animation state machine | TODO |
| FE-09 | Sprint 2 | Pupil gaze and hover interaction | TODO |
| FE-10 | Sprint 3 | Expansion and collapse transitions | TODO |
| FE-11 | Sprint 3 | Swiper navigation controller | TODO |
| FE-12 | Sprint 3 | Reusable controls and feedback | TODO |
| FE-13 | Sprint 4 | System page presentation | TODO |
| FE-14 | Sprint 4 | Volume slider UX | TODO |
| FE-15 | Sprint 4 | Network and Bluetooth UX | TODO |
| FE-16 | Sprint 4 | Clock and locale UX | TODO |
| FE-17 | Sprint 5 | Media page layout | TODO |
| FE-18 | Sprint 5 | Media seek slider interaction | TODO |
| FE-19 | Sprint 5 | Media session switching and feedback | TODO |
| FE-20 | Sprint 6 | Converter intake and format selection | TODO |
| FE-21 | Sprint 6 | Converter options and fidelity notices | TODO |
| FE-22 | Sprint 6 | Conversion queue and progress UX | TODO |
| FE-23 | Sprint 7 | Settings and preferences UI | TODO |
| FE-24 | Sprint 7 | Accessibility and localization | TODO |
| FE-25 | Sprint 7 | Performance and lifecycle polish | TODO |
| FE-26 | Sprint 7 | UI regression and release acceptance | TODO |

## Task specifications and ready-to-send prompts

### FE-01 — UI foundation and design tokens

- **Sprint:** Sprint 1
- **Owner / expected location:** `src/Lunar.Desktop/Themes`
- **Objective:** Create Colors, Typography, Spacing, Components resource dictionaries and documented tokens; ensure light/dark contrast.
- **Acceptance criteria:** Theme resources compile, no hard-coded repeated brushes, high contrast review.
- **Status:** DONE — [evidence](docs/evidence/sprint-1/FE-01.md), 2026-11-03. Light palette approved by the user; on-screen high-contrast review deferred to FE-02 by explicit user decision (2026-11-03).

**Prompt for AI coding agent**

```text
TASK: FE-01 — UI foundation and design tokens
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and FE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY FE-01 (Sprint 1).
Architecture owner / relevant paths: Lunar.Desktop/Themes.
Functional scope: Create Colors, Typography, Spacing, Components resource dictionaries and documented tokens; ensure light/dark contrast.
Acceptance requirements: Theme resources compile, no hard-coded repeated brushes, high contrast review.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### FE-02 — WPF application shell

- **Sprint:** Sprint 1
- **Owner / expected location:** `src/Lunar.Desktop/App.xaml; Shell/IslandWindow.xaml`
- **Objective:** Create borderless transparent WPF window with composited rounded pill and a single view host.
- **Acceptance criteria:** Runs as a normal non-activating utility where feasible; no taskbar clutter; clean close.
- **Status:** DONE — [evidence](docs/evidence/sprint-1/FE-02.md), 2026-11-03. ADR-0007 accepted; high-contrast review carried from FE-01 passed (user-reported). No user-facing exit until FE-06; DPI/monitor matrix pending FE-03/05.

**Prompt for AI coding agent**

```text
TASK: FE-02 — WPF application shell
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and FE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY FE-02 (Sprint 1).
Architecture owner / relevant paths: Lunar.Desktop/App.xaml; Shell/IslandWindow.xaml.
Functional scope: Create borderless transparent WPF window with composited rounded pill and a single view host.
Acceptance requirements: Runs as a normal non-activating utility where feasible; no taskbar clutter; clean close.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### FE-03 — Shell layout and adaptive sizing

- **Sprint:** Sprint 1
- **Owner / expected location:** `src/Lunar.Desktop/Shell`
- **Objective:** Implement idle/expanded layout constraints, min/max sizing and content measurement.
- **Acceptance criteria:** No clipping at 100/125/150/200% DPI.
- **Status:** DONE by user decision — [evidence](docs/evidence/sprint-1/FE-03.md), 2026-11-03. Rules, measurement and 27 tests pass, including a 96/120/144/192 DPI matrix. The user accepted this evidence level ("Terima, tandai DONE"): real windows were checked only at the host's 125% scale, and the expanded layout is not reachable in the running app until Sprint 3, so real 100/150/200% on-screen confirmation remains unverified.

**Prompt for AI coding agent**

```text
TASK: FE-03 — Shell layout and adaptive sizing
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and FE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY FE-03 (Sprint 1).
Architecture owner / relevant paths: Lunar.Desktop/Shell.
Functional scope: Implement idle/expanded layout constraints, min/max sizing and content measurement.
Acceptance requirements: No clipping at 100/125/150/200% DPI.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### FE-04 — Island view model and state binding

- **Sprint:** Sprint 1
- **Owner / expected location:** `src/Lunar.Desktop/Shell/IslandViewModel.cs`
- **Objective:** Bind Idle, Expanding, Expanded, Collapsing, Busy and error states to UI without OS calls.
- **Acceptance criteria:** Transitions are deterministic and testable with fake services.
- **Status:** DONE — [evidence](docs/evidence/sprint-1/FE-04.md), 2026-11-03. Presentation state only (`IslandVisualState`, busy flag, inline error); the validated state machine with guards, debouncing and time stays BE-09. Not yet wired to clicks or animation (FE-03 / Sprint 3).

**Prompt for AI coding agent**

```text
TASK: FE-04 — Island view model and state binding
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and FE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY FE-04 (Sprint 1).
Architecture owner / relevant paths: Lunar.Desktop/Shell/IslandViewModel.cs.
Functional scope: Bind Idle, Expanding, Expanded, Collapsing, Busy and error states to UI without OS calls.
Acceptance requirements: Transitions are deterministic and testable with fake services.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### FE-05 — Top-center monitor positioning

- **Sprint:** Sprint 1
- **Owner / expected location:** `src/Lunar.Desktop/Shell/IslandPositionController.cs`
- **Objective:** Position on selected display work area, handle display changes and taskbar placement.
- **Acceptance criteria:** Visible after resolution, scale, monitor removal changes.
- **Carried obligation (user decision 2026-11-03, BE-08):** FE-05 must also produce real hotplug / taskbar-move / DPI-change evidence that `IWindowEnvironment.DisplaysChanged` fires and `GetDisplays` reflects it, and decide whether WPF `DpiChanged` is needed because `WM_DPICHANGED` delivery to the hidden window is unverified.
- **Status:** DONE — [evidence](docs/evidence/sprint-1/FE-05.md), 2026-11-03. Code, 22 automated tests, a real-exe startup check and a user-run display-change check pass (four real scale/resolution changes: Windows raised the notification, WPF `DpiChanged` also fired, the user reported the pill always visible). Still unverified and carried to the Sprint 1 gate (ADR-0005): a second real monitor / mixed DPI, and a work-area change without a DPI change (taskbar auto-hide).

**Prompt for AI coding agent**

```text
TASK: FE-05 — Top-center monitor positioning
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and FE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY FE-05 (Sprint 1).
Architecture owner / relevant paths: Lunar.Desktop/Shell/IslandPositionController.cs.
Functional scope: Position on selected display work area, handle display changes and taskbar placement.
Acceptance requirements: Visible after resolution, scale, monitor removal changes.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### FE-06 — System tray interface

- **Sprint:** Sprint 1
- **Owner / expected location:** `src/Lunar.Desktop/Shell/TrayController.cs`
- **Objective:** Provide tray menu Open, Settings, Start with Windows, Exit and state indicators.
- **Acceptance criteria:** No duplicate icons; exit disposes app; keyboard accessible.
- **Status:** DONE — [evidence](docs/evidence/sprint-1/FE-06.md), 2026-11-03. Manual tray check passed (user-reported). Settings and Start with Windows are shown disabled (FE-23, BE-29); state indicators arrive with later states; no single-instance guard until BE-05.

**Prompt for AI coding agent**

```text
TASK: FE-06 — System tray interface
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and FE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY FE-06 (Sprint 1).
Architecture owner / relevant paths: Lunar.Desktop/Shell/TrayController.cs.
Functional scope: Provide tray menu Open, Settings, Start with Windows, Exit and state indicators.
Acceptance requirements: No duplicate icons; exit disposes app; keyboard accessible.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### FE-07 — Eyes vector artwork

- **Sprint:** Sprint 2
- **Owner / expected location:** `src/Lunar.Desktop/Features/Eyes/EyesView.xaml`
- **Objective:** Build resolution-independent paired eyes with pupil and eyelid geometry.
- **Acceptance criteria:** Crisp at all tested DPI; no external image required.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: FE-07 — Eyes vector artwork
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and FE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY FE-07 (Sprint 2).
Architecture owner / relevant paths: Lunar.Desktop/Features/Eyes/EyesView.xaml.
Functional scope: Build resolution-independent paired eyes with pupil and eyelid geometry.
Acceptance requirements: Crisp at all tested DPI; no external image required.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### FE-08 — Blink animation state machine

- **Sprint:** Sprint 2
- **Owner / expected location:** `src/Lunar.Desktop/Features/Eyes/EyeAnimationController.cs`
- **Objective:** Create randomized but bounded blink scheduling, double blink, idle sleep, pause/resume.
- **Acceptance criteria:** No leaked timers; obey reduced motion; predictable tests with injected time/random.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: FE-08 — Blink animation state machine
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and FE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY FE-08 (Sprint 2).
Architecture owner / relevant paths: Lunar.Desktop/Features/Eyes/EyeAnimationController.cs.
Functional scope: Create randomized but bounded blink scheduling, double blink, idle sleep, pause/resume.
Acceptance requirements: No leaked timers; obey reduced motion; predictable tests with injected time/random.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### FE-09 — Pupil gaze and hover interaction

- **Sprint:** Sprint 2
- **Owner / expected location:** `src/Lunar.Desktop/Features/Eyes`
- **Objective:** Add smoothed pupil follow within bounded range; hover/press feedback; click expands island.
- **Acceptance criteria:** Mouse leave returns neutral; no tracking outside window; smooth without jitter.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: FE-09 — Pupil gaze and hover interaction
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and FE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY FE-09 (Sprint 2).
Architecture owner / relevant paths: Lunar.Desktop/Features/Eyes.
Functional scope: Add smoothed pupil follow within bounded range; hover/press feedback; click expands island.
Acceptance requirements: Mouse leave returns neutral; no tracking outside window; smooth without jitter.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### FE-10 — Expansion and collapse transitions

- **Sprint:** Sprint 3
- **Owner / expected location:** `src/Lunar.Desktop/Shell`
- **Objective:** Animate pill to panel and back with transform/opacity/clip and interruption safety.
- **Acceptance criteria:** Rapid clicks do not corrupt state; reduced motion uses immediate transition.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: FE-10 — Expansion and collapse transitions
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and FE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY FE-10 (Sprint 3).
Architecture owner / relevant paths: Lunar.Desktop/Shell.
Functional scope: Animate pill to panel and back with transform/opacity/clip and interruption safety.
Acceptance requirements: Rapid clicks do not corrupt state; reduced motion uses immediate transition.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### FE-11 — Swiper navigation controller

- **Sprint:** Sprint 3
- **Owner / expected location:** `src/Lunar.Desktop/Navigation`
- **Objective:** Implement three pages with horizontal swipe, wheel optional, arrow buttons, indicators, keyboard left/right.
- **Acceptance criteria:** Focus not stolen from sliders; index stays 0..2; touchpad support tested.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: FE-11 — Swiper navigation controller
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and FE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY FE-11 (Sprint 3).
Architecture owner / relevant paths: Lunar.Desktop/Navigation.
Functional scope: Implement three pages with horizontal swipe, wheel optional, arrow buttons, indicators, keyboard left/right.
Acceptance requirements: Focus not stolen from sliders; index stays 0..2; touchpad support tested.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### FE-12 — Reusable controls and feedback

- **Sprint:** Sprint 3
- **Owner / expected location:** `src/Lunar.Desktop/Controls; Behaviors`
- **Objective:** Create accessible slider, icon button, toggle, status chip, skeleton, tooltip and error inline patterns.
- **Acceptance criteria:** Shared components keyboard navigable and style-consistent.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: FE-12 — Reusable controls and feedback
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and FE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY FE-12 (Sprint 3).
Architecture owner / relevant paths: Lunar.Desktop/Controls; Behaviors.
Functional scope: Create accessible slider, icon button, toggle, status chip, skeleton, tooltip and error inline patterns.
Acceptance requirements: Shared components keyboard navigable and style-consistent.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### FE-13 — System page presentation

- **Sprint:** Sprint 4
- **Owner / expected location:** `src/Lunar.Desktop/Features/System/SystemPage.xaml`
- **Objective:** Build volume/mute, date/time, Wi-Fi and Bluetooth cards bound to read-only ViewModel state and commands.
- **Acceptance criteria:** No Windows API in XAML/code-behind; handles unknown and unsupported.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: FE-13 — System page presentation
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and FE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY FE-13 (Sprint 4).
Architecture owner / relevant paths: Lunar.Desktop/Features/System/SystemPage.xaml.
Functional scope: Build volume/mute, date/time, Wi-Fi and Bluetooth cards bound to read-only ViewModel state and commands.
Acceptance requirements: No Windows API in XAML/code-behind; handles unknown and unsupported.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### FE-14 — Volume slider UX

- **Sprint:** Sprint 4
- **Owner / expected location:** `src/Lunar.Desktop/Features/System/SystemPageViewModel.cs`
- **Objective:** Provide debounced/controlled volume slider, mute indicator and external-change reconciliation.
- **Acceptance criteria:** No feedback loop; user drag not jerked by background refresh.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: FE-14 — Volume slider UX
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and FE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY FE-14 (Sprint 4).
Architecture owner / relevant paths: Lunar.Desktop/Features/System/SystemPageViewModel.cs.
Functional scope: Provide debounced/controlled volume slider, mute indicator and external-change reconciliation.
Acceptance requirements: No feedback loop; user drag not jerked by background refresh.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### FE-15 — Network and Bluetooth UX

- **Sprint:** Sprint 4
- **Owner / expected location:** `src/Lunar.Desktop/Features/System`
- **Objective:** Show connectivity, supported toggles, permission/unsupported messaging, Settings fallback actions.
- **Acceptance criteria:** Never claim toggle succeeded until service confirms; no fake scan UI.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: FE-15 — Network and Bluetooth UX
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and FE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY FE-15 (Sprint 4).
Architecture owner / relevant paths: Lunar.Desktop/Features/System.
Functional scope: Show connectivity, supported toggles, permission/unsupported messaging, Settings fallback actions.
Acceptance requirements: Never claim toggle succeeded until service confirms; no fake scan UI.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### FE-16 — Clock and locale UX

- **Sprint:** Sprint 4
- **Owner / expected location:** `src/Lunar.Desktop/Features/System`
- **Objective:** Show localized date/time with accessible formatting and minute refresh.
- **Acceptance criteria:** Correct time zone, date rollover, 12/24h preference.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: FE-16 — Clock and locale UX
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and FE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY FE-16 (Sprint 4).
Architecture owner / relevant paths: Lunar.Desktop/Features/System.
Functional scope: Show localized date/time with accessible formatting and minute refresh.
Acceptance requirements: Correct time zone, date rollover, 12/24h preference.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### FE-17 — Media page layout

- **Sprint:** Sprint 5
- **Owner / expected location:** `src/Lunar.Desktop/Features/Media/MediaPage.xaml`
- **Objective:** Build artwork, title, artist, source, transport buttons, duration, and empty state.
- **Acceptance criteria:** Long text ellipsized, missing artwork graceful, no session clearly explained.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: FE-17 — Media page layout
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and FE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY FE-17 (Sprint 5).
Architecture owner / relevant paths: Lunar.Desktop/Features/Media/MediaPage.xaml.
Functional scope: Build artwork, title, artist, source, transport buttons, duration, and empty state.
Acceptance requirements: Long text ellipsized, missing artwork graceful, no session clearly explained.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### FE-18 — Media seek slider interaction

- **Sprint:** Sprint 5
- **Owner / expected location:** `src/Lunar.Desktop/Features/Media/MediaPageViewModel.cs`
- **Objective:** Separate displayed playback position from user drag position; commit seek on drag release and handle unsupported seek.
- **Acceptance criteria:** No jumping during drag; HH:MM:SS display; failure restores real position.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: FE-18 — Media seek slider interaction
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and FE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY FE-18 (Sprint 5).
Architecture owner / relevant paths: Lunar.Desktop/Features/Media/MediaPageViewModel.cs.
Functional scope: Separate displayed playback position from user drag position; commit seek on drag release and handle unsupported seek.
Acceptance requirements: No jumping during drag; HH:MM:SS display; failure restores real position.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### FE-19 — Media session switching and feedback

- **Sprint:** Sprint 5
- **Owner / expected location:** `src/Lunar.Desktop/Features/Media`
- **Objective:** Show active session/source and optional selector if adapter exposes multiple; loading/error states.
- **Acceptance criteria:** No stale artwork or commands when session changes.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: FE-19 — Media session switching and feedback
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and FE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY FE-19 (Sprint 5).
Architecture owner / relevant paths: Lunar.Desktop/Features/Media.
Functional scope: Show active session/source and optional selector if adapter exposes multiple; loading/error states.
Acceptance requirements: No stale artwork or commands when session changes.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### FE-20 — Converter intake and format selection

- **Sprint:** Sprint 6
- **Owner / expected location:** `src/Lunar.Desktop/Features/Converter/ConverterPage.xaml`
- **Objective:** Add file picker, drag-drop, input list, output format picker filtered by planner capabilities.
- **Acceptance criteria:** Invalid files explained; file paths not logged in UI diagnostics.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: FE-20 — Converter intake and format selection
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and FE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY FE-20 (Sprint 6).
Architecture owner / relevant paths: Lunar.Desktop/Features/Converter/ConverterPage.xaml.
Functional scope: Add file picker, drag-drop, input list, output format picker filtered by planner capabilities.
Acceptance requirements: Invalid files explained; file paths not logged in UI diagnostics.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### FE-21 — Converter options and fidelity notices

- **Sprint:** Sprint 6
- **Owner / expected location:** `src/Lunar.Desktop/Features/Converter`
- **Objective:** Show quality, alpha background, PDF DPI/pages, page size, DOCX dependency and lossy/reconstruction warnings conditionally.
- **Acceptance criteria:** Options valid per route; disabled routes cannot start.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: FE-21 — Converter options and fidelity notices
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and FE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY FE-21 (Sprint 6).
Architecture owner / relevant paths: Lunar.Desktop/Features/Converter.
Functional scope: Show quality, alpha background, PDF DPI/pages, page size, DOCX dependency and lossy/reconstruction warnings conditionally.
Acceptance requirements: Options valid per route; disabled routes cannot start.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### FE-22 — Conversion queue and progress UX

- **Sprint:** Sprint 6
- **Owner / expected location:** `src/Lunar.Desktop/Features/Converter/ConverterPageViewModel.cs`
- **Objective:** Show per-job status, determinate/indeterminate progress, cancel, retry, save/open output.
- **Acceptance criteria:** Progress never fabricated; collapse does not cancel jobs.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: FE-22 — Conversion queue and progress UX
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and FE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY FE-22 (Sprint 6).
Architecture owner / relevant paths: Lunar.Desktop/Features/Converter/ConverterPageViewModel.cs.
Functional scope: Show per-job status, determinate/indeterminate progress, cancel, retry, save/open output.
Acceptance requirements: Progress never fabricated; collapse does not cancel jobs.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### FE-23 — Settings and preferences UI

- **Sprint:** Sprint 7
- **Owner / expected location:** `src/Lunar.Desktop/Features/Settings (add per ADR)`
- **Objective:** Create settings surface for launch at login, display, offset, theme, motion, privacy, default output and reset.
- **Acceptance criteria:** Persistence via application abstraction; invalid settings recover.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: FE-23 — Settings and preferences UI
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and FE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY FE-23 (Sprint 7).
Architecture owner / relevant paths: Lunar.Desktop/Features/Settings (add per ADR).
Functional scope: Create settings surface for launch at login, display, offset, theme, motion, privacy, default output and reset.
Acceptance requirements: Persistence via application abstraction; invalid settings recover.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### FE-24 — Accessibility and localization

- **Sprint:** Sprint 7
- **Owner / expected location:** `src/Lunar.Desktop/Accessibility; Resources`
- **Objective:** Add automation names, focus order, keyboard navigation, screen-reader status, reduced motion, locale resource strings.
- **Acceptance criteria:** Keyboard-only and screen-reader smoke tests pass.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: FE-24 — Accessibility and localization
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and FE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY FE-24 (Sprint 7).
Architecture owner / relevant paths: Lunar.Desktop/Accessibility; Resources.
Functional scope: Add automation names, focus order, keyboard navigation, screen-reader status, reduced motion, locale resource strings.
Acceptance requirements: Keyboard-only and screen-reader smoke tests pass.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### FE-25 — Performance and lifecycle polish

- **Sprint:** Sprint 7
- **Owner / expected location:** `src/Lunar.Desktop/Shell; Features`
- **Objective:** Suspend nonessential animation on minimized/hidden, detach events, avoid unnecessary layout and UI thread blocking.
- **Acceptance criteria:** Idle CPU/RAM targets measured; no subscription leaks.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: FE-25 — Performance and lifecycle polish
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and FE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY FE-25 (Sprint 7).
Architecture owner / relevant paths: Lunar.Desktop/Shell; Features.
Functional scope: Suspend nonessential animation on minimized/hidden, detach events, avoid unnecessary layout and UI thread blocking.
Acceptance requirements: Idle CPU/RAM targets measured; no subscription leaks.
Before coding: inspect repository and relevant interfaces, identify dependencies/blockers and provide a minimal file-by-file change plan. Reuse existing abstractions; avoid modifying unrelated modules. If an interface is missing, define the smallest contract in the appropriate Application layer and coordinate with the other backlog.
During coding: follow Clean Architecture, MVVM, SOLID, DI, async cancellation, resource disposal, structured typed errors, safe defaults and accessibility where applicable. Do not call OS APIs from Views/ViewModels; do not put presentation concerns into Domain or adapters. Do not add packages without checking license and Windows compatibility.
Verification: add focused automated tests; execute available restore/build/test/static checks; perform Windows integration or manual UI checks when required. If execution environment lacks Windows, explicitly mark integration verification BLOCKED, not passed.
Completion output: (1) task status, (2) files created/changed and reason, (3) implementation summary, (4) actual commands/results, (5) acceptance checklist with evidence, (6) known limitations/risks, (7) next unblocked task. Do not start the next task automatically.
```

### FE-26 — UI regression and release acceptance

- **Sprint:** Sprint 7
- **Expected location:** `tests/Lunar.Desktop.UITests; docs/TEST_REPORT.md`
- **Objective:** Implement UI automation/manual checklist across monitors, DPI, Windows versions and feature failures.
- **Acceptance criteria:** Document evidence and known issues; no unsupported success claims.
- **Status:** TODO

**Prompt for AI coding agent**

```text
TASK: FE-26 — UI regression and release acceptance
PROJECT: Lunar (Windows desktop Dynamic Island).
Read PRD.md, ARCHITECTURE.md and FE_TASK.md completely; use those documents as mandatory contracts.
Implement ONLY FE-26 (Sprint 7).
Architecture owner / relevant paths: tests/Lunar.Desktop.UITests; docs/TEST_REPORT.md.
Functional scope: Implement UI automation/manual checklist across monitors, DPI, Windows versions and feature failures.
Acceptance requirements: Document evidence and known issues; no unsupported success claims.
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
