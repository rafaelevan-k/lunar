# Accepted Sprint 0 decisions

ADR-0001–0004 are **Accepted** by the user on **2026-10-27 (Asia/Jakarta)**, following the explicit request to approve these four technical choices. [Approval record](../evidence/sprint-0/adr-acceptance-20261027/APPROVAL.md). Sprint 0 closure, BE-01, exact production contracts, native redistribution clearance and WiX EULA acceptance remain separate gates.

Subsequently on 2026-10-27, the user also accepted **ADR-0005** and explicitly closed **Sprint 0 discovery with current limitations**. [Closure approval](../evidence/sprint-0/closure-20261027/APPROVAL.md). BE-01/Sprint 1, full production contracts, redistribution clearance, tooling terms and deferred validation remain separate obligations.

The concrete decision package was prepared on 2026-10-19; acceptance was recorded on 2026-10-27 without changing the reviewed decision bodies or their validation conditions. [Evidence](../CONVERTER_DISCOVERY.md) and [exit review](../SPRINT_0_EXIT_REVIEW.md) distinguish proven feasibility, missing discovery evidence and later release work. Prior test/advisory snapshots retain their original dates and have not been rerun or reclassified as passing.

| ADR | Accepted selection | Approval boundary |
|---|---|---|
| 0001 | .NET 10/WPF/MVVM, canonical six layers, `.sln`, disposable spikes | No BE-01 or automatic prototype promotion |
| 0002 | Core Audio / Native Wi-Fi / Radios / GSMTC; truthful capability states and normalized port strategy | Exact public contracts still require BE-04 review; no native write/seek success inferred |
| 0003 | SkiaSharp 4.153.1, Open XML 3.5.1, Windows PDF rasterization; optional external LibreOffice | No final redistribution clearance; DPI metadata and DOCX fidelity remain explicit holds |
| 0004 | WiX SDK 7.0.0 per-user MSI, one self-contained win-x64 multi-file payload | Tooling terms not accepted; no install/publish/release tests run |
| 0005 | Discovery closure with accepted environment/manual/DOCX validation deferral | Named Sprint 1/6/7 deadlines remain; no missing test becomes PASS and no Sprint 1 start |

- [ADR-0001: baseline and prototype promotion](ADR-0001-wpf-prototype-boundary.md)
- [ADR-0002: capability probes and contracts](ADR-0002-windows-capabilities.md)
- [ADR-0003: conversion strategy](ADR-0003-conversion-strategy.md)
- [ADR-0004: packaging strategy](ADR-0004-packaging-strategy.md)
- [ADR-0005: Accepted Sprint 0 validation deferral](ADR-0005-sprint-0-validation-deferral.md) — user approved the amendment and discovery closure; no Sprint 1 authorization.
- [ADR-0010: Display environment port, placement rules and settings launcher](ADR-0010-display-environment-port.md) — **Accepted** by the user 2026-11-03 for BE-08.
- [ADR-0011: Settings model, repository port and JSON store](ADR-0011-settings-store.md) — **Accepted** by the user 2026-11-03 for BE-06.
- [ADR-0009: Structured diagnostic logging](ADR-0009-diagnostic-logging.md) — **Accepted** by the user 2026-11-03 for BE-07.
- [ADR-0008: Single-instance coordination port and lifecycle policy](ADR-0008-single-instance-coordination.md) — **Accepted** by the user 2026-11-03 for BE-05.
- [ADR-0007: Non-activating window style exception](ADR-0007-non-activating-window-style.md) — **Accepted** by the user 2026-11-03 for FE-02.
- [ADR-0006: Application port contract semantics](ADR-0006-application-port-contracts.md) — **Accepted** by the user 2026-11-03 for BE-04.

Technical continuation on 2026-10-27 repeated builds/probes and demonstrated synthetic PNG density normalization; Word export and interactive/target-matrix evidence remain incomplete. [Current results](../SPIKE_RESULTS.md#completion-attempt--2026-10-27). The accepted fifth decision changes evidence timing only; the four earlier decision bodies and historical evidence are preserved.

These filenames follow AGENTS §12. Architecture's older illustrative `0001-...` filenames are examples, not additional files to duplicate.
