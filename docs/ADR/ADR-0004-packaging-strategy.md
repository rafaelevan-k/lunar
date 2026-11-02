# ADR-0004: win-x64 per-user packaging investigation

- Status: Accepted
- Date: 2026-10-19
- Approver: User (explicit approval in this conversation)
- Accepted: 2026-10-27 (Asia/Jakarta)
- Approval scope: Packaging strategy only; no tooling EULA/fee acceptance, tool installation or release validation
- Revised: 2026-10-19

## Context
PRD §7 requires per-user option, startup opt-in, settings retention and signing if available. Architecture's `packaging/wix` is conditional on WiX selection. Installer, runtime deployment and OS support are not validated.

## Decision
Select **one win-x64, self-contained, multi-file WPF deployment in a per-user MSI built with WiX Toolset SDK 7.0.0** for later release implementation. Keep trimming and single-file publishing off initially. Do not publish a parallel framework-dependent, MSIX or ARM64 variant without a reviewed amendment.

Use user-writable application installation scope (for example LocalAppData/Programs/Lunar), HKCU-only user settings/startup registration and an asInvoker runtime. Startup is disabled until the user opts in. Uninstall removes app payload and asks explicitly before deleting settings; upgrades retain/migrate settings and support tested rollback. No installer/custom action is implemented during Sprint 0.

Self-contained avoids making installation of the correct .NET Desktop Runtime a user prerequisite. In exchange, Lunar owns distributing serviced runtime updates and runtime notices; package size/startup/update costs must be measured in release work. This is a reasoned strategy selection, not evidence of an already tested publish.

Before any WiX tool restore/install/use, the owner must review and satisfy the version-specific [OSMF/EULA terms](https://docs.firegiant.com/wix/osmf/), including any applicable fee. Approval of this technical ADR is not automatic EULA acceptance or a claim about the owner's commercial eligibility. No fee is paid and no license accepted here. If terms are declined or inapplicable to the intended distribution, reopen this ADR before implementation; do not silently install an old tool or switch format.

## Alternatives
Framework-dependent deployment is smaller and can use centrally serviced runtimes but introduces a Desktop Runtime prerequisite. MSIX offers packaged lifecycle, but signed sideloading/distribution trust and packaged API behavior need additional validation; [Microsoft signing guidance](https://learn.microsoft.com/en-us/windows/msix/package/signing-package-overview) informs that tradeoff. A portable zip remains a developer artifact, not the release installer. Per-machine default, dual formats and ARM64 are outside this selection. WiX's canonical packaging location already matches BE-29; no task ownership or acceptance criterion changes.

## Consequences
Format, architecture, runtime deployment and tool version are now concrete proposals. Remaining conditions are owner review of tooling terms, certificate availability, size/startup measurements and actual install/upgrade/uninstall/startup/rollback acceptance in BE-29/30. Those release tests are not required to be implemented during Sprint 0; a decision and explicit risks are. A signing certificate was not inspected or assumed available.

## Migration
Affected: BE-02/29/30, FE-23/26, packaging/, release checklist and privacy/settings docs. No current installation/schema/startup registration to migrate. Rollback and settings migration fixtures are required before release.

## Security/Performance Impact
Do not change registry/startup during discovery, install certificates or accept licenses automatically. Signed payload integrity and startup path validation need verification. Self-contained size and launch cost must be measured.

## Validation
Official NuGet version metadata observed WiX SDK 7.0.0 as latest stable on the reporting date. [Package scope schema](https://docs.firegiant.com/wix/schema/wxs/package/) and [.NET publishing documentation](https://learn.microsoft.com/en-us/dotnet/core/deploying/) support the proposed mechanisms. This is documentation/feasibility evidence only: no WiX restore, EULA acceptance, publish or installer execution was performed. Existing successful WPF/native prototypes were not rerun.

Install/upgrade/uninstall/rollback/startup/signing checks remain NOT_RUN and scheduled for the release tasks. Accepting this ADR does not close Sprint 0, authorize BE-01 or waive release acceptance.

## Acceptance record
Accepted by the user on 2026-10-27 as part of ADR-0001–0004. WiX terms/eligibility, signing and release tests remain separate requirements; no tool or EULA was installed/accepted by this documentation action. See the [approval record](../evidence/sprint-0/adr-acceptance-20261027/APPROVAL.md).
