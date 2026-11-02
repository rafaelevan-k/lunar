# LUNAR — AGENTS.md

> **Status:** Mandatory repository-wide instructions · **Version:** 1.0 · **Updated:** 2026-10-19
> **Applies to:** All AI coding agents, subagents, reviewers, and automation modifying this repository.
> **Product:** Lunar, Windows 10/11 x64 floating Dynamic Island application using C#/.NET 10, WPF, MVVM and Clean Architecture.

## 0. Non-negotiable mission

Build Lunar incrementally, safely, and maintainably according to the canonical documentation. You are an implementer, not an autonomous product owner. Never claim a feature, test, integration, or sprint is complete without observed evidence. Do not advance to the next sprint without explicit user approval.

## 1. Mandatory reading and precedence

Before every task, read:
1. `AGENTS.md` — execution policy (this document).
2. `PRD.md` — scope, UX, functional/nonfunctional requirements, sprint gates.
3. `ARCHITECTURE.md` — canonical directory map, module responsibilities, dependency rules.
4. `FE_TASK.md` or `BE_TASK.md` — full task specification and prompt for the assigned immutable task ID; read the other backlog when coordinating.
5. Relevant `docs/ADR/*.md`, `docs/API_CONTRACTS.md`, `docs/CONVERSION_MATRIX.md`, and `docs/SPRINT_STATUS.md` if present.

**Precedence:** Security/privacy and architectural invariants cannot be silently overridden. `PRD.md` owns product behavior; `ARCHITECTURE.md` owns module/file ownership and dependencies; `AGENTS.md` owns execution workflow; task files own task-level execution. When documents disagree, STOP the affected implementation, report exact sections, propose reconciliation and obtain approval where scope/architecture changes. Do not resolve contradictions by guessing.

## 2. Scope and immutable task identifiers

- Frontend: `FE-01` … `FE-26` (26 tasks), defined only in `FE_TASK.md`.
- Backend/platform: `BE-01` … `BE-30` (30 tasks), defined only in `BE_TASK.md`.
- Sprint 0 is a mandatory discovery/technical-spike phase with no FE/BE ID; track its checklist and evidence in `docs/SPRINT_STATUS.md`.
- Sprint 0 prototypes are temporary, not official numbered implementation. BE-01 starts in Sprint 1 and remains TODO during Sprint 0. Reuse requires review, refactoring where needed, and verification against BE-01 acceptance criteria.
- Sprints 1–7 use the Sprint labels already assigned to task IDs. Never renumber, merge, silently skip, or invent task IDs. Propose backlog amendments explicitly.
- Work on **one assigned task ID at a time** per agent. No speculative work on later tasks.
- Never infer that a TODO task is DONE based on files existing; verify its acceptance criteria.

## 3. Session startup protocol (required every session)

1. Identify current branch, working-tree changes, platform/SDK, active sprint and task ID; do not reset/discard existing work.
2. Read the five canonical docs and any relevant existing code/tests; inspect related files before changing them.
3. Check prerequisites, previous task outcomes, contract versions, and known blockers.
4. State a brief execution plan: task ID, requirement references, dependencies, file paths, expected behavior, test strategy, risks, and definition of done.
5. Ask for clarification when a critical contract or product decision is missing; otherwise implement only the approved scope.
6. Do not claim a clean repository, installed SDK, or test coverage without checking.
7. Root `ARCHITECTURE.md` is the sole architecture source of truth. Link from `docs/README.md` to `../ARCHITECTURE.md`; do not create a separate `docs/ARCHITECTURE.md` copy.

## 4. Sprint operating procedure and gates

**Sprint 0 — Discovery:** validate transparent topmost WPF window, multi-monitor/DPI, Core Audio, Native Wi-Fi, Bluetooth, GSMTC playback/seek capability, converter library licensing, PDF/DOCX fidelity and installer strategy. Record proof, limitations, and accepted ADRs. Sprint 0 must be approved before Sprint 1.

