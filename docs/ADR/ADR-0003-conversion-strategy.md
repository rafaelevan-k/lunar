# ADR-0003: Local conversion strategy and provider selection gate

- Status: Accepted
- Date: 2026-10-19
- Approver: User (explicit approval in this conversation)
- Accepted: 2026-10-27 (Asia/Jakarta)
- Approval scope: Version-specific provider strategy with existing conditions; no final redistribution clearance or validation waiver
- Revised: 2026-10-19 after real synthetic conversion experiments

## Context
PRD §5 defines route semantics. Synthetic image/PDF experiments and DOCX embedding now have actual evidence, but no LibreOffice/Word fidelity comparison has completed. Exact package graph, hashes, signatures and advisory snapshot are recorded in [converter evidence](../CONVERTER_DISCOVERY.md). No production conversion task has started.

## Decision
Select the following strategy for later implementation, subject to the validation conditions stated here:

| Responsibility | Selected approach | Evidence and remaining limits |
|---|---|---|
| PNG/JPEG decoding/encoding, alpha and orientation | SkiaSharp 4.153.1 / Win32 native assets 4.153.1 | Exact-pixel PNG roundtrip, white alpha composition, EXIF orientation 6 and one corrupt image tested; ICC/other orientations/hostile inputs not verified |
| Image-page PDF creation | SkiaSharp 4.153.1 PDF writer | Two-page image/vector fixture rendered correctly; avoids adding a second PDF creator with uncertain license eligibility |
| PDF page rasterization | Windows.Data.Pdf | Four page renders at calibrated 150/300 pixel geometry on host 125% DPI; requires explicit DIP normalization, output pixel verification and PNG resolution-metadata normalization. Other DPI/OS/rotation/crop/encryption cases unverified |
| Image-containing DOCX | Open XML SDK 3.5.1 | Schema errors zero, embedded PNG bytes exact, no external relationships; layout unverified |
| DOCX layout to PDF then images | Optional separately installed LibreOffice | Do not bundle/download or silently substitute Word. Not detected via default paths, PATH or App Paths. DependencyUnavailable must disable/explain these routes; renderer version and fidelity require a follow-up spike before promising availability |
| PDF→DOCX | Disabled in v1 | No OCR/reconstruction provider selected |

Keep JPG/JPEG one encoding; images→DOCX means embedding and PDF→images means per-page rasterization. All processing remains local and originals read-only.

Production ownership remains the existing architecture: native Windows PDF calls belong in a Windows adapter implementing an Application-owned rasterization port. `Lunar.Conversion/Providers/PdfRasterizationProvider.cs` coordinates through that port; Desktop wires it, with **no Windows↔Conversion project reference**. This harness combines experiment assertions in a disposable CLI only and must not be copied into a production provider. BE-04/BE-24 must review exact stream/DTO, bounds, cancellation and disposal contracts first; no public port is implemented here.

Package approval is version-specific. Preserve managed/native license and third-party notices, pin/lock dependencies and repeat advisory review before production addition/release. The package graph includes OpenXml.Framework 3.5.1 and System.IO.Packaging 10.0.2; a clean advisory snapshot is not a full security audit. A future package upgrade is a separate dependency review, not blanket approval of any version.

The native package's aggregate notice includes LGPL/eCos and alternative-license sections as well as permissive terms. Component applicability to the exact win-x64 DLL and resulting redistribution obligations are not fully established. This selection approves a feasibility direction only; shipping the native payload is on hold until that review is complete. Do not assert either that the DLL is MIT-only or that every aggregate-notice component is linked.

## Alternatives
PDFium would require another native supplier/wrapper/provenance/update review; retain as a fallback candidate only if Windows.Data.Pdf fails target acceptance. PDFsharp adds a creator dependency and version-specific licensing burden without being a rasterizer. ImageSharp's eligibility terms require a separate business/license decision and are unnecessary for the tested subset. Open XML alone does not render layout. Bundled LibreOffice expands redistribution/installer/security work. Word is a reference experiment only, not a product dependency. Cloud conversion violates scope.

## Consequences
The selected image/PDF/embedding path has feasibility evidence, not production acceptance. Native PDF outputs in this host carry roughly 120-DPI PNG metadata even when pixel geometry corresponds to requested 150/300 DPI; production must normalize that metadata and test scaling independently of the desktop. The successful CLI peak working set was 171,008,000 bytes; this is not an idle-app budget measurement. Cancellation/resource containment, encrypted files, arbitrary fonts/layout and the full 25-route matrix remain unverified.

The DOCX layout route may remain unavailable when LibreOffice is absent, as already permitted by PRD. Its missing fidelity evidence is still a Sprint 0 exit-review gap; accepting this ADR does not mark that experiment passed, waive it or turn Word automation into a fallback. Unsupported/dependency-missing states must be explicit and no lossless/editable-text promise is allowed.

## Migration
Affected: BE-19–27, FE-20–22, conversion matrix, package notices and security model. No production project/package/interface/schema exists to migrate. Proposed limits and subprocess contract need acceptance before implementation.

## Security/Performance Impact
Read-only originals; bounded ZIP/XML/PDF/image processing; unique staging and validated output commit; no macros or external-resource fetch. External renderer timeout/cancellation must terminate owned process tree and isolate profile. Caps and isolation effectiveness require adversarial tests.

## Validation
See [CONVERTER_DISCOVERY](../CONVERTER_DISCOVERY.md) for executed assertions, all failed attempts, hashes, visual review and advisory/signature evidence. Only synthetic fixtures were processed. Original PNG/PDF/DOCX hashes were preserved. A guarded Word reference attempt timed out at 60 seconds and its owned process was cleaned up; no reference PDF exists. LibreOffice fidelity comparison is NOT_RUN.

User acceptance approves this concrete selection and stated follow-up conditions only. Production coding, public contracts, final dependency redistribution approval and Sprint 0 closure remain separate gates.

## Acceptance record
Accepted by the user on 2026-10-27 as part of ADR-0001–0004. The native redistribution hold, DPI metadata issue, DOCX fidelity gap and all stated validation conditions remain open. No package version or production code was changed by recording acceptance. See the [approval record](../evidence/sprint-0/adr-acceptance-20261027/APPROVAL.md).
