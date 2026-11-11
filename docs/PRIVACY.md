# Lunar privacy and diagnostics

This notice describes what Lunar 1.0.x does with your data, and it is installed next to the app. It is based on ADR-0009 (logging), ADR-0014 and ADR-0015 (Usage page), ADR-0019 (screen sharing), ADR-0020 (notes), ADR-0021 (brightness), ADR-0022 (recordings), ADR-0023 (Pomodoro and Do Not Disturb) and ADR-0025 (update check).

## Summary

- Lunar has **no telemetry** and no upload of your files. Nothing about you or your files leaves your computer.
- Lunar contacts the internet in only two ways:
  - **the update check:** once a day it asks GitHub for the newest Lunar release (on by default, switch in Settings), and downloads it only when you choose Install now;
  - **the Usage page:** off by default; it contacts Anthropic only after you turn it on.
- Lunar writes a small local log of **what happened**, never **what you were doing**.
- Diagnostics (extra detail) are **off** unless you turn them on.
- Your **notes** and **recordings** stay on this PC and are never logged or sent anywhere.
- Every Lunar window is **kept out of screen sharing, screenshots and recordings**, always.

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

File names and paths, document or image contents, note text, recorded audio, sound levels, microphone names, PDF passwords, Wi-Fi network names (SSIDs) or passwords, Bluetooth device names, media titles, artists or listening history, clipboard content, exception messages or stack traces, user names, and window titles of other applications.

## Where and for how long