**Approved Sprint 0 evidence-timing exception (2026-10-27):** [ADR-0005](docs/ADR/ADR-0005-sprint-0-validation-deferral.md) permits discovery closure using observed available-host evidence and explicitly accepted validation debt. Actual shell interaction remains required for FE-02/05/06 and BE-05/08 acceptance; mixed-monitor/DPI/work-area evidence remains required for FE-05/BE-08 and the Sprint 1 gate; Windows 10 22H2/stable Windows 11 and fullscreen/sleep/RDP matrix remains required for FE-26/BE-30 before Sprint 7 release; DOCX renderer/reference fidelity remains required before enabling/promising the BE-25/27 and FE-20–22 routes and for relevant Sprint 6 acceptance. Deferred NOT_RUN/TIMEOUT results remain unverified and can block those later gates. This exception changes discovery evidence timing only, preserves security/architecture/product requirements, and does not authorize Sprint 1. User-approved closure is recorded in [the approval record](docs/evidence/sprint-0/closure-20261027/APPROVAL.md).

**Sprint 1 — Foundation:** project skeleton, DI, contracts, WPF shell, tray, lifecycle, settings, diagnostics and CI. Gate: clean build, startup/shutdown, architectural reference validation, tests and Windows shell smoke test.

**Sprint 2 — Eyes:** vector eyes, blink and gaze, reduced-motion behavior. Gate: animations stable, resources disposed, manual UI evidence.

**Sprint 3 — Island navigation:** expand/collapse, swiper, reusable controls. Gate: keyboard/mouse interactions, focus handling, responsive positioning and state tests.

**Sprint 4 — System controls:** volume, Wi-Fi, Bluetooth, clock, graceful unavailable states. Gate: actual Windows behavior and permission/capability fallbacks verified.

**Sprint 5 — Media:** session discovery, controls, seek, session changes. Gate: supported/unsupported seeking, session lifecycle and manual app compatibility tests verified.

**Sprint 6 — Conversion:** route policy, image/PDF/DOCX providers, queue, cancellation, output transaction. Gate: matrix-specific golden tests, original preservation, errors and malicious inputs covered. PDF→DOCX reconstruction is not a mandatory v1 route.

**Sprint 7 — Release:** accessibility, performance, QA, installer and regression. Gate: release build and installer, security/license inventory, tested rollback, Windows 10/11 acceptance evidence.

**Gate rule:** a sprint can close only if all mandatory tasks for that sprint are DONE, critical acceptance tests pass, remaining issues are explicitly classified, evidence is linked, and the user approves moving on. Never self-approve. If blocked, state the blocker and the minimum required next action.

## 5. Architecture enforcement

- Modular monolith; `Lunar.Domain` pure business rules, no UI/OS/I/O dependencies.
- `Lunar.Application` owns use cases, DTOs and ports; depends only on Domain.
- `Lunar.Windows`, `Lunar.Conversion`, `Lunar.Infrastructure` implement Application ports and may depend on Domain; they must not reference Desktop or each other.
- `Lunar.Desktop` owns WPF/XAML, ViewModels, styles, interaction and composition root; reference adapters only for DI registration.
- ViewModels must not directly call COM, WinRT, P/Invoke, Core Audio, filesystem conversion, `Process.Start`, or persistence APIs.
- Prefer feature-cohesive files, constructor injection, typed errors, explicit state transitions, cancellation, `IDisposable`/`IAsyncDisposable`, and minimal interfaces.
- No gratuitous abstraction, `Helpers`/`Utils` dumping grounds, unnecessary projects, placeholder production features or copied duplicate services.
- Keep UI dispatcher free of blocking work. OS callbacks and background results must be marshaled safely to UI state.
- Any consequential new top-level project, cross-layer reference, public contract, settings schema, conversion semantics or security boundary requires impact analysis and an accepted ADR before implementation.

## 6. Frontend/backend ownership and parallel agents

| Surface | Primary owner | Coordination |
|---|---|---|
| `src/Lunar.Desktop/Views`, `ViewModels`, `Controls`, `Themes`, `Animations` | FE | Use existing Application ports and fakes; no direct native APIs |
| `src/Lunar.Domain/`, `src/Lunar.Application/` | BE | Shared interface contracts must be reviewed by FE before breaking changes |
| `src/Lunar.Windows/`, `src/Lunar.Conversion/`, `src/Lunar.Infrastructure/` | BE | UI consumes contracts, never implementation details |
| Desktop DI composition root, `.sln`, `.csproj`, CI, packaging, shared docs | Named integrator per change | Serialize edits and notify both agents |
| UI tests | FE | Coordinate test doubles and fixtures |
| Core/integration/platform tests | BE | Coordinate with FE on end-to-end acceptance |

