# Lunar

Lunar is a floating "Dynamic Island" for Windows 10/11 (x64). A small always-on-top pill sits at the top centre of the
screen, shows an animated pair of eyes while idle, and expands into quick controls for the system, media and file
conversion. It is offline-first: no account, no telemetry, no network backend.

> **Status: early development.** The application shell exists and runs, but the features below are planned, not
> built. Today the app shows an empty pill, a tray icon (Open / Exit), single-instance handling, settings storage and
> logging. It is not a finished product and has no installer yet.

## What it is meant to do

| Area | Plan |
|---|---|
| Idle avatar | Vector eyes that blink and follow the pointer, with reduced-motion support |
| System controls | Volume and mute, clock, Wi-Fi and Bluetooth status and actions (with Windows Settings fallbacks) |
| Media | Discover and control Windows media sessions, with seeking where the app supports it |
| File converter | Local PNG / JPG / PDF / DOCX conversions with honest fidelity labels; originals are never modified |
| Quality | Multi-monitor and DPI aware, keyboard accessible, high-contrast themes, English first (Indonesian planned) |

Non-goals for v1: voice assistant, cloud features, OCR, automatic updates, PDF to editable DOCX reconstruction.

## Built so far

- WPF shell: transparent, topmost, non-activating pill window; tray icon; single instance; top-centre placement that
  follows display, taskbar and DPI changes; idle 160×44 and expanded layouts; presentation state model.
- Design tokens (dark, light, high-contrast).
- Settings in `%LocalAppData%\Lunar\settings.json` (atomic writes, backup, corruption recovery, schema versioning).
- Bounded rolling diagnostic logs with redaction (diagnostics are opt-in).
- Application ports for system and media services, and a typed `Result` / error model.
- Build, test and package scripts and a Windows CI workflow.

## Tech stack

C# on .NET 10, WPF (MVVM), `Microsoft.Extensions.DependencyInjection`, Serilog, xUnit v3. Packages are pinned through
central package management and lock files. The SDK is pinned to **10.0.402** in `global.json`.

## Architecture

A modular monolith with dependencies pointing inward:

```
src/
  Lunar.Domain          pure business rules, no UI / OS / I/O
  Lunar.Application     use cases, models and ports (interfaces); depends only on Domain
  Lunar.Windows         Windows API adapters (display, audio, network, media) implementing Application ports
  Lunar.Conversion      file conversion adapters
  Lunar.Infrastructure  settings, logging, single-instance coordination
  Lunar.Desktop         WPF views, view models, themes, composition root (the executable)
tests/                  one test project per layer; architecture tests enforce the reference rules
scripts/                build.ps1, test.ps1, package.ps1
```

The adapter projects (`Windows`, `Conversion`, `Infrastructure`) do not reference `Desktop` or each other. View models
never call native, file or persistence APIs directly. These rules are checked by tests in every test project.

## Requirements

- Windows 10 or 11, x64
- .NET SDK **10.0.402** with Windows Desktop support. `global.json` rejects other SDK versions. A portable copy
  placed in `.tools/dotnet` is picked up automatically by the scripts (that folder is git-ignored).
- PowerShell 5.1 or later

## Build, test and run

```powershell
./scripts/build.ps1                          # locked restore, Release build (warnings are errors), format check
./scripts/test.ps1 -Suite Gate -NoBuild      # unit tests plus architecture checks of every project
./scripts/test.ps1 -Suite Hardware -NoBuild  # Windows integration and WPF UI tests (need an interactive desktop)
./scripts/package.ps1 -NoBuild               # unsigned archive of the build output (not an installer)
```

Run the app from the build output:

```powershell
dotnet run --project src/Lunar.Desktop -c Release
```

Raw `dotnet restore / build / test Lunar.sln` also works. Use `--locked-mode` for restore to honour the lock files.

## Continuous integration

GitHub Actions (`.github/workflows/ci.yml`) runs on `windows-2022` and `windows-latest`. The `build-and-test` jobs
are the pull-request gate (build, analyzers, format, unit and architecture tests, packaging). A separate non-blocking
job runs the tests that need a Windows desktop session. See [docs/CI.md](docs/CI.md). Blocking merges on failure
requires a branch protection rule in the repository settings.

## Privacy and security

Everything runs locally. Lunar does not upload files, send telemetry or check for updates. Logs are code-only
events, never file names, document contents, Wi-Fi details or listening history. See [docs/PRIVACY.md](docs/PRIVACY.md)
and [docs/SECURITY.md](docs/SECURITY.md).

## Roadmap

1. Foundation: solution, shell, tray, settings, logging, CI (done)
2. Eyes: animated vector eyes
3. Island navigation: expand / collapse and swipeable pages
4. System controls: volume, Wi-Fi, Bluetooth, clock
5. Media sessions
6. File conversion
7. Accessibility, performance, installer and release

## License

No license has been chosen yet, so all rights are reserved by default. Third-party components and their licenses are
listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
