# Lunar

**A Dynamic Island for Windows 11, with a pair of eyes that watch over your desktop.**

[![Latest release](https://img.shields.io/github/v/release/rafaelevan-k/lunar?label=download&color=6f42c1)](https://github.com/rafaelevan-k/lunar/releases/latest)
![Windows 11 x64](https://img.shields.io/badge/Windows%2011-x64-0078D4)
[![License: Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE)

Lunar is a small pill at the top of your screen. It blinks and glances around while you work. Click it and it opens into
everything you reach for all day: sound and brightness, what's playing, a focus timer, a voice recorder, notes and a
file converter. Close it and it gets out of your way again.

It runs entirely on your PC, needs no account, sends no telemetry, uses almost no CPU, and **stays invisible when you
share your screen**.

**[⬇ Download Lunar for Windows 11](https://github.com/rafaelevan-k/lunar/releases/latest)**: one small installer, no
administrator rights needed.

---

## Why Lunar

- **It has a personality.** Instead of another toolbar, Lunar is a pair of vector eyes that blink, look around and smile
  when something good happens. It feels alive without ever getting in the way.
- **Invisible in screen shares, visible to you.** Every Lunar window is left out of Teams, Zoom and Meet screen sharing,
  screenshots and screen recordings. You see your controls, notes and timer; your audience never does.
- **Focus mode that really focuses.** Start a Pomodoro session and Lunar turns on Windows **Do Not Disturb** for you. It
  turns it off while you pause, and puts everything back exactly as it was when you finish.
- **A recorder that lives in the island.** Press Record and close the island. It shows live red sound lines, the time,
  and Pause and Stop buttons that work without opening anything. Your recordings are saved as ordinary MP3 files.
- **The island shows what's happening.** While recording or focusing, the closed island turns into a live recorder or
  countdown. When your focus time is over, the eyes come back and smile.
- **Private by design.** Your files, notes and recordings never leave your PC. There is no account and no telemetry.
  Logs never contain file names, note text, audio or what you listen to.
- **Light as a feather.** Sitting idle, Lunar uses about **0.04 % CPU** and about 95 MB of private memory, measured over
  five minutes on a typical laptop.
- **Keeps itself up to date, safely.** When a new version comes out, Lunar asks: **Install now** or **Later**. It
  installs only a download whose fingerprint matches the official release, then opens again by itself. You can turn the
  check off.
- **Made for everyone.** Full keyboard support, screen-reader names, a High Contrast theme and reduced motion. Dark and
  light themes. English and Bahasa Indonesia.

## What's inside

Click the eyes, then swipe or use the arrow keys to move between six pages:

| Page | What you get |
|---|---|
| **System** | Clock, volume and mute, screen brightness, Wi-Fi and Bluetooth. Everything follows changes you make elsewhere, like your keyboard's volume keys |
| **Media** | What's playing in any app that supports Windows media controls, with artwork, play/pause, previous, next, seeking, and switching between apps |
| **Pomodoro** | A focus timer from 1 to 180 minutes with quick presets. Do Not Disturb while it runs, a countdown in the closed island, and a pixel-art egg that hops while you focus and cracks into a sunny-side-up egg at the end |
| **File converter** | Convert between PNG, JPG and PDF, or turn pictures into a Word document, entirely offline. Drop in a batch, follow the queue, cancel or retry. Your originals are never touched |
| **Notes & Records** | Quick plain-text notes that save as you type, with Undo after a delete. The voice recorder: pick a microphone or let Windows choose, and get MP3 files in `Music\Lunar Recordings` or any folder you like |
| **Usage** *(optional)* | Your Claude 5-hour and weekly limits at a glance, with a small notice when a limit resets. Off until you turn it on |

### New in 1.0.6

- **Pomodoro** with Windows Do Not Disturb and a countdown in the closed island
- **Voice recorder** with live sound lines, Pause and Stop in the closed island
- **Automatic update check** with Install now or Later
- Smoother opening and closing of the island, and bolder buttons

## Install

1. Download **`Lunar.msi`** from the [latest release](https://github.com/rafaelevan-k/lunar/releases/latest).
2. Run it. It installs for your Windows account only and needs no administrator rights. A short progress bar
   appears and closes by itself when it's done.
3. Open **Lunar** from the Start menu. The eyes appear at the top of your screen.

> The installer isn't code-signed yet, so Windows SmartScreen may say "Windows protected your PC". Choose **More info**,
> then **Run anyway**. To check your download, compare `Get-FileHash .\Lunar.msi` with the `Lunar.msi.sha256` file on
> the release page.

- **Updating:** Lunar offers new versions itself. From version 1.0.5 or older, install the new `Lunar.msi` by hand once.
  Your settings, notes and recordings are always kept.
- **Removing:** Windows Settings → Apps → Lunar. Lunar asks whether to delete its settings and notes too. Your
  recordings stay in your own folder.

**Requirements:** Windows 11, 64-bit. Nothing else to install, because the .NET runtime is included.

## Your privacy

- Everything runs on your PC. Files, notes and recordings are never uploaded.
- Lunar contacts the internet in only two ways:
  - **The daily update check.** GitHub sees that someone asked. You can turn it off in Settings → Updates.
  - **The optional Usage page**, only after you switch it on.
- No account and no telemetry. The local log records what happened, never what you were doing.
- Lunar opens your microphone only when you press Record, and Windows shows its own microphone indicator.
  Please tell people before you record them.

Read the full [privacy notice](docs/PRIVACY.md) and the [user guide](docs/USER_GUIDE.md). Both are also installed next
to the app.

## Good to know

- Windows 11 x64 only.
- Word documents can be made from pictures, but Word files can't be converted to PDF yet.
- Wi-Fi and Bluetooth switches, seeking and external-monitor brightness depend on Windows, your drivers, the media app
  and the monitor. Where something isn't allowed, Lunar says so and opens the right Windows setting.
- Lunar turns on Do Not Disturb through a Windows feature Microsoft doesn't document. If Windows says no, the timer
  still runs and tells you.
- Being invisible to screen capture means you can't screenshot Lunar either.
- The Usage page uses an Anthropic address that isn't officially documented, so it may stop working. Lunar is not
  affiliated with Anthropic.
- Lunar has been tested on Windows 11 with a single monitor.

---

## For developers

Lunar is written in C# on **.NET 10** with **WPF** (MVVM). It also uses `Microsoft.Extensions.DependencyInjection`,
Serilog, SkiaSharp, the Open XML SDK, the Windows PDF renderer and Windows audio capture, with xUnit v3 for tests. The
installer is a per-user MSI built with the WiX Toolset. Packages are pinned with central package management and lock
files, and the SDK is pinned in `global.json`.

### Architecture

A modular monolith with dependencies pointing inward:

```
src/
  Lunar.Domain          pure business rules, no UI / OS / I/O
  Lunar.Application     use cases, models and ports (interfaces); depends only on Domain
  Lunar.Windows         Windows adapters: display, audio, microphone, network, Bluetooth, media, PDF, Do Not Disturb, installer hand-off
  Lunar.Conversion      file conversion providers, planner, queue and output transaction
  Lunar.Infrastructure  settings, logging, notes, recordings, Start with Windows, usage source, update feed
  Lunar.Desktop         WPF views, view models, themes and the composition root (the executable)
packaging/              WiX installer project, third-party notices and licence texts
tests/                  one test project per layer; architecture tests enforce the reference rules
scripts/                build, test, package and release-check scripts
```

The adapter projects (`Windows`, `Conversion`, `Infrastructure`) do not reference `Desktop` or each other. View models
never call native, file or persistence APIs directly. Architecture tests in every test project enforce these rules.
Security tests pin the few places that may use the network or start another program.

### Build, test and run

You need Windows 11 x64, the .NET SDK pinned in `global.json` (with Windows Desktop support) and PowerShell 5.1 or
later. A portable SDK in `.tools/dotnet` is picked up automatically.

```powershell
./scripts/build.ps1                          # locked restore, Release build (warnings are errors), format check
./scripts/test.ps1 -Suite Gate -NoBuild      # unit tests plus architecture checks
./scripts/test.ps1 -Suite All -NoBuild       # every suite, including Windows integration and WPF UI tests (needs a desktop session)
dotnet run --project src/Lunar.Desktop -c Release
```

Build and check the installer. You must first review and accept the WiX OSMF EULA:

```powershell
./scripts/package-installer.ps1 -Version 1.0.6 -OutputDirectory artifacts/installer-1.0.6 -WixTermsReviewedByOwner -AcceptWixEula
./scripts/Test-ReleasePackage.ps1 -MsiPath artifacts/installer-1.0.6/Lunar.msi -ExpectedVersion 1.0.6
```

**Publishing a release that Lunar can offer as an update:**
- tag it `vX.Y.Z`;
- don't mark it as a draft or prerelease;
- attach the installer under the exact name `Lunar.msi`.

GitHub Actions builds and tests every push on `windows-2022` and `windows-latest`; see [docs/CI.md](docs/CI.md).
Security notes are in [docs/SECURITY.md](docs/SECURITY.md).

## License

Lunar is licensed under the [Apache License, Version 2.0](LICENSE). Copyright 2026 Rafael Evan Kristanto; see
[NOTICE](NOTICE). Bundled third-party components keep their own licences. They are listed in
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) and shipped as `THIRD_PARTY_NOTICES.txt`.
