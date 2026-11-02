# Conversion matrix — discovery baseline

Source: PRD §5 and Sprint 6 prompt. These are intended v1 routes, **not implemented or fidelity-verified**.

S = same encoding/no-op or separate copy; N = direct image encode; R = rendered/embedded; D = optional installed renderer required; X = unavailable in v1. JPG/JPEG share one encoding.

| Input | PNG | JPG | JPEG | DOCX | PDF |
|---|---|---|---|---|---|
| PNG | S | N | N | R embed | R image page |
| JPG | N | S | S | R embed | R image page |
| JPEG | N | S | S | R embed | R image page |
| DOCX | D page images | D page images | D page images | S | D layout engine |
| PDF | R per page | R per page | R per page | X (post-v1 stretch) | S |

PRD's B marker for PDF→DOCX identifies a stretch possibility; the Sprint 6 prompt explicitly disables it in v1 absent separate approval. No scope change is made here.

- PNG→JPEG: lossy; alpha composited on selectable background, default white; EXIF/ICC handling must be golden-tested.
- Images→DOCX: embedded pixels, no OCR or editable-text claim.
- PDF→images: page selection, 72/150/300 DPI (150 default), bounded pixels/pages, stable page numbering.
- DOCX→PDF/images: optional separately installed LibreOffice, isolated profile and timeout; fonts/layout may differ from Word. No silent install or network fallback.
- S routes must preserve originals and follow explicit destination/collision policy; never reinterpret this as permission to overwrite input.
- Passwords remain in memory; no persistence or logging.

Continuation evidence: a disposable harness demonstrated Unicode/alpha PNG roundtrip, PNG→JPEG, EXIF orientation 6, image-page PDF creation, PDF raster geometry at 150/300 on the current host, exact image embedding in DOCX, one corrupt image/PDF each, and opening/counting a synthetic 100-page PDF. See [CONVERTER_DISCOVERY](CONVERTER_DISCOVERY.md). These do not implement product routes or mark any BE task DONE.

DOCX rendering/fidelity, encrypted inputs, other orientations/crop/rotation/ICC, long paths, all-page stress/cancellation, collisions/disk-full/output transactions and complete golden route coverage remain NOT_RUN. Native PNG resolution metadata still follows host DPI and needs normalization. User accepted the ADR-0003 provider strategy on 2026-10-27; native redistribution/license applicability, exact contracts and target acceptance remain pending. No route/test status was changed by that approval.

Subsequent technical validation on 2026-10-27: four separate normalized synthetic PNGs pass requested 150/300 DPI within PNG density quantization, unchanged geometry and all-pixel equality; the original native PNGs remain preserved. DOCX reference export again TIMEOUT after opening/pagination. No product route became implemented or available. [Current results](SPIKE_RESULTS.md#completion-attempt--2026-10-27).
