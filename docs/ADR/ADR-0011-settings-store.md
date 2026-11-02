# ADR-0011: Settings model, repository port and JSON store (BE-06)

- Status: **Accepted**
- Date: 2026-11-03
- Approver: User (explicit approval in chat: "Terima ADR-0011", 2026-11-03)
- Approval scope requested: the `LunarSettings` model and its v1 field set, the `ISettingsRepository` port, the on-disk format (`settings.json`, schema version 1), the corruption/recovery policy, and the atomic-write and backup strategy.

## Context

BE-06 persists versioned settings to LocalAppData with atomic writes, backup, validation, schema migration and defaults; acceptance is "corrupt settings recover without crashing; tests for migration". PRD §6 and ARCHITECTURE §11 fix the location (`%LocalAppData%\Lunar\settings.json`), a `schemaVersion`, atomic write plus backup, validate-on-read, safe-default fallback with a recoverable notice, and no credentials or file contents. ARCHITECTURE §11 shows an *illustrative* schema; ADR-0006 deferred `ISettingsRepository`/`LunarSettings` to this task. A settings schema is a persistence contract, so AGENTS §12 requires an accepted ADR. Consumers arrive later: FE-05 (monitor, offset), FE-23 (Settings page), BE-29 (startup), and the diagnostics opt-in, which today is only the `LUNAR_DIAGNOSTICS=1` environment variable (ADR-0009, `docs/PRIVACY.md`). Logging already uses `%LocalAppData%\Lunar\Logs`.

## Decision

**Layers.** `LunarSettings` and the port live in `Lunar.Application` (no `System.IO`, no packages, per the layer tests). Serialization, file I/O and migration live in `Lunar.Infrastructure/Settings`. No new project, reference or package: `System.Text.Json` is part of the BCL.

**Port** (`Lunar.Application/Abstractions/Settings/ISettingsRepository.cs`):
```csharp
public interface ISettingsRepository {
    Task<Result<SettingsLoadResult>> LoadAsync(CancellationToken ct);
    Task<Result> SaveAsync(LunarSettings settings, CancellationToken ct);
}
public sealed record SettingsLoadResult(LunarSettings Settings, SettingsRecovery Recovery);
public enum SettingsRecovery { None, CreatedDefaults, RestoredFromBackup, ResetToDefaults, ValuesReset, NewerVersionIgnored }
```
- `LoadAsync` never fails because of file *content*. Missing, corrupt or invalid content produces usable settings plus a `Recovery` value the UI can show as a notice. It fails only for cancellation (`Cancelled`) or when the file cannot be read at all (`AccessDenied`/`IoFailure`); in that case the file is left untouched.
- `SaveAsync` validates and normalizes, then writes atomically. Success means the data is on disk and the file was replaced.
- Calls are serialized inside the repository; it holds no state besides a lock.

**Model** (`Lunar.Application/Settings/Models/LunarSettings.cs`, immutable records with `init` properties and a `Defaults` instance; pure range rules in `Lunar.Application/Settings/SettingsRules.cs`, unit-tested). Proposed v1 field set (a subset of PRD §6 with defined meaning; the rest is additive later):

| Group | Field | Default | Rule |
|---|---|---|---|
| root | `launchAtStartup` | false | opt-in (PRD) |
| island | `preferredMonitorId` | null (primary) | display device name from `DisplayInfo.Id`, ≤ 128 chars |
| island | `topOffsetDip` | 8 | 0–200 |
| appearance | `theme` | System | System / Dark / Light |
| appearance | `reduceMotion` | false | bool |
| appearance | `eyeAnimationEnabled` | true | bool |
| appearance | `autoCollapse` | true | bool |
| media | `preferredSessionId` | null | app/session id, ≤ 128 chars; no titles, artists or history |
| conversion | `outputDirectory` | null | ≤ 260 chars; must be an absolute local path (checked in Infrastructure), else reset to null |
| privacy | `diagnosticsEnabled` | false | opt-in |
| root | `locale` | null (system) | BCP-47 tag, ≤ 35 chars |

Not included: accent colour (no design token yet), "topmost behavior" and exact page-persistence semantics (PRD does not define them). They can be added without a version bump because a missing field means its default.

**Format.** UTF-8 JSON, camelCase, nested objects as in ARCHITECTURE §11, plus top-level `"schemaVersion": 1`. Unknown fields are ignored on read and dropped on the next save. File size is capped at 64 KiB on read; larger files count as corrupt.

