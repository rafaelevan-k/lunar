# Lunar

Lunar is a floating "Dynamic Island" for Windows 11 (x64). A small always-on-top pill sits at the top centre of the
screen and shows an animated pair of eyes while idle. Click it to open quick controls: the system, media, a file
converter, notes with a voice recorder, a Pomodoro focus timer, and an optional Claude usage page. It runs locally, with no
account and no telemetry, checks GitHub once a day for a newer version (asking before it installs anything), and it stays
out of screen sharing and screenshots.

> **Status: 1.0.5 released** ([download](https://github.com/rafaelevan-k/lunar/releases/latest)). All planned features
> are built and tested, including notes, brightness and hiding from screen sharing (Sprint 8). Known limitations are
> listed below.

## What it does

| Area | Behaviour |
|---|---|
| Idle avatar | Vector eyes that blink and glance around, with reduced-motion support; click to open, Esc to close |
| System | Clock in your regional format; volume and mute and display brightness that follow outside changes; Wi-Fi and Bluetooth status and switches where Windows allows them, otherwise a link to Windows Settings |
| Media | The playing app's title, artist and artwork; play/pause, previous and next only when the app supports them; seeking only on seekable timelines; switching between several apps |
| File converter | Local conversions between PNG, JPG/JPEG and PDF, and pictures into Word (DOCX), with notes about what each route keeps or loses. Batch queue with cancel, retry and "show in folder". Originals are never changed |
| Pomodoro | A focus timer (1–180 minutes) that turns on Windows Do Not Disturb while it runs and puts it back after; the countdown shows in the closed island |
| Notes and recordings | Plain-text notes kept on this PC (create, edit, delete with Undo, saved on their own). A voice recorder saves MP3 files to a folder you choose, with Pause and Stop and live sound lines in the closed island |
| Updates | Once a day Lunar asks GitHub for the newest release and offers Install now or Later; the download must match the release's SHA-256 before it runs. Can be turned off |
| Usage (opt-in) | Claude 5-hour and weekly limits, read with the Claude Code sign-in on this PC; off by default, and no network request before it is turned on |
| Settings | Theme (Windows, dark, light), reduced motion, eye animation, display and position, Start with Windows, default output folder, English or Bahasa Indonesia, diagnostics |
| Screen sharing | Every Lunar window is left out of screen sharing, screenshots and recordings while staying on your own screen; always on |
| Accessibility | Keyboard navigation, screen-reader names, High Contrast palette, Windows animation setting respected |

Not in this version: Word (DOCX) to PDF or pictures (held until a layout engine is verified), PDF to editable Word,
OCR, voice assistant, cloud features, and updates installed without asking.

## Install

Download `Lunar.msi` from the [latest release](https://github.com/rafaelevan-k/lunar/releases/latest) and run it
(or build it yourself with `scripts/package-installer.ps1`, or `scripts\package-installer.cmd` from Command Prompt). It
installs for the current user only, to `%LocalAppData%\Programs\Lunar`, without administrator rights, and adds a Start
menu shortcut. The installer shows a short progress bar and closes by itself when it is done.
- **Upgrade:** from 1.0.6 on, Lunar offers new versions itself (Install now or Later). A newer MSI run by hand also replaces the program. Settings, notes, recordings and the Start with Windows choice are kept.
- **Uninstall:** Windows Settings → Apps. Lunar asks whether to delete its settings, notes and logs too.
- **Signing:** the installer is not signed, so expect a SmartScreen warning.
- **Docs:** the [user guide](docs/USER_GUIDE.md) and the [privacy notice](docs/PRIVACY.md) are installed next to the
  app, together with the third-party licence texts.

## Known limitations

- Windows 11 x64 only. Windows 10 is not supported: the bundled .NET 10 runtime does not support Windows 10 22H2.
- Word (DOCX) can be made from pictures, but Word files cannot be converted yet.
- Wi-Fi and Bluetooth switches depend on Windows and the device driver. Seeking depends on the media app.
- External monitor brightness needs DDC/CI and was not tested on a real external monitor.
- Lunar cannot be screenshotted or recorded, by design; notes are stored unencrypted in the user profile.
- Do Not Disturb is turned on through an interface Microsoft does not document; if Windows refuses, the timer still runs and says so.
- Updates are checked against the release's SHA-256 (integrity), not a code signature (the MSI is unsigned).
- The Usage page relies on an Anthropic address that is not officially documented, and it may stop working. Lunar is
  not affiliated with Anthropic.
- Tested on Windows 11 26H2 (build 26300) with one 125 % monitor. Not yet checked on a stable 24H2/25H2 build, with a
  second monitor or mixed scaling, with Narrator, over fullscreen apps, or over Remote Desktop.

## Tech stack

C# on .NET 10, WPF (MVVM), `Microsoft.Extensions.DependencyInjection`, Serilog, SkiaSharp, the Open XML SDK and the Windows
PDF renderer, xUnit v3. The packager is the WiX Toolset 7 (per-user MSI). Packages are pinned through central package
management and lock files, and the SDK is pinned to **10.0.402** in `global.json`.

## Architecture

A modular monolith with dependencies pointing inward:

```
src/
  Lunar.Domain          pure business rules, no UI / OS / I/O
  Lunar.Application     use cases, models and ports (interfaces); depends only on Domain
  Lunar.Windows         Windows API adapters (display, audio, network, Bluetooth, media, PDF) implementing Application ports
  Lunar.Conversion      file conversion providers, planner, queue and output transaction
  Lunar.Infrastructure  settings, logging, single instance, Start with Windows, usage source
  Lunar.Desktop         WPF views, view models, themes, composition root (the executable)
packaging/              WiX installer project, third-party notices and licence texts
tests/                  one test project per layer; architecture tests enforce the reference rules
scripts/                build, test, package and release-check scripts
```

The adapter projects (`Windows`, `Conversion`, `Infrastructure`) do not reference `Desktop` or each other. View models
never call native, file or persistence APIs directly. Tests in every test project check these rules.

## Requirements (to build)

- Windows 11, x64
- .NET SDK **10.0.402** with Windows Desktop support. `global.json` rejects other SDK versions. A portable copy
  placed in `.tools/dotnet` is picked up automatically by the scripts (that folder is git-ignored).
- PowerShell 5.1 or later

## Build, test and package

```powershell
./scripts/build.ps1                          # locked restore, Release build (warnings are errors), format check
./scripts/test.ps1 -Suite Gate -NoBuild      # unit tests plus architecture checks of every project
./scripts/test.ps1 -Suite All -NoBuild       # every suite, including Windows integration and WPF UI tests (needs a desktop session)
```

Build the installer. The owner must first review and accept the WiX OSMF EULA; the version must be 1.0.x or later:

```powershell
./scripts/package-installer.ps1 -Version 1.0.2 -OutputDirectory artifacts/installer-1.0.2 -WixTermsReviewedByOwner -AcceptWixEula
./scripts/Test-ReleasePackage.ps1 -MsiPath artifacts/installer-1.0.2/Lunar.msi -ExpectedVersion 1.0.2
```

After a dependency or runtime update, regenerate the notices with
`./scripts/Build-ThirdPartyNotices.ps1 -PayloadDirectory <payload>`. After installing, compare the installed copy
with its payload with `./scripts/Test-InstalledPayload.ps1 -PayloadDirectory <payload>`.

Run the app from source:

```powershell
dotnet run --project src/Lunar.Desktop -c Release
```

## Continuous integration

GitHub Actions (`.github/workflows/ci.yml`) runs on `windows-2022` and `windows-latest`. The `build-and-test` jobs
are the pull-request gate: build, analyzers, format, unit and architecture tests, and packaging. A separate non-blocking
job runs the tests that need a Windows desktop session. See [docs/CI.md](docs/CI.md). Blocking merges on failure
requires a branch protection rule in the repository settings.

## Privacy and security

Conversions and everything else run on your PC. Lunar does not upload files, send telemetry or check for updates. The
one network use is the opt-in Usage page, which asks Anthropic for your limits over HTTPS only after you turn it on.
Logs are code-only events, never file names, document contents, note text, Wi-Fi details or listening history. See
[docs/PRIVACY.md](docs/PRIVACY.md) and [docs/SECURITY.md](docs/SECURITY.md).

## License

Lunar is licensed under the [Apache License, Version 2.0](LICENSE). Copyright 2026 Rafael Evan Kristanto; see
[NOTICE](NOTICE). The installer ships both, as `LICENSE.txt` and `NOTICE.txt`.

Bundled third-party components keep their own licences. Their texts are shipped as `THIRD_PARTY_NOTICES.txt`,
generated into [packaging/THIRD_PARTY_NOTICES.txt](packaging/THIRD_PARTY_NOTICES.txt). The dependency inventory is
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
