# ADR approval record

- Approval date: 2026-10-27, Asia/Jakarta
- Approver: User in this conversation; recorded by Codex, not self-approved
- User response (verbatim): “saya setuju”
- Context: the preceding assistant explicitly asked whether the user approved the technical choices in ADR-0001–0004, distinguishing ADR acceptance from Sprint 0 closure and BE-01 authorization.
- Result: ADR-0001, ADR-0002, ADR-0003 and ADR-0004 changed from Proposed to Accepted. [Approved decision index](../../../ADR/README.md).
- Reviewed basis: the four existing concrete decisions and their conditions; before-edit SHA-256 values recorded in `before.json`. Decision bodies/validation sections were not rewritten as part of acceptance.

## Scope retained

The approval records the WPF/.NET baseline, Windows capability/contract-normalization strategy, version-specific converter provider strategy and packaging strategy. It does not approve an exact production API, waive missing validation, grant native redistribution clearance, accept WiX EULA/fees, install software, close Sprint 0 or start Sprint 1/BE-01.

Outstanding Sprint 0 evidence remains target OS/manual shell/multi-monitor and DOCX rendering/fidelity. Native component/license applicability and PNG DPI metadata remain explicit follow-up conditions. No failed test evidence, earlier report or source code was changed. All 56 FE/BE tasks remain TODO.

## Verification scope

Documentation-only consistency/link checks and source/backlog preservation checks are recorded in `verification.log`. Build, native API probes, converters and manual Windows tests are NOT_RUN for this approval-recording action; no new technical validation result is claimed. SDK version check returned 10.0.402; Git branch/status discovery still reports no Git repository.