**Versions and migration.** `SettingsMigration` is an ordered chain of `vN → vN+1` steps over the parsed JSON tree, each deterministic and fixture-tested. Rules: a file with no or an invalid `schemaVersion` is corrupt. A version older than current is migrated step by step and saved in the new format. **No production migration exists yet** because v1 is the first version; the mechanism is proven with test-only steps and a v1 round-trip fixture, and the first real step arrives with the first breaking schema change (additive fields never need one). A version **newer** than this build supports is not rewritten: defaults are used in memory, `Recovery = NewerVersionIgnored`, the file is left untouched, and `SaveAsync` returns `Unsupported` so an older build cannot destroy newer data.

**Atomic write and backup.** Write `settings.json.tmp` in the same folder, flush to disk, then `File.Replace(tmp, settings.json, settings.json.bak)` (first save: `File.Move`). The `.bak` is always the previous good file. Leftover `.tmp` files are deleted on the next save or load. The folder is created on demand under `%LocalAppData%\Lunar` (resolved by the OS API, no hard-coded user path; the root is injectable for tests).

**Recovery order on load.** `settings.json` → if unreadable as valid JSON of a known version, `settings.json.bak` (`RestoredFromBackup`) → defaults (`ResetToDefaults`). A corrupt main file is first copied to `settings.corrupt.json` (single slot, overwritten) so nothing is silently lost; this file contains only preferences. Individually invalid values (out of range, wrong type, bad path) are replaced by their default and reported as `ValuesReset`; valid siblings are kept. A missing file yields `CreatedDefaults` and writes nothing until the first save.

**Privacy.** Settings hold no credentials, SSIDs, document content or media history. The output folder and media app id are user preferences stored only in this local file and never logged. Diagnostics use `IDiagnosticLog` events with codes only (`settings.load.recovered`, `settings.save.failed`); no paths or values.

**Registration and wiring.** `ServiceRegistration` registers `ISettingsRepository` as a singleton. `App` does not load settings in BE-06; the first consumers (FE-05, FE-23) do. The `LUNAR_DIAGNOSTICS=1` switch stays until a consumer reads `privacy.diagnosticsEnabled`, which is a later change that will update `docs/PRIVACY.md`.

## Alternatives

- Throw on corrupt content: violates the acceptance criterion.
- Reset the whole file when any value is invalid: loses valid user choices.
- Store the schema version in the file name or a second file: more states to keep consistent.
- SQLite/registry: heavier, new packages or system-wide state, contrary to PRD.
- Overwrite a newer-version file: silent data loss when users run an older build.
- Newtonsoft.Json: extra dependency with no benefit here.

## Consequences

- Application gains one port, one record graph and pure rules; Infrastructure gains the store and migration chain; no new dependency edge.
- FE-05/FE-23/BE-29 get typed settings and a notice they can display; they still need ordinary change-notification plumbing, which BE-06 does not provide (reload through `LoadAsync`; no change events in v1).
- A single backup slot and a single corrupt-copy slot bound disk use to about three small files.

## Migration

None for users (first schema version). Affected: BE-06, FE-05, FE-23, BE-29, BE-07 follow-up (diagnostics setting), `docs/API_CONTRACTS.md`, `docs/PRIVACY.md`, `ARCHITECTURE.md` §11 example (illustrative; unchanged unless you ask).

## Security/Performance Impact

No network, elevation or new packages. Reads are size-capped and parsed with `System.Text.Json` (no polymorphic or type-name handling). Paths from the file are validated before use and never executed. Atomic replace keeps either the old or new file complete after a crash; a power loss between flush and replace leaves the previous file intact. Cost is a few KiB per save.

## Validation

Unit tests: rules and defaults; round-trip; missing file; empty, truncated, binary and oversized files; wrong types; each value out of range; unknown fields; missing and invalid `schemaVersion`; migration chain order and idempotence; newer version untouched with `Unsupported` on save; backup restore; leftover `.tmp`; save failure keeps the old file; cancellation; concurrent saves; no sensitive values in log events. Architecture tests (Application has no `System.IO`). Real host: save/load against a temp folder on this machine and one run against the real `%LocalAppData%\Lunar` path with cleanup. Not verified: power-loss behavior, network or redirected `%LocalAppData%`, antivirus locking, Windows 10 22H2.
