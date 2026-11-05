# Lunar privacy and diagnostics

Status: reflects BE-07 (ADR-0009, accepted 2026-11-03). This is a development-time description of what the app does today, not the final end-user privacy notice (that is a Sprint 7 deliverable, PRD §9).

## Summary

- Lunar has **no telemetry**, no update check and no upload of your files. Nothing leaves your computer, with one opt-in exception: the Usage page (off by default, see below) contacts Anthropic, and only after you turn it on.
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

## Temporary conversion files

While a file is being converted, Lunar works in a private temporary folder under `%LocalAppData%\Lunar\Temp\` named only by a random identifier, and writes the finished result to the folder you chose in one step at the end (as a temporary copy that is then renamed, so a result is either missing or complete). The temporary folder holds copies of the results in progress and a short list of the temporary file paths Lunar created in your folder, so they can be removed if Lunar is closed in the middle of a conversion. It is deleted when the conversion ends, and anything left by a crash is deleted the next time Lunar starts. None of this is sent anywhere or written to the log. Your original files are only read, never changed.

## Usage page (opt-in, FE-27, ADR-0014)

The fourth page shows your Claude 5-hour and weekly limits. It is **off until you turn it on** with the switch on that page, which says what it does first.

- **What is sent, and where:** once on, Lunar asks Anthropic's usage address (`api.anthropic.com`) over HTTPS for your limits, using a token you paste. A request goes out about every 5 minutes while the island is open and about every 15 minutes while it is closed, with a longer wait after errors. Nothing else is sent: no file, no device or Lunar information beyond what an HTTPS request carries. The address is not documented by Anthropic and may change or stop working; the page then says it cannot read the limits.
- **The token:** you paste it into a masked field (the token that `claude setup-token` prints). It is stored only on this PC, in `%LocalAppData%\Lunar\usage-token.dat`, encrypted with Windows DPAPI for your Windows user (other users and other PCs cannot read it). It is never shown again, never written to the log, never part of settings, and sent only to Anthropic. **Remove token** deletes it for good; turning the switch off stops all requests and keeps the token until you remove it. This is a deliberate exception to "passwords only in memory", chosen by the user.
- **What is kept:** the reading is held in memory only to draw the page and to notice a reset; nothing about your usage is written to disk. A reset that happens while Lunar is closed is therefore not announced.
- **Settings:** the switch is saved as `usage.enabled` in `settings.json` (a yes/no only).
- **Logs:** only fixed event names and an error category (for example `usage.token.save.failed`), never the token, the address's reply or your figures.
