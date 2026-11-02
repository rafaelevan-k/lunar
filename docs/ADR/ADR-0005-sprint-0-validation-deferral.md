# ADR-0005: Sprint 0 validation deferral for unavailable environments

- Status: Accepted
- Date: 2026-10-27, Asia/Jakarta
- Approver: User (explicit approval in this conversation)
- Accepted: 2026-10-27, Asia/Jakarta
- Approval record: [Sprint 0 closure](../evidence/sprint-0/closure-20261027/APPROVAL.md)
- Scope: Sprint 0 exit evidence timing only; Sprint 1 remains separately authorized

## Context

The user requested completion of Sprint 0 and confirmed that no Windows 10 22H2 VM/device, stable Windows 11 test environment or second monitor is available. Before this amendment, PRD §9 Sprint 0 required window startup on Win10/11; AGENTS §4 required multi-monitor/DPI and PDF/DOCX fidelity discovery. The exit review consequently blocked closure. Available-host Release builds, native read-only probes, WPF runtime/render and synthetic image/PDF/DOCX structure have actual evidence. These do not satisfy the missing environment/manual/fidelity cases.

On 2026-10-27, repeat DOCX reference automation opened the fixture and computed pagination, but timed out in `ExportAsFixedFormat`; no reference PDF was produced. Optional LibreOffice remains undetected in the recorded discovery. The UI tool did not expose a targetable prototype window, so actual mouse/tray/focus behavior is still NOT_RUN. See [current results](../SPIKE_RESULTS.md#completion-attempt--2026-10-27).

## Decision

Permit closing **discovery only**, using the available-host evidence and explicit acceptance of the deferred items below. Never convert NOT_RUN/TIMEOUT into PASS. Keep all supported-OS, UI, security, conversion-fidelity and release requirements intact.

| Deferred evidence | Accountable existing tasks / required deadline | Consequence until verified |
|---|---|---|
| Actual shell mouse nonactivation, outside hit testing, tray Open/Exit, keyboard focus on available Windows | FE-02/05/06, BE-05/08; relevant task cannot be DONE without its acceptance evidence; mandatory Sprint 1 shell smoke gate stays intact | Runtime properties/rendering are the only available proof; no claim interactive behavior works |
| Mixed monitors, DPI 100/150/200%, work-area changes/hotplug | FE-05 and BE-08 before relevant task/Sprint 1 acceptance; complete regression in FE-26/BE-30 | Relevant task remains IN_REVIEW/BLOCKED if environment remains unavailable; no waiver there |
| Windows 10 22H2 and stable supported Windows 11 matrix, fullscreen/sleep/RDP | FE-26/BE-30 before Sprint 7 release approval; earlier feature gates still require available Windows integration/manual proof | No verified compatibility claim or release approval without actual target evidence |
| DOCX layout/reference and LibreOffice-versus-reference fidelity | Follow-up discovery before enabling/promising DOCX rendering in BE-25/BE-27/FE-20–22; required for relevant Sprint 6 acceptance | Missing dependency routes remain DependencyUnavailable; Word stays reference-only; no hidden replacement or download |

Native component redistribution clearance, WiX terms/eligibility/signing, hostile-input containment, real device writes/media seeking and installer testing retain their existing later-task holds; this decision does not waive them. The new PNG resolution experiment resolves only synthetic sRGB metadata at the current host scale.

The user explicitly accepted this evidence-timing change and Sprint 0 closure on 2026-10-27: “ya saya setuju, dan tutup sprint 0 dengan kondisi yg saat ini masih terbatas”. Earlier silence, the prior four ADR approvals and asking whether extra hardware was needed were not approval. Starting Sprint 1 or assigning BE-01 remains a separate instruction.

## Alternatives

Keeping Sprint 0 BLOCKED until all environments and a functioning renderer/reference were supplied was the prior policy. Another option was temporary lab/VM access. Buying hardware, installing a new OS/renderer or changing system display/privacy settings is not assumed authorized or required by this decision.

## Consequences

Discovery can close with accepted, tracked validation debt. Some Sprint 1/6 tasks can still become blocked by exactly these missing environments; deferral moves the deadline, it does not provide missing evidence. A false compatibility/fidelity claim remains prohibited. All 56 numbered tasks remain TODO at closure.

## Migration / approved documentation changes

No code, dependency, public contract, settings, route, project boundary or task ID changes.

1. PRD §9, Sprint 0 exit: qualify Win10/11 startup as the **available Windows host** discovery check; link this accepted decision and require the unchanged full target matrix before release. Add the four explicit deferred-evidence deadlines above. PRD §7/13/16 and the Sprint 1/6/7 gates stay in force.
2. AGENTS §4, Sprint 0: retain feasibility discovery and accepted ADRs; add this explicitly approved exception for evidence timing, linking the four deadlines. Keep the gate rule and later sprint requirements unchanged.
3. ARCHITECTURE §17 discovery mapping remains unchanged; root remains the sole architecture source. No duplicate architecture document.
4. Update ADR index, SPRINT_STATUS, SPRINT_0_EXIT_REVIEW, COMPATIBILITY, DEPENDENCY_REVIEW and RISK_REGISTER with the approval record, residual NOT_RUN/TIMEOUT results, deadlines and Sprint 0 closure only after approval and final consistency checks. Retain historical raw logs.

## Security/Performance Impact

No boundary is relaxed: no user documents, uploads, credentials, radio/media writes, elevation, renderer installation, EULA acceptance or runtime network conversion. No production performance or accessibility PASS is inferred. Existing resource/safety/license requirements remain mandatory in their assigned tasks.

## Validation

Observed evidence: [Windows/Desktop commands](../evidence/sprint-0/20261026T195334Z/commands.jsonl), [converter commands](../evidence/sprint-0/continuation-20261026T195608Z/commands.jsonl), [Word outcome](../evidence/sprint-0/continuation-20261026T195608Z/word-reference-result.json). Accepting this decision records the evidence-timing amendment and discovery closure; it creates no new technical test result or Sprint 1 authorization.

## Acceptance and closure record

Accepted by the user on 2026-10-27 together with Sprint 0 discovery closure under the limitations above. The migration is documentation/evidence-check maintenance only; no new technical PASS, later-task completion, production implementation, EULA acceptance or Sprint 1 authorization is inferred. The original technical logs are preserved. See [approval and verification](../evidence/sprint-0/closure-20261027/APPROVAL.md).
