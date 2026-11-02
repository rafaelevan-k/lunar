# User approval — ADR-0005 and Sprint 0 closure

- Date: 2026-10-27, Asia/Jakarta
- Approver: User, explicit message in this conversation
- Prior request: approve ADR-0005 and close Sprint 0 with unavailable evidence deferred to the named task deadlines; Sprint 1 remains separately authorized.
- Exact response: “ya saya setuju, dan tutup sprint 0 dengan kondisi yg saat ini masih terbatas”
- Decision: ADR-0005 Accepted; Sprint 0 discovery DONE and CLOSED under that evidence-timing amendment.
- Next authorization: WAIT for separately assigned task/Sprint 1 approval. BE-01 and all 56 numbered tasks remain TODO.

## Accepted limitations and unchanged deadlines

| Evidence still missing | Required later acceptance |
|---|---|
| Actual shell mouse nonactivation/hit testing/tray/keyboard | FE-02/05/06, BE-05/08; relevant task acceptance and mandatory Sprint 1 shell smoke |
| Mixed monitors, DPI 100/150/200%, work-area changes/hotplug | FE-05/BE-08 before relevant task/Sprint 1 acceptance; complete regression FE-26/BE-30 |
| Windows 10 22H2/stable Windows 11 matrix, fullscreen/sleep/RDP | FE-26/BE-30 before Sprint 7 release; earlier feature gates retain actual available-Windows proof |
| DOCX renderer/reference and LibreOffice-versus-reference fidelity | Follow-up discovery before enabling/promising BE-25/27 and FE-20–22 DOCX rendering; relevant Sprint 6 acceptance |

NOT_RUN remains NOT_RUN. The Word reference TIMEOUT remains a failed export experiment; no DOCX fidelity PASS. No new target hardware/OS, compatibility, native write/media seek, accessibility/performance, security corpus or installer result is asserted. No native redistribution clearance or WiX EULA/fee is accepted by this closure. Later tasks may still block if their acceptance evidence is unavailable.

## Observed technical basis (existing results, not rerun for closure)

- [Windows/Desktop Release builds and runner](../20261026T195334Z/commands.jsonl): exit 0; both builds zero warnings/errors; actual read probes and WPF runtime smoke recorded.
- [Converter commands](../continuation-20261026T195608Z/commands.jsonl): locked restore/build/generator exit 0; synthetic assertions and four PNG density/pixel checks pass.
- [Word result](../continuation-20261026T195608Z/word-reference-result.json): TIMEOUT 60 seconds during export, no PDF, owned process cleaned, source unchanged.
- [Prior evidence/static verification](../continuation-20261026T195608Z/verification-handoff.log): exit 0 for recorded scope; not manual/target/fidelity acceptance.

## Closure action and verification

Docs-only acceptance/status/gate changes plus maintenance of the evidence checker for the accepted ADR state. No production source/package/contract change. Startup inspection again found no Git repository; branch/clean-tree claims unavailable. System SDK remains 9.0.306; portable version command returned 10.0.402. No restore/build/API/UI/converter rerun during this closure action.

Preservation baseline: [132 source/project/backlog/architecture/runner/historical evidence hashes](preserved-before.json). Pre-edit documentation/checker hashes: [documents-before.json](documents-before.json). Executed closure verification and exit codes: [commands](commands.jsonl), [final checks](verification-final.log), [changed-file inventory](changed-files.json). The [first checker run](verification.log) rejected different shorthand for the same FE-05/BE-08 deadline; its comparison was corrected and failure retained. These checks validate documentation consistency and evidence preservation, not missing integration tests.
