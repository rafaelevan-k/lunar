# Converter discovery continuation

Date: 2026-10-19, Asia/Jakarta. Owner/integrator: Codex. Authorized scope: unfinished Sprint 0 validation, disposable converter experiments, four decision-ready ADRs and evidence. No FE/BE ID. Original Desktop/Windows prototypes were not edited, rebuilt or rerun. No production solution, conversion service, queue, UI, installer or Application contract was implemented.

Decision status update, 2026-10-27: user accepted ADR-0001–0004 with their stated conditions. Technical results and dated verification snapshots below remain those of the 2026-10-19 experiment. [Approval record](evidence/sprint-0/adr-acceptance-20261027/APPROVAL.md); no new tests or gate waivers were inferred.

## Environment and exact dependency evidence

Same Windows x64 host build 26300.9711, portable SDK 10.0.402. No Git metadata/branch available. Harness targets `net10.0-windows10.0.19041.0`; this is not proof of target OS compatibility. CLI uses per-monitor-v2 process awareness; observed system DPI is 120. All data is generated synthetic content under ignored `artifacts/sprint-0-converter/`; no user documents were accessed.

| Resolved package | Version | Observed package license | Signature verification |
|---|---|---|---|
| SkiaSharp | 4.153.1 | MIT | exit 0 |
| SkiaSharp.NativeAssets.Win32 | 4.153.1 | MIT plus aggregate third-party notices | exit 0 |
| DocumentFormat.OpenXml | 3.5.1 | MIT | exit 0 |
| DocumentFormat.OpenXml.Framework | 3.5.1 | MIT | exit 0 |
| System.IO.Packaging | 10.0.2 | MIT | exit 0 |

Exact package SHA-256 values: [packages.json](evidence/sprint-0/converter-20261019/packages.json). NuGet signatures: [package-signatures.log](evidence/sprint-0/converter-20261019/package-signatures.log). Package content hashes/resolution: `src/Lunar.Conversion/Spike/packages.lock.json`; local `Directory.Packages.props` pins the two direct dependencies without introducing production root scaffolding. Windows SDK reference pack remains SDK-managed.

Exact source URLs/repository commits and native/notice hashes are in [dependency provenance](evidence/sprint-0/converter-20261019/dependency-provenance.json). Final `restore --locked-mode` completed with exit 0 and unchanged lock file: [locked restore](evidence/sprint-0/converter-20261019/restore-locked.log).

`dotnet list ... package --vulnerable --include-transitive --no-restore` returned exit 0 and no vulnerable packages reported by the current configured sources: [advisory snapshot](evidence/sprint-0/converter-20261019/nuget-audit.log). This is a NuGet advisory snapshot, not a complete CVE/native-code/security audit or assurance about future advisories.

Native payload tested: win-x64 `libSkiaSharp.dll`, SHA-256 `935ef4a00462e6b0c4dadb870f734fa43679b4f561287fe0d28cbe2ba147e832`. The exact package [license](evidence/sprint-0/converter-20261019/SkiaSharp-4.153.1-LICENSE.txt) and [aggregate notices](evidence/sprint-0/converter-20261019/SkiaSharp-4.153.1-THIRD-PARTY-NOTICES.txt) are preserved. The aggregate includes multiple terms, including a libmicrohttpd LGPL/eCos section and alternative-license sections. Their presence does not prove every component is linked in the tested DLL, and MIT package metadata does not prove the whole payload is MIT-only. Binary-specific component applicability/obligations remain a redistribution hold before shipping; no complete native SBOM or legal approval is claimed.

## Executed experiments

Successful run: [generate-run-05.jsonl](evidence/sprint-0/converter-20261019/generate-run-05.jsonl), process exit 0. Fixture/output SHA-256 and byte counts: [manifest](evidence/sprint-0/converter-20261019/fixture-output-manifest.json). Generated binaries/fixtures remain ignored local artifacts; the manifest and harness allow reproduction.

| Experiment | Actual result | What it does not prove |
|---|---|---|
| Alpha PNG encode/decode with Unicode filename | PASS; 640×320, all 204,800 pixels compared, zero differences | ICC, all decoders, malformed/oversized inputs |
| PNG→JPEG quality 90 | PASS; explicit white alpha composite, white sample RGB 255/255/255 | Losslessness, complete quality/metadata behavior |
| JPEG EXIF orientation 6→PNG | PASS; origin RightTop detected; explicit rotation yields 320×640 and correct corner | All eight EXIF orientations or automatic orientation by the decoder |
| Corrupt image | PASS; one five-byte fixture rejected | Adversarial parser/security corpus |
| Image→DOCX | PASS; Open XML schema errors zero, one image part with exact source bytes, no external relationships | DOCX pagination/layout or editable OCR text |
| Image/vector PDF creation | PASS; two Letter pages, Arial used for synthetic labels | Arbitrary document fidelity, font substitution/embedding compliance for other fonts |
| PDF→PNG, both pages at intended 150/300 pixel geometry | PASS; 1275×1650 and 2550×3300; image color/alpha anchors and vector transparency checked | Other page sizes/crops/rotation, other OS/DPI contexts, encrypted PDFs |
| PDF 100 pages | PASS for opening and exact PageCount=100 only | Rasterizing all 100 pages, stress/cancellation/resource containment |
| Corrupt PDF | PASS; five-byte fixture rejected, HRESULT 0x80048014 | Password/permission/encryption/malicious PDF cases |
| Source preservation | PASS for synthetic PNG/PDF hashes; DOCX hash unchanged after reference timeout | Production output transactions, disk full, collisions, interruption recovery |