Files are in `%LocalAppData%\Lunar\Logs\` (`lunar-YYYYMMDD.log`). Storage is bounded:

| Mode | Files kept | File size limit | Total (approximately) |
|---|---|---|---|
| Normal | 7 | 1 MiB | 7 MiB |
| Diagnostics | 7 | 5 MiB | 35 MiB |

A file can exceed its size limit by at most one log line (the size is checked before each write). Older files are deleted automatically. You can delete the whole `Logs` folder at any time; Lunar recreates it. If the folder cannot be written, Lunar keeps working and simply does not log.

## Settings file

Preferences are stored in `%LocalAppData%\Lunar\settings.json` (with `settings.json.bak`, the previous good copy, and `settings.corrupt.json`, the last unusable file, kept so nothing is lost silently). They hold only choices you make: startup, island position, theme and motion, language, a preferred media app id, a conversion output folder, the recordings folder and the chosen microphone's Windows id, the last focus length, the update-check switch and the diagnostics switch. They never hold passwords, Wi-Fi names, document content or media titles or history, and the settings store writes no values or paths to the logs. Deleting the folder resets Lunar to defaults.

## Notes

The Notes page (ADR-0020) stores your notes in `%LocalAppData%\Lunar\notes.json`, separate from the settings, with `notes.json.bak` (the previous good copy) and, if the file is ever damaged, `notes.corrupt.json` (the damaged file, kept whole so nothing is lost silently). Each note holds its text and when it was created and changed; nothing else.

- **Plain text, not encrypted.** The file is protected by your Windows account's file permissions, like the settings. Anyone who can read your account's files (for example another administrator, or a backup of your profile) can read your notes. Do not keep passwords in them.
- **Never sent or logged.** Note text is never sent anywhere and never written to the log: log events about notes carry only fixed names and an error category (for example `notes.save.failed`). Notes are not shown in the tray or in notifications.
- **Limits** keep the file bounded: 100,000 characters per note, 10,000 notes and 20 MB for the file.
- Deleting a note removes it from the file at its next save; **Undo** is offered for a few seconds before that is final in the app.

## Hidden from screen sharing

Lunar asks Windows to leave every one of its windows out of screen capture (ADR-0019): the island, the Settings window, the reset notice, tooltips, the tray menu and Lunar's dialogs. You still see them; screen sharing, screenshots and recordings that use Windows' capture features do not. This is always on. It is a privacy convenience, not a security boundary: a phone camera, or software built to ignore this Windows setting, can still see Lunar, and the tray icon (part of the Windows taskbar) stays visible. If Windows refuses, **Settings → Privacy** says that Lunar may be visible, and the log records only an error code.

## Recordings (ADR-0022)

- **No microphone is opened** until you press Record, and Windows shows its own microphone sign while Lunar records. If Windows privacy settings block microphones for desktop apps, Lunar says so and never works around it.
- **Where they go:** MP3 files in the folder you choose (normally `Music\Lunar Recordings`). While recording, the sound goes to a hidden part file (`~….lunar-part`) in that folder, renamed to the finished `.mp3` at the end. A part file left by a crash is recovered or left in place, never deleted silently.
- **Never sent or logged.** Audio, the sound levels shown in the island, microphone names and file paths are never sent anywhere and never written to the log; log events carry fixed names and an error category only.
- **Not encrypted.** Recordings are ordinary files protected by your Windows account's permissions.
- **Uninstalling never deletes them**, because they are in your own folder.
- **Consent is yours to get.** Tell people before you record them; in many places recording without consent is against the law.

## Pomodoro and Do Not Disturb (ADR-0023)

During a focus session Lunar turns on Windows Do Not Disturb (alarms only) and afterwards puts back the state it found. To repair an unexpected stop, it keeps a small marker, `%LocalAppData%\Lunar\focus.marker`, holding only the earlier Do Not Disturb setting; it is deleted when the session ends. Lunar does not read, store or see your notifications. The last focus length is saved in the settings. Nothing about sessions is sent anywhere; the log has fixed event names only.

## Update check (ADR-0025)

- **What is sent:** once shortly after Lunar starts and then every 24 hours, one HTTPS request to `api.github.com` asks for the newest release of `rafaelevan-k/lunar`. It carries no account, no identifier and nothing about you, your files or how you use Lunar, only a fixed product name. Like any website, GitHub sees your IP address and the time.
- **When you choose Install now**, Lunar downloads `Lunar.msi` from GitHub's own servers into `%LocalAppData%\Lunar\Updates`. It runs the file only if its size and SHA-256 fingerprint match the release, and deletes it otherwise. The downloaded installer is deleted the next time Lunar starts.
- **Turning it off:** **Settings → Updates → Check for updates**. When it is off, Lunar makes no request to GitHub at all.
- **Logs:** fixed event names only, never an address, a version history or a file path.

## Display brightness

The brightness row (ADR-0021) reads and sets the brightness of the display the island is on, through Windows for a built-in screen and through DDC/CI for an external monitor. It reads no other information from the display, stores nothing and sends nothing; the log records only error codes.

## Diagnostics (opt-in)

Diagnostics add `Debug` events and the larger size limit; they add no new kinds of data. They are off by default. You turn them on in **Settings, Privacy, Detailed diagnostics** (the tray icon's Settings item). The choice is saved with your other preferences and applies from the **next time Lunar starts**, because the log is set up before anything else; the Settings window says so while the saved choice is not yet in use. The environment variable `LUNAR_DIAGNOSTICS=1` (only the exact value `1`) still turns diagnostics on for a run, whatever is saved. At start-up Lunar looks once, read-only, at the saved diagnostics choice in `settings.json` (a file of at most 64 KB; any problem means off); it never repairs or rewrites the file for this.

The Settings window itself stores only the choices listed above, in the same file, and writes nothing to the logs except a code when a save fails. **Reset all settings** returns every choice to its default, which also turns the Usage page off.

Lunar never uploads logs. A way to export logs for support would need its own review and an explicit, reviewable user action.

## Uninstalling

When you uninstall Lunar from Windows Settings, it asks whether to delete your settings, notes and logs too (`%LocalAppData%\Lunar`). Recordings are in your own folder and are never deleted by uninstalling. If you choose **No**, or uninstall silently, they are kept for a later reinstall, and you can delete the folder yourself at any time. Uninstalling always removes Lunar from Start with Windows.

## Not controlled by Lunar

Crash dumps and Windows Error Reporting are controlled by Windows, not by Lunar.

## Temporary conversion files

While a file is being converted, Lunar works in a private temporary folder under `%LocalAppData%\Lunar\Temp\` named only by a random identifier, and writes the finished result to the folder you chose in one step at the end (as a temporary copy that is then renamed, so a result is either missing or complete). The temporary folder holds copies of the results in progress and a short list of the temporary file paths Lunar created in your folder, so they can be removed if Lunar is closed in the middle of a conversion. It is deleted when the conversion ends, and anything left by a crash is deleted the next time Lunar starts. None of this is sent anywhere or written to the log. Your original files are only read, never changed.

## Usage page (opt-in, FE-27/FE-29, ADR-0014 and ADR-0015)

The Usage page (the last page) shows your Claude 5-hour and weekly limits. It is **off until you turn it on** with the switch on that page, which says what it does first.

- **What Lunar reads:** the sign-in that Claude Code keeps on this PC, `%USERPROFILE%\.claude\.credentials.json`, **read-only**. Lunar takes only three fields from it (the access token, when it expires and its permissions), never the refresh token, and it never writes, renames or locks the file, so Claude Code is not disturbed. The token is held in memory for one request and wiped; it is never stored by Lunar, shown, logged or put in settings. Lunar never signs in for you, never renews the token and never contacts Claude's sign-in service. If Claude Code is not signed in, or the sign-in has expired or lacks the permission to read usage, the page says so in plain words and makes **no network request**.
- **What is sent, and where:** once on, Lunar asks Anthropic's usage address (`api.anthropic.com`) over HTTPS for your limits, with that token. A request goes out about every 5 minutes while the island is open and about every 15 minutes while it is closed, with a longer wait after errors; the island opening, or the Refresh button, tries again after a sign-in problem (at most once every 30 seconds). Nothing else is sent: no file, no device or Lunar information beyond what an HTTPS request carries. The address is not documented by Anthropic and may change or refuse a login at any time; the page then says it cannot read the limits.
- **Terms:** Anthropic's terms limit how Claude Code sign-ins may be used outside Claude Code. This feature is a read-only use by the account's own owner, one small request at a time, and is a grey area; it may stop working if Anthropic blocks it.
- **Earlier token:** a token pasted in an earlier version (from `claude setup-token`) can never read usage; Lunar deletes that stored file (`%LocalAppData%\Lunar\usage-token.dat`) once at start-up.
- **What is kept:** the reading is held in memory only to draw the page and to notice a reset; nothing about your usage is written to disk. A reset that happens while Lunar is closed is therefore not announced.
- **Settings:** the switch is saved as `usage.enabled` in `settings.json` (a yes/no only).
- **Logs:** only fixed event names and an error category (for example `usage.login.expired`), never a token, the address's reply or your figures.
