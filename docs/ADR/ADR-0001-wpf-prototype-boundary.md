# ADR-0001: WPF baseline and temporary prototype boundary

- Status: Accepted
- Date: 2026-10-19
- Approver: User (explicit approval in this conversation)
- Accepted: 2026-10-27 (Asia/Jakarta)
- Approval scope: Technical strategy and stated conditions only; no Sprint 0 closure or Sprint 1 authorization
- Revised: 2026-10-19 after converter discovery continuation

## Context
PRD §2/9 and ARCHITECTURE §17 authorize a temporary .NET 10 WPF feasibility spike. System-wide SDK is 9.0.306; no initial solution or source exists. User approved clarifying Sprint 0 vs BE-01 and canonical documentation location, not a new architecture.

## Decision
Select C#/.NET 10, WPF, MVVM and the six canonical layers already specified. Use `Lunar.sln` for official implementation, the canonical architecture's test-project names and inward reference map. Pin SDK 10.0.402, nullable analysis and deterministic builds; Desktop remains WPF and architecture fitness tests must enforce references. Do not generate the solution until BE-01 is explicitly authorized.

Treat all `.Spike.csproj` executables and converter fixtures as disposable discovery work. They are not production adapters, a task-completion shortcut, or an accepted public contract. Constructor injection and JSONL are adequate for these experiments; production DI/logging remain BE-04/BE-07. Reuse only after review/refactoring and the assigned task's acceptance tests. No worker process, web backend, telemetry or runtime auto-downloader is selected.

Keep the PRD Windows 10 22H2 / Windows 11 x64 compatibility target. Current build 26300 evidence does not validate that target matrix or establish Microsoft's servicing support for every Windows edition. Do not change or advertise OS support based on a successful compile; compatibility and vendor lifecycle remain explicit verification risks.

## Alternatives
WinUI 3 adds packaging/window constraints requiring separate evidence. Full production scaffold now would prematurely implement BE-01. A .NET 9 build would not prove the required target.

## Consequences
Some spike code may be discarded. Prototype build/lifecycle evidence is useful but does not prove settings, production DI, shell states, UI accessibility or architecture reference tests.

## Migration
Affected: BE-01/04/05/08 and FE-02/05/06; no current production interface/schema migration. BE-01 must replace or deliberately promote `.Spike.csproj` projects, enforce inward references, add actual tests and build setup; no automatic copying.

## Security/Performance Impact
No elevation, persistent startup or background networking. Performance budgets and all Windows/DPI acceptance cases remain measured gates, not consequences inferred from WPF selection.

## Validation
Reuse the original successful Desktop/Windows build and runtime evidence in [SPIKE_RESULTS](../SPIKE_RESULTS.md). Those prototypes were neither edited nor rerun during this continuation; their recorded source hashes are checked in the continuation verification. New converter harness Release build succeeds using the same SDK; see [CONVERTER_DISCOVERY](../CONVERTER_DISCOVERY.md).

Unverified: target OS matrix, mixed-monitor shell behavior and full production DI/lifecycle/architecture tests. The first two are discovery acceptance gaps; the production tests belong to their numbered tasks. Acceptance of this ADR records the technology/prototype-promotion decision only. It does not close Sprint 0 or start BE-01.

## Acceptance record
Accepted by the user on 2026-10-27 in response to the explicit request to approve ADR-0001–0004. Decision text and validation limitations are unchanged. See the [approval record](../evidence/sprint-0/adr-acceptance-20261027/APPROVAL.md).
