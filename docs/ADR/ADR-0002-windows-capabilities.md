# ADR-0002: Windows capability detection and truthful fallbacks

- Status: Accepted
- Date: 2026-10-19
- Approver: User (explicit approval in this conversation)
- Accepted: 2026-10-27 (Asia/Jakarta)
- Approval scope: Capability/naming/result strategy only; exact production API review remains required
- Revised: 2026-10-19

## Context
PRD §2.2/4 and ARCHITECTURE §6/9 require OS-specific adapters and capability-neutral ports. Illustrative contract signatures differ; see API_CONTRACTS. Device enumeration cannot prove writes, permissions, external-event behavior or media seeking.

## Decision
Select Core Audio default endpoint for volume; Native Wi-Fi for adapter/radio/connectivity; Windows radio API for Bluetooth; GSMTC for media. An available API is distinct from a permitted action. Background discovery reads capabilities; it must not request radio consent, scan SSIDs, change playback or synthesize keyboard commands.

Resolve illustrative contract naming in favor of `IWifiService`, `MediaSessionSnapshot` and typed `OperationResult` for commands. Preserve required play/pause/previous/next/seek operations rather than implementing the architecture example's abbreviated method list as a complete API. Prefer separate conversion planner and executor ports. Application owns the normalized contracts; no Windows/WPF/COM type crosses them. This is the naming/result strategy approval, not an approved full C# API version: BE-04 must present exact read-result shapes, cancellation and subscription/replay/threading/disposal semantics before implementation. [API_CONTRACTS](../API_CONTRACTS.md) remains discovery-only.

Settings launches use only whitelisted URIs: `ms-settings:network-wifi`, `ms-settings:bluetooth`, `ms-settings:dateandtime`. Unknown, disabled, enabled, connected, denied and absent states remain distinct. Permission-sensitive SSID is optional; a restricted toggle exposes a clear explanation and Settings action.

Window proposal: idle nonactivation, explicit tray activation for keyboard use. No global click-through that prevents interacting with the pill. DPI/work area/fullscreen handling requires manual evidence and production environment adapter.

## Alternatives
Shelling out to netsh, synthesizing media keystrokes and toggling adapters with elevation do not satisfy reliable capability contracts. Always activating the idle pill risks stealing focus. Hiding unsupported operations without explanation is poor UX.

## Consequences
Unknown, unavailable, denied, disabled, enabled and connected remain distinct. SSID is optional/location-sensitive; no prompt during background discovery. Toggle success requires native result and refreshed state. Seeking requires finite timeline and published position capability, then actual command verification.

## Migration
Affected: BE-04/08/10–18, FE-05/14–19, PRD §2.1 and architecture §6 illustrative signatures. No production API introduced or normalized yet. Accepted signature changes must update both canonical examples and API_CONTRACTS.

## Security/Performance Impact
No new security boundary. Consent only for explicit control requests; no SSID/media history in logs. Prefer OS events, unsubscribe deterministically, marshal snapshots to Dispatcher.

## Validation
Reuse the real read-only probe results in [SPIKE_RESULTS](../SPIKE_RESULTS.md): audio read succeeds; Wi-Fi is disconnected while radios are On; Bluetooth is On; a finite current GSMTC session advertises position control. No old probe was rerun.

Discovery gaps: manual nonactivation/keyboard/hit testing, additional OS/monitor configurations and capability fallback cases. Actual volume write/event integration, radio toggling and seek accuracy remain explicit unverified risks for BE-10–18/FE-14–19, not fabricated Sprint 0 integration failures. Full acceptance of those later features is required in their own sprints. No guarantee that all apps can seek or all radios can toggle is made.

## Acceptance record
Accepted by the user on 2026-10-27 as part of ADR-0001–0004. Exact C# signatures, read-result/cancellation/subscription semantics and implementation are not approved by this strategy alone. See the [approval record](../evidence/sprint-0/adr-acceptance-20261027/APPROVAL.md).
