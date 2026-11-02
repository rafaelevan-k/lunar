# Lunar privacy and diagnostics

Status: reflects BE-07 (ADR-0009, accepted 2026-11-03). This is a development-time description of what the app does today, not the final end-user privacy notice (that is a Sprint 7 deliverable, PRD §9).

## Summary

- Lunar has **no telemetry**, no update check and no upload of any kind. Nothing leaves your computer.
- Lunar writes a small local log of **what happened**, never **what you were doing**.
- Diagnostics (extra detail) are **off** unless you turn them on.

## What is logged

Each log line is one structured event with only these fields:

| Field | Meaning |
|---|---|
| Timestamp, level | when it happened; Debug (diagnostics only), Information, Warning, Error |
| Event ID, component, operation | short fixed names such as `app.start.primary`, `App`, `Start` |
| Error code | a category such as `IoFailure`, `Timeout`, `Cancelled` |
| Duration | milliseconds, when relevant (for example uptime at exit) |
| Exception type | the type name only, for example `InvalidOperationException` |
| App version, OS build | version numbers such as `10.0.26300`, to match logs to builds |

There is **no free-text message field and no parameter field** in the logging API, so code cannot add anything else. As a second layer, any event ID, component or operation that is not a short token (letters, digits, `.`, `_`, `-`, at most 64 characters) is replaced by `redacted` before it is written.

## What is never logged

File names and paths, document or image contents, PDF passwords, Wi-Fi network names (SSIDs) or passwords, Bluetooth device names, media titles, artists or listening history, clipboard content, exception messages or stack traces, user names, and window titles of other applications.

## Where and for how long

Files are in `%LocalAppData%\Lunar\Logs\` (`lunar-YYYYMMDD.log`). Storage is bounded:

| Mode | Files kept | File size limit | Total (approximately) |
|---|---|---|---|
| Normal | 7 | 1 MiB | 7 MiB |
| Diagnostics | 7 | 5 MiB | 35 MiB |

A file can exceed its size limit by at most one log line (the size is checked before each write). Older files are deleted automatically. You can delete the whole `Logs` folder at any time; Lunar recreates it. If the folder cannot be written, Lunar keeps working and simply does not log.

## Settings file

Preferences are stored in `%LocalAppData%\Lunar\settings.json` (with `settings.json.bak`, the previous good copy, and `settings.corrupt.json`, the last unusable file, kept so nothing is lost silently). They hold only choices you make: startup, island position, theme and motion, language, a preferred media app id, a conversion output folder and the diagnostics switch. They never hold passwords, Wi-Fi names, document content or media titles or history, and the settings store writes no values or paths to the logs. Deleting the folder resets Lunar to defaults.

## Diagnostics (opt-in)

Diagnostics add `Debug` events and the larger size limit; they add no new kinds of data. They are off by default. Until a Settings page exists (FE-23, using the settings store from BE-06), the only switch is the environment variable `LUNAR_DIAGNOSTICS=1`, read when Lunar starts (only the exact value `1` enables it). This is a tester/developer switch, not a user feature, and will be replaced by a setting.

Lunar never uploads logs. A way to export logs for support would need its own review and an explicit, reviewable user action.

## Known gaps

- Logging of conversion, media and system features does not exist yet because those features do not exist yet; each will define its own fixed event IDs and be checked against this page.
- Uninstall behavior for the Logs folder (keep or delete, with a prompt) belongs to the installer task (BE-29).
- Crash dumps and Windows Error Reporting are controlled by Windows, not by Lunar.