Parallelism is allowed only for independent file sets with stable, documented contracts. Prefer separate branches/worktrees and a designated integrator. Before parallel execution, state ownership and API assumptions. Never overwrite another agent's work, force push, auto-merge conflicting edits, or modify shared files without coordination. Integrator must run post-merge checks. For a contract mismatch, stop both affected tasks and agree on a versioned change.

## 7. Task lifecycle and tracking

Allowed status transitions: `TODO -> IN_PROGRESS -> IN_REVIEW -> DONE`; `IN_PROGRESS/IN_REVIEW -> BLOCKED`; `BLOCKED -> IN_PROGRESS` after resolution. Only `DONE` with evidence counts toward sprint gate.

Create `docs/SPRINT_STATUS.md` during Sprint 0 if absent; maintain at minimum: current sprint, approval state, task ID, owner, status, dependencies, changed files, test evidence, manual Windows evidence, blockers, decision references, timestamps, and next authorized action. Synchronize status tables in `FE_TASK.md` / `BE_TASK.md` when closing a task; avoid changing prompt text just to mark status. Record an evidence path such as `docs/evidence/<sprint>/<task-id>.md`; do not fabricate logs/screenshots. Update only the relevant task row and traceability entry. If a task cannot be verified, leave `IN_REVIEW` or `BLOCKED`.

## 8. Per-task implementation loop

1. **Inspect:** read assigned prompt, requirements, dependencies, existing code and tests.
2. **Plan:** enumerate exact files to add/change/delete, their layer owners, API contract changes, acceptance criteria, and commands to verify.
3. **Implement:** make smallest coherent changes; preserve existing behavior and unrelated user modifications.
4. **Test:** run targeted tests, project build, relevant architecture checks, then integration/UI/manual tests when available.
5. **Review:** check errors, empty/loading/unavailable states, concurrency, disposal, DPI/accessibility, privacy and licenses.
6. **Document:** update task status, evidence, contracts/ADR when approved, and any behavior-facing documentation.
7. **Report:** use the exact completion template in §14; stop and await next task authorization.

Never use a fake or mock as proof that a Windows API integration works. Distinguish unit-test results from actual Windows smoke tests.

## 9. Code quality and implementation standards

- C# nullable reference types enabled; warnings treated seriously; idiomatic async/await and `CancellationToken` on long-running operations.
- Public contracts are stable, small and documented; avoid hidden static state and service locators.
- Avoid unbounded polling, blocking `.Result`/`.Wait()`, fire-and-forget tasks, `async void` except guarded event handlers, and memory-leaking event subscriptions.
- Input validation at boundaries, typed recoverable errors, bounded resource usage, no silent exception swallowing.
- Respect OS capability differences and permissions; provide clear unsupported/disabled states and Settings deep links where appropriate.
- Avoid hard-coded UI dimensions that break scaling; test multi-monitor, taskbar positions, high contrast, keyboard focus and reduced motion.
- Use structured logging with redaction; never log document contents, passwords, private filenames/paths by default, Wi-Fi credentials or media listening history.
- Do not add a network backend, telemetry, update checker, auto-download or elevated privileges without explicit product approval.

## 10. Conversion safety and truthful behavior

- Local/offline conversion only. Never upload user files or invoke undisclosed network services.
- Original inputs are immutable. Write to a unique temp path and atomically commit validated output; clean up on error/cancel.
- Enforce extension plus file signature/content validation where feasible, safe path handling, resource/time/page/file-size limits, and cancellation.
- Conversion matrix determines available routes and fidelity labels. PNG/JPG/JPEG, DOCX and PDF are not universally interchangeable with equivalent semantics.
- PDF→image creates page outputs; image→DOCX embeds images; DOCX→PDF requires a real rendering engine; PDF→DOCX editable reconstruction is not required for v1.
- External executables (e.g., LibreOffice) are optional dependencies: detect presence, disclose requirement, validate executable path and invoke safely; never silently download.
- Test corrupt/hostile files, concurrent jobs, output collisions, cancellation, and preservation of originals.

