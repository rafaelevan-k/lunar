# Sprint 0 exit review

Prepared: 2026-10-19; closed: 2026-10-27, Asia/Jakarta. Owner/integrator: Codex. User accepted ADR-0001–0005 and explicitly approved Sprint 0 closure under the current limitations; [closure approval](evidence/sprint-0/closure-20261027/APPROVAL.md). No Sprint 1 progression was approved and no new technical validation is implied by recording closure.

**Gate outcome: READY_FOR_USER_APPROVAL → user approved → CLOSED with accepted limitations.** Sprint 0 discovery is DONE under accepted ADR-0005 and the synchronized AGENTS §4 / PRD §9 evidence-timing exception. Target/manual shell and DOCX fidelity remain unverified, with named later-task deadlines; Sprint 1/BE-01 remains unauthorized.

## Current completion attempt — 2026-10-27

New evidence is in [SPIKE_RESULTS](SPIKE_RESULTS.md#completion-attempt--2026-10-27). All three spike projects now have repeat Windows Release builds, zero warnings/errors. Actual native read probes and WPF instrumented smoke pass; converter generator passes, including four separate PNGs with requested density, unchanged geometry and full decoded pixel equality. Normalized 150-DPI pages visually reviewed. Metadata feasibility is resolved for the current synthetic sRGB/125% host case only; older metadata observations below remain historical.

Word reference opens the synthetic DOCX and computes pagination, then export stalls for 60 seconds. No reference PDF, exact owned process cleaned, source unchanged. This is a failed reference experiment, not DOCX fidelity proof. Repeated UI discovery found no targetable prototype window; actual focus/tray/hit testing remains NOT_RUN. Session-owned interactive launches were cleaned up.

The user confirmed no extra target OS environment or second monitor, then explicitly accepted [ADR-0005](ADR/ADR-0005-sprint-0-validation-deferral.md) and instructed closure: “ya saya setuju, dan tutup sprint 0 dengan kondisi yg saat ini masih terbatas”. The evidence-timing amendment is now effective; it preserves existing-task deadlines and feature/release requirements. There is no new compatibility/fidelity PASS or Sprint 1 authorization. ADR-0001–0004 are not reopened. No numbered task is DONE (56 TODO).

## Deferred evidence and unchanged later gates

| Open evidence | Required owner/task acceptance deadline |
|---|---|
| Actual mouse/focus/hit testing/tray interaction | FE-02/05/06, BE-05/08; relevant acceptance and Sprint 1 shell smoke |
| Mixed monitors, 100/150/200% DPI, work-area/hotplug | FE-05/BE-08 before relevant task/Sprint 1 acceptance; regression FE-26/BE-30 |
| Windows 10 22H2/stable Windows 11 and fullscreen/sleep/RDP | FE-26/BE-30 before Sprint 7 release; earlier available-Windows proof still mandatory |
| DOCX renderer/reference comparison | Follow-up discovery before enabling/promising BE-25/27, FE-20–22 rendering and relevant Sprint 6 acceptance |

Native redistribution applicability, WiX terms/signing, actual system/media controls and full conversion/security/installer acceptance remain their original later-task holds. A missing environment can still block those tasks. Only discovery evidence timing changed. No release recommendation is made.

The sections below preserve the pre-closure discovery review, including its former BLOCKED recommendation and dated technical results. Current closure and deadlines are above; no old failure/NOT_RUN has been relabeled PASS.

## Work and demos with evidence

| Deliverable | Evidence / result | Status |
|---|---|---|
| Five canonical docs and approved clarification | Root architecture remains canonical; BE-01 Sprint 1 TODO; prior consistency checks | Verified; no further canonical scope changes this continuation |
| Original WPF window/tray/build and read-only native APIs | Reuse [original results](SPIKE_RESULTS.md), observed on build 26300 at 125% | Verified for recorded scope only; not repeated |
| New converter harness | [Release build](evidence/sprint-0/converter-20261019/build-attempt-7.log), [actual experiments](evidence/sprint-0/converter-20261019/generate-run-05.jsonl) | Feasibility assertions pass; no production feature/task completion |
| Image/PDF demo | Alpha/Unicode PNG, JPEG/orientation, two-page image/vector PDF rendered at 150/300 pixel geometry, 100-page open | Scoped PASS with [hashes](evidence/sprint-0/converter-20261019/fixture-output-manifest.json) and [visual/pixel review](evidence/sprint-0/converter-20261019/raster-review.json) |
| DOCX demo | Exact image embedding, zero schema errors, source preserved | Structure PASS; layout/fidelity unverified |
| Dependency/license review | Five package signatures pass; no NuGet advisories reported at query; exact native aggregate notices preserved | Initial review complete for discovery; native binary-specific redistribution obligations unresolved |
| Four ADRs | [ADR index and selections](ADR/README.md), approval recorded 2026-10-27 | Four Accepted by user; technical strategy/conditions only |
| Risk/evidence classification | This review, CONVERTER_DISCOVERY, COMPATIBILITY, SECURITY | Documented, no implicit acceptance of risks |

Numbered task counts: **0 DONE, 56 TODO** (26 FE / 30 BE), including BE-01 TODO. There are no assigned numbered tasks to close in Sprint 0; checklist evidence cannot mark a later task DONE.

## Actual commands and test coverage

New work used the existing SDK 10.0.402; only `Lunar.Conversion.Spike.csproj` was restored/built. Last build: exit 0, zero warnings/errors. Successful converter process: exit 0, 1,795 ms, peak working set 171,008,000 bytes on this host; neither value is a release/idle budget assertion. Package verification: exit 0 for each of five packages. Advisory query: exit 0, no known vulnerable package reported by current sources. Read-only pixel/metadata assertions: exit 0.

All earlier failures remain visible: obsolete Skia API build, DOCX child-order schema error, incorrect PDF pixel assumptions, two reference-harness Word errors and final 60-second Word timeout. Corrected converter cases have later passing evidence; no DOCX rendering PASS is claimed. Details/commands/logs: [CONVERTER_DISCOVERY](CONVERTER_DISCOVERY.md).

Historical 2026-10-19 [verification](evidence/sprint-0/converter-20261019/verification.log) confirmed all 13 recorded original source/project/runner hashes matched, all 56 tasks were TODO, four ADRs were Proposed at that time, the converter had no project references and all 11 successful synthetic fixture/output hashes matched. Locked restore passed without changing dependency resolution. Those logs are unchanged; the 2026-10-27 approval changes only decision status. No new compatibility/fidelity PASS is inferred.

No production unit/integration/UI suite, architecture-fitness suite, installer test or full conversion golden corpus was executed. Their absence is not misreported as failing tests. Such suites belong to their assigned numbered tasks; current executable assertions establish feasibility only.

## Unresolved items by decision/gate

| Item | Priority / classification | Evidence and minimum next action |
|---|---|---|
| Windows 10 22H2 / stable Windows 11 shell startup and interaction | P1, missing Sprint 0 environment evidence | Provide supported target environments and record startup, actual nonactivation, hit testing and tray exit; build 26300 alone does not satisfy both targets |
| Mixed-monitor/DPI/taskbar/fullscreen behavior | P1, missing discovery evidence | Targeted new interaction cases at 100/150/200%, mixed monitors, taskbar/work-area changes and fullscreen. Retain original smoke proof; do not rerun it as substitute |
| DOCX rendering/fidelity | P1, missing Sprint 0 experiment | Supply/locate a consented LibreOffice/reference environment or diagnose the guarded Word reference timeout; render synthetic fixture, inspect every page, compare against a valid independent reference. No silent renderer install |
| Native Skia component/license applicability | P1, hold before redistribution | Exact aggregate notices include varied/alternative and LGPL/eCos terms. Establish applicability to tested win-x64 DLL, preserve required notices/source obligations and approve redistribution before shipping. MIT package field/signatures alone are insufficient |
| PNG DPI metadata differs from pixel geometry | P1, known provider correctness gap before BE-24 acceptance | Pixels normalize correctly at this host DPI, encoded PNG density remains ~120. Normalize metadata; test physical sizing and other DPI/OS/rotation/crop cases in provider implementation. Not a fully working production renderer |
| WiX terms and signing | P1, pre-tooling/release condition | Owner reviews 7.0.0 OSMF/EULA/eligibility before tool installation/use; certificate availability unknown. No EULA was accepted or installer created |
| Full hardware writes/media seek/lifecycle | Unverified feature risk, Sprint 4/5 acceptance | Later BE-10–18/FE-14–19 must test actual writes, events, denial/absence, live/seekable sessions and closure. Read-only capability snapshots are not control proof |
| Encryption/cancellation/caps/100-page rasterization/collisions/disk-full/security corpus | Unverified feature risk, Sprint 6/7 acceptance | Later conversion tasks must prove these properties; 100-page opening is not 100-page pipeline acceptance |
| Install/upgrade/uninstall/startup/rollback, full accessibility/performance | Release work, Sprint 7 | Execute BE-28–30/FE-24–26 acceptance later; these full tests are not mandatory Sprint 0 implementations |
| No Git metadata | Workflow limitation | Branch/clean-tree claims unavailable. Address repository setup when assigned; not a converter/API feasibility failure |

Prior tracking used BLOCKED too broadly. This review distinguishes missing mandatory discovery evidence from unresolved risks that must be accepted/classified now and verified before later feature/release acceptance. No new backlog ID, dependency, acceptance criterion or product route was introduced.

Resolved governance item: user acceptance of four ADRs was recorded on 2026-10-27. Remaining evidence/redistribution/tooling conditions above are retained; acceptance does not convert NOT_RUN results into PASS or accept third-party EULAs.

## Architecture, privacy and release implications

No production public contract or project reference was added; no Domain/Application implementation, service locator, backend, telemetry, startup registration or renderer auto-downloader exists. The disposable CLI combines experimental assertions; ADR-0003 requires the production Windows PDF engine to implement an Application port, while Conversion coordinates through that port, with no adapter-to-adapter dependency. Exact signatures still require review in BE-04/BE-24.

Only synthetic fixtures were processed. Original synthetic PNG/PDF/DOCX hashes are preserved. Word automation was limited to a proven new instance, macro-disabled, read-only fixture access and a bounded outer timeout; verified owned instances were cleaned up. Native/reflection/API success is separated from manual evidence. Framework development restore had network access; converters did not upload files or invoke network conversion services.

No critical product defect is asserted because no production product exists. The DPI metadata discrepancy is an observed experiment finding; other priority items are acceptance gaps/risks, not invented failing integrations. Signing, native licensing, untrusted-input containment, target compatibility and renderer fidelity remain release risks.

## Approval record and next authorized action

Four ADRs are **Accepted** by the user, with their reviewed limitations and conditions intact. No repeated request for this approval is needed. Complete missing Sprint 0 environment/rendering evidence; any proposed mandatory-gate change still requires an explicit canonical-doc decision rather than a silent waiver.

Current handoff: Sprint 0 closure is approved and recorded in [the closure evidence](evidence/sprint-0/closure-20261027/APPROVAL.md). WAIT for separate task/Sprint 1 authorization. **BE-01 and all Sprint 1 work remain unauthorized and TODO.**
