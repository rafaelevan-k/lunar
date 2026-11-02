# Initial security review — Sprint 0

Status: discovery, not a completed BE-28 audit. Product requirements remain PRD §4.3/7 and AGENTS §9/10.

## Prototype boundary

- Probe uses installed Windows APIs, makes no network requests, enumerates no filenames/media metadata/SSIDs and changes no device state.
- Core Audio RCWs are released; WLAN memory and handle freed; asynchronous WinRT operations have an 8-second timeout, with a separate 45-second runner bound.
- No radio permission prompt, scan, pairing, volume write, play/pause or seek command is issued automatically.
- Window runs `asInvoker`, no startup registration or persistence. Smoke logging is explicit, creates a new file, and logs only operational fields. Framework DI container/rolling production logs are future numbered work; prototype uses constructor injection and bounded event count.
- Microsoft SDK bootstrap is a development action, stored under ignored `.tools/`; archive SHA-512 is checked against official release metadata before execution. It is not an application auto-downloader.
- Build/restore may contact NuGet for development dependencies. This is distinct from local-only runtime conversion. No user documents are uploaded.

## Production threat model and required evidence

| Boundary / threat | Required mitigation | Verification status |
|---|---|---|
| Untrusted extensions/content | Validate extension and signatures, bounded decode | NOT_RUN; converter absent |
| DOCX ZIP/XML | Entry/count/expanded-size/ratio limits, prohibit DTD/external resolution, no macros/remote relationships | NOT_RUN |
| PDF/image exhaustion | File/page/pixel/memory/time caps; cancellation; worker isolation only if reviewed | NOT_RUN |
| Paths/reparse points/collisions | Destination containment; safe names; explicit overwrite; originals read-only | NOT_RUN |
| Output transaction | Unique staging, validate output, safe same-volume commit or documented cross-volume fallback; cancel cleanup | NOT_RUN |
| Optional renderer | User-approved installed executable, canonical path/signature, no shell interpolation, ArgumentList, isolated profile, process-tree termination, no remote resource fetching | NOT_RUN |
| Privacy | Redacted structured bounded logs, no contents/passwords/SSID/history; passwords memory only | Probe fields inspected; production NOT_RUN |
| Dependencies | Pinned versions, lock graph, native provenance, CVE/license inventory and notices | Initial review only |
| Packaging | Per-user runtime, explicit startup opt-in, tested upgrade/rollback/uninstall and settings retention prompt | NOT_RUN |

No claim that launching LibreOffice headless alone creates a security sandbox or prevents all external-resource access. The production renderer boundary needs explicit validation before untrusted documents are processed.

## Converter discovery continuation

Only controlled generated fixtures were processed by a disposable CLI. The harness rejects an existing output workspace, reads source PNG/PDF read-only and verifies hashes; DOCX embeds the exact PNG with no external relationships and passes schema validation. Render targets are capped at 9 million intended pixels for this fixture experiment, with native-operation timeouts and outer process bounds. These are not proof of a production input validator, atomic output transaction, hard native memory sandbox or effective in-flight cancellation.

Package signatures and a current NuGet advisory snapshot were collected for the exact five-package graph. Native aggregate notices contain mixed/alternative terms including LGPL/eCos sections; binary-specific applicability and redistribution compliance remain open. No native binary was obtained from an unverified mirror. Preserve full notices and exact hashes before production approval.

Word reference automation was restricted to a new instance with no prior Word process, read-only synthetic DOCX access, macro disable and outer 60-second bound. Initial reference-harness handle-discovery attempts failed; their own hidden automation processes were identified and cleaned using PID/start-time/automation checks. The corrected attempt timed out, its owned process was cleaned, no PDF exists and source DOCX hash is unchanged. No document contents or raw Office exception messages are logged. Word is not a Lunar dependency or production fallback.

Fixtures/outputs and caches remain under ignored artifacts/.tools. Evidence stores synthetic labels, hashes, operational results and licenses. No user input, media history, SSID, password, startup registration, installer or runtime network conversion was introduced. Product UI/accessibility/security hardening and converter adversarial tests remain unverified in their later tasks; exact evidence/risks are in CONVERTER_DISCOVERY and SPRINT_0_EXIT_REVIEW.

2026-10-27: bounded continuation runner now records safe Word phases and automatically cleans the exact proven owned Word PID/start time after the 60-second export timeout; source DOCX unchanged. Native PNGs are preserved; normalized outputs use new paths and assert all-pixel equality. Synthetic OS-rendered sRGB only, no arbitrary user ICC claim. No production input/security boundary changed; no new dependencies or renderer install. UI observation produced no interaction PASS and no private app screenshot was saved. [Current evidence](SPIKE_RESULTS.md#completion-attempt--2026-10-27).
