# Sprint 0 risk register

Date: 2026-10-19, Asia/Jakarta. Owner: Codex for discovery; product decisions belong to user.

Decision status updated 2026-10-27: user accepted ADR-0001–0005 and closed Sprint 0 discovery with current limitations. ADR-0005 changes evidence timing; missing validation remains unverified, redistribution/EULA conditions unchanged. [Closure approval](evidence/sprint-0/closure-20261027/APPROVAL.md).

| Risk | Severity / affected work | Mitigation / next evidence | State |
|---|---|---|---|
| Only SDK 9 installed system-wide | Build prerequisite, all .NET 10 builds | Resolved locally with verified portable SDK 10.0.402 and actual successful Release builds; use explicit portable executable | Mitigated locally |
| Folder has no Git metadata | Workflow limitation, BE-01 | No branch/change-history guarantee; address repository setup when assigned; not an API/converter feasibility failure | Open |
| Windows 10 22H2 baseline vs current .NET OS support lifecycle | P1 compatibility/release | Runtime ability and vendor support differ. [Policy source reviewed during discovery](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md) did not establish blanket Windows 10 Home/Pro 22H2 support. Accepted ADR-0001 retains the PRD target; actual OS tests/lifecycle review remain required | Open |
| Host build 26300 is not the full supported OS matrix | Deferred discovery evidence / release gate | Windows 10 22H2/stable Windows 11 and fullscreen/sleep/RDP: FE-26/BE-30 before Sprint 7 release; earlier actual Windows gates intact | Open; timing accepted in ADR-0005 |
| Transparent window focus, DPI, work-area/fullscreen | Deferred discovery evidence / shell gate | Actual interaction: FE-02/05/06, BE-05/08 and Sprint 1 shell smoke. Mixed-DPI/work-area/hotplug: FE-05/BE-08 before relevant task/Sprint 1 acceptance. Full regression FE-26/BE-30 | Open; timing accepted in ADR-0005 |
| Radio controls restricted or consent denied | P1, BE-12/13 | Separate enumeration vs toggle proof; read-only discovery and Settings fallback; never claim write capability from enumeration | Open |
| GSMTC app does not publish seekable timeline | P0, media | Inspect control/timeline flags; test seekable and live apps; no simulated keyboard seek | Open |
| Converter terms / native component applicability | P1 redistribution hold | Five packages' versions/hashes/signatures reviewed; native aggregate has varied/alternative terms. Resolve actual DLL obligations before shipping; no MIT-only payload claim | Strategy Accepted 2026-10-27; release clearance open |
| DOCX fidelity / renderer unavailable | Deferred discovery evidence / conversion gate | Word export TIMEOUT; no LibreOffice detected. Supply working consented reference/renderer and complete comparison before enabling/promising BE-25/27, FE-20–22 routes and relevant Sprint 6 acceptance | Open; timing accepted in ADR-0005 |
| Native PDF DPI behavior | P1 before BE-24 acceptance | Current spike normalizes four separate synthetic sRGB outputs to requested density, with unchanged geometry/all pixels. Verify other DPI/OS/crop/rotation/ICC and production bounds | Mitigated for fixture at 125%; broader acceptance open |
| Installer terms / release implementation | Pre-tooling/release condition | Accepted ADR-0004: WiX 7.0.0 MSI/per-user/self-contained win-x64. Owner reviews applicable tooling terms before use. Full installer tests belong to BE-29/30 | Strategy Accepted 2026-10-27; terms/release tests pending |
| ADR acceptance | Governance item resolved | Four ADRs accepted by user on 2026-10-27; conditions remain, no self-approval or automatic BE-01 authorization | Resolved for strategy approval only |

P0/P1 here describes risk priority, not fabricated observed product defects. No product exists yet.

2026-10-27 completion attempt: repeat builds/native/WPF instrumentation and converter assertions pass. Word opens/counts pages but times out at PDF export; no fidelity result. Actual UI interaction remains NOT_RUN because no targetable prototype window was returned. User confirmed no second monitor/target OS environment and subsequently accepted [ADR-0005](ADR/ADR-0005-sprint-0-validation-deferral.md) and discovery closure. Later task/release obligations above remain open. [Technical evidence](SPIKE_RESULTS.md#completion-attempt--2026-10-27).