The successful CLI reports 1,795 ms and peak working set 171,008,000 bytes (about 163.1 MiB). These are one-run process measurements, not a benchmark distribution, converter maximum, or idle Lunar RAM result. There is no production queue/transaction implementation to validate.

Four PDF raster outputs (both pages at both sizes) were visually inspected: text is readable, image quadrants and transparent stripe are positioned correctly, and the intentional red/blue vector overlap renders. Eight independent white/blue/purple/pink color samples are within tolerance 8 per channel. [Raster review and PNG metadata](evidence/sprint-0/converter-20261019/raster-review.json). The rotated image was also inspected. This is fixture-specific rendering evidence, not a golden comparison across engines or arbitrary documents.

### DPI discovery and remaining metadata issue

Initial harness incorrectly treated `PdfPageRenderOptions.DestinationWidth/Height` as final pixels. At host 125% DPI, requested 1275×1650 produced 1594×2063. Setting process awareness alone did not fix it. [Microsoft documents destination width in DIPs](https://learn.microsoft.com/en-us/uwp/api/windows.data.pdf.pdfpagerenderoptions.destinationwidth); [page size is also DIPs](https://learn.microsoft.com/en-us/uwp/api/windows.data.pdf.pdfpage.size).

After normalizing destination DIPs using observed system DPI, actual output dimensions match requested geometry on this host. Both actual dimensions and a color anchor are asserted, rather than trusting API success. This correction has not been tested across mixed monitors or other scales/OS versions.

All four PNGs still encode approximately **119.99 DPI metadata**, although their pixel sizes represent Letter at 150/300 DPI. This discrepancy is explicitly unresolved. BE-24 must normalize resolution metadata, test physical dimensions in viewers/print consumers, and verify rotation/crop/page geometry and caps. The experiment does not declare this a production-ready renderer.

### DOCX renderer/reference limitation

LibreOffice detection now checks both standard Program Files locations, PATH and HKLM/HKCU App Paths; no match found. [Detection result](evidence/sprint-0/converter-20261019/libreoffice-detection.json). Custom portable paths were not exhaustively searched. Nothing was downloaded or installed, and no renderer fallback was silently substituted.

Microsoft Word binary 16.0.19127.20860 was found with a valid Microsoft Authenticode signature. It was investigated only as an independent local reference, not as a Lunar provider or proof of license entitlement. The first two attempts failed because the reference harness attempted to obtain an application window handle using an unsuitable Word API; they are harness failures, not evidence Word cannot render DOCX. Two newly created hidden automation instances were identified by PID, exact creation time and automation flag and stopped; user documents had not been opened in those attempts.

The corrected attempt requires no existing Word process and proves new automation ownership before opening the controlled fixture read-only with macros disabled. It did not finish within the outer 60-second bound. The owned automation process was terminated after PID/start-time/hidden-window checks. [Timeout/cleanup evidence](evidence/sprint-0/converter-20261019/word-reference-timeout.json): no owned process remains, no reference PDF exists, original DOCX hash unchanged. No raw document text/path or Word exception message is logged.

Result: **DOCX layout and LibreOffice-versus-Word fidelity remain NOT_RUN/UNVERIFIED**. The reason for the final automation stall is not established. Do not infer an Office activation problem, DOCX defect, LibreOffice defect or platform incompatibility from the timeout.

The PDF/Documents skills' visual-review principle was used. Their container-only dependency loader, artifact-operation marker and bundled LibreOffice are not exposed in this native Windows coding environment; no container artifact authoring workflow or canonical `render_docx.py` render is claimed. Fixtures were generated by the requested .NET feasibility harness. No finished DOCX/PDF document is being delivered to the user as a validated artifact; generated files are internal experiments.

## Reproduction and preserved failures

Use the portable SDK explicitly, `NUGET_PACKAGES=<repo>/.tools/nuget`, telemetry opt-out and a developer shell that can discover the Windows SDK. These commands apply only to the new harness and do not invoke `Invoke-Sprint0.ps1` or old prototypes:

```powershell
.\.tools\dotnet\dotnet.exe restore src\Lunar.Conversion\Spike\Lunar.Conversion.Spike.csproj --locked-mode
.\.tools\dotnet\dotnet.exe build src\Lunar.Conversion\Spike\Lunar.Conversion.Spike.csproj -c Release --no-restore
.\.tools\dotnet\dotnet.exe src\Lunar.Conversion\Spike\bin\Release\net10.0-windows10.0.19041.0\Lunar.Conversion.Spike.dll generate artifacts\sprint-0-converter\NEW_RUN
```

The generator rejects an existing workspace. Production public contracts and conversion providers are deliberately absent. `scripts/Export-WordDiscoveryReference.ps1` is a reference experiment, requires an outer 60-second owned-process guard as used in this session, and is not a reliable general converter. Do not rerun Word reference until the host automation issue is diagnosed or a renderer/reference environment is supplied.

| Observed command/attempt | Exit / result | Evidence |
|---|---|---|
| Restore new harness | 0 | [restore.log](evidence/sprint-0/converter-20261019/restore.log) |
| Build attempt 1 | 1; obsolete SkiaSharp API calls rejected as errors | [build-attempt-1.log](evidence/sprint-0/converter-20261019/build-attempt-1.log) |
| Build attempts 2–7 | 0; last build zero warnings/errors | [last build](evidence/sprint-0/converter-20261019/build-attempt-7.log) |
| Generate runs 01/02 | 1; DOCX formatting child order invalid, then corrected | [run 01](evidence/sprint-0/converter-20261019/generate-run-01.jsonl), [run 02](evidence/sprint-0/converter-20261019/generate-run-02.jsonl) |
| Generate runs 03/04 | 1; raster dimensions differ; awareness alone insufficient | [run 03](evidence/sprint-0/converter-20261019/generate-run-03.jsonl), [run 04](evidence/sprint-0/converter-20261019/generate-run-04.jsonl) |
| Generate run 05 | 0; scoped experiment assertions pass | [run 05](evidence/sprint-0/converter-20261019/generate-run-05.jsonl) |
| `dotnet nuget verify <each of five packages> --all` | 0 each | [signature log](evidence/sprint-0/converter-20261019/package-signatures.log) |
| NuGet transitive vulnerability query | 0; none reported by current sources | [audit](evidence/sprint-0/converter-20261019/nuget-audit.log) |
| Word reference attempts 1/2 | 1; harness isolation/handle discovery failed | [attempt 1](evidence/sprint-0/converter-20261019/word-reference.jsonl), [attempt 2](evidence/sprint-0/converter-20261019/word-reference-attempt-2.jsonl) |
| Corrected Word reference attempt 3 | TIMEOUT at 60 s; outer command failed; child exit not collected as success | [timeout](evidence/sprint-0/converter-20261019/word-reference-timeout.json) |
| Read-only raster sample/metadata assertions | 0 | [raster review](evidence/sprint-0/converter-20261019/raster-review.json) |

No failed test log was rewritten. No unit test suite/full conversion golden matrix is claimed; this is an executable feasibility experiment.

Historical 2026-10-19 [verification](evidence/sprint-0/converter-20261019/verification.log) checked old source hashes, 56 TODO statuses, four ADRs that were Proposed then, project boundaries, script syntax, run results and reference cleanup. [Source manifest](evidence/sprint-0/converter-20261019/source-manifest.json) and [environment](evidence/sprint-0/converter-20261019/environment.json) retain that experiment's scope. The separate 2026-10-27 approval record changes ADR status only; local checks do not substitute for unexecuted manual/product acceptance.

## Decision package and exit boundary

ADRs 0001–0004 were decision-ready on 2026-10-19 and accepted by the user on 2026-10-27. Native component applicability, PNG DPI metadata, renderer fidelity, target OS/multi-monitor/manual shell evidence, and packaging terms/release tests remain classified in [SPRINT_0_EXIT_REVIEW](SPRINT_0_EXIT_REVIEW.md). No validation/redistribution hold was waived and no BE-01 work started.

## Technical follow-up — 2026-10-27

See [current completion results](SPIKE_RESULTS.md#completion-attempt--2026-10-27). The revised spike preserves each native PNG and creates a separate normalized sRGB output. Four outputs pass requested density within 0.02 DPI, unchanged dimensions and complete decoded BGRA equality. Both normalized 150-DPI pages visually inspected. This resolves the density discrepancy only for these fixtures/current host; production/cross-DPI/ICC requirements remain. Converter locked restore, Release build and generator pass; no package change. New peak working set is 415,744,000 bytes, a discovery cost to revisit before production, not a budget PASS.

Guarded Word follow-up identifies the stall: `Documents.Open` and `ComputeStatistics` complete, `ExportAsFixedFormat` does not finish within 60 seconds. Exact owned process cleaned, DOCX unchanged, no PDF. No LibreOffice/independent-reference fidelity claim. New runner: `scripts/Invoke-Sprint0Continuation.ps1` (`-WordReference` opt-in); records phases, child bounds, commands and outcome. A failure exit for the combined run correctly preserves the reference TIMEOUT despite passing converter assertions. No repeat reference attempt is useful without a changed rendering environment or targeted diagnosis.