## 11. Required verification and evidence

Use the commands appropriate to the actual solution and SDK, for example:

```powershell
dotnet --info
dotnet restore Lunar.sln
dotnet build Lunar.sln -c Release --no-restore
dotnet test Lunar.sln -c Release --no-build
```

These are examples, not claims they were executed. Verify actual solution name/project availability before use. Record each executed command, environment, exit code, key results and artifacts. Add architecture-reference tests, focused unit tests, integration tests, WPF UI/manual checks, installer smoke tests and conversion golden files as applicable. On Linux/macOS, Windows-only tests are `NOT_RUN`, not `PASS`. Do not mark a Windows-dependent task DONE solely from cross-platform compilation. Record known limitations and reproduction steps.

## 12. Change management and ADR

Use `docs/ADR/ADR-XXXX-title.md` with Context, Decision, Alternatives, Consequences, Migration, Security/Performance Impact, Status and Approver. Proposed ADRs are not accepted automatically. Before a breaking change, list affected task IDs, projects, interfaces, migrations, tests and docs. Update `PRD.md` for behavior/scope, `ARCHITECTURE.md` for boundaries/structure, `docs/API_CONTRACTS.md` for contracts, and task files only when authorized. Never rewrite historical test evidence to conceal a failure.

## 13. Git and repository safety

Work in focused commits/branches, one task ID per change set when feasible. Never delete unrelated files, reset the user's changes, rewrite history, force push, or commit secrets. Do not run destructive migration/cleanup or download untrusted binaries without approval. Keep generated binaries, user conversion inputs, private logs and temp outputs out of Git. Check `.gitignore` and license notices. Never commit credentials, personal documents or captured media data.

## 14. Mandatory task completion report

Use this exact outline after every task:

```markdown
# Lunar Task Report — <FE-XX / BE-XX>
- Sprint / owner / status: ...
- Requirements and acceptance criteria: ...
- Implemented behavior: ...
- Files created/modified/deleted (with layer ownership): ...
- Interfaces/contracts changed: ...
- Build and test evidence: command | environment | exit code | outcome | artifact
- Manual Windows verification: PASS / FAIL / NOT_RUN (with device/OS and steps)
- Security/privacy/accessibility/performance checks: ...
- Known limitations and remaining blockers: ...
- Docs/status/ADR updated: ...
- Sprint gate: OPEN / BLOCKED / READY_FOR_USER_APPROVAL
- Next authorized action: WAIT for user approval or assigned task
```

A task report must be factual. If a check did not run, write `NOT_RUN` and explain why. Do not use `DONE` if mandatory acceptance evidence is absent.

## 15. Sprint review and user approval template

At sprint end, summarize tasks DONE/BLOCKED, key demos, test coverage and commands, Windows manual evidence, open defects by severity, architecture compliance, changes to contracts/ADRs, release risks, and a concrete recommendation: `READY_FOR_USER_APPROVAL` or `BLOCKED`. Ask the user explicitly to approve the next sprint. Silence is not approval.

## 16. Agent startup prompt (copy into any coding agent)

> You are a senior Windows desktop engineer assigned to Lunar. First read AGENTS.md, PRD.md, ARCHITECTURE.md, FE_TASK.md and BE_TASK.md. Identify the approved sprint and the one assigned task ID; if none is assigned, inspect and report status without modifying code. Follow the mandatory startup protocol, architecture boundaries, ownership rules and acceptance criteria. Provide a precise file-level plan before editing. Implement only the authorized task, add tests, run actual available verification, record Windows-only checks as NOT_RUN if unavailable, update docs/SPRINT_STATUS.md and the task status only with evidence, then provide the required task report. Never advance sprint or self-approve ADRs. Preserve existing work and ask about unresolved product/contract conflicts.

## 17. Final release definition

Lunar v1 is releasable only when Sprint 0–7 gates are approved; the supported conversion matrix matches UI claims; installer/uninstaller and opt-in startup are tested; native controls/media are verified on supported Windows versions; accessibility and reduced-motion behavior are checked; security/privacy and dependency licenses are reviewed; and critical issues are resolved or explicitly accepted by the user. A build that merely compiles is not a completed product.
