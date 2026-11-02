# Lunar

Windows Dynamic Island utility. Sprint 0 discovery was closed by the user on 2026-10-27 under accepted limitations; the subsequent BE-01 assignment starts the Sprint 1 solution scaffold. This is not a finished product. See [BE-01 evidence and manual checklist](docs/evidence/sprint-1/BE-01.md) for verified scope and [current status](docs/SPRINT_STATUS.md).

Read [AGENTS.md](AGENTS.md), [PRD.md](PRD.md), the sole canonical [ARCHITECTURE.md](ARCHITECTURE.md), [FE_TASK.md](FE_TASK.md), [BE_TASK.md](BE_TASK.md), then [Sprint status](docs/SPRINT_STATUS.md).

## Build the BE-01 scaffold

Install SDK **10.0.402** with WindowsDesktop support on Windows x64, or use the
existing ignored portable SDK below. `global.json` rejects other SDK versions.

```powershell
$env:DOTNET_ROOT = Join-Path $PWD '.tools/dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:NUGET_PACKAGES = Join-Path $PWD '.tools/nuget'
& .\.tools\dotnet\dotnet.exe restore Lunar.sln --locked-mode
& .\.tools\dotnet\dotnet.exe build Lunar.sln -c Release --no-restore
& .\.tools\dotnet\dotnet.exe test Lunar.sln -c Release --no-build --no-restore
& .\.tools\dotnet\dotnet.exe format Lunar.sln --verify-no-changes --no-restore
```

For a system-installed matching SDK, use `dotnet` and omit `DOTNET_ROOT` above.
`Lunar.sln` contains the six canonical production projects and six test projects.
Production output is `bin/<project>/Release/<tfm>/`; intermediate files are isolated
under `obj/<project>/`. SDK analyzers, nullable checks, deterministic output and
warning-as-error policy apply to the scaffold; tests use centrally pinned xUnit v3
with MTP disabled, Test SDK and VSTest adapter packages.

Desktop is currently a **WPF library**, with no production entry point or shell.
It deliberately excludes retained discovery App/window/tray sources. FE-02/BE-05
will introduce the executable; MVVM/DI, shell, tray, positioning, settings and state
machine implementation remain assigned tasks. The current tests enforce architecture
and do not prove those behaviors. See [test scope](tests/README.md).

## Run the Sprint 0 spike

Windows x64, interactive desktop, SDK **10.0.402** with WindowsDesktop support. The inspected system default SDK is 9.0.306. A verified portable SDK is available locally at `.tools/dotnet/dotnet.exe`; it is ignored and not part of source distribution. Other checkouts must supply this exact SDK or review an intentional pin update. Use an ordinary standard-user development shell; no elevated runtime is required.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\Invoke-Sprint0.ps1 -WindowSmoke
```

Restores SDK-managed dependencies, builds both temporary projects, reads Windows capabilities and shows a five-second WPF window. Omit `-WindowSmoke` for build/native probes only. The runner writes a fresh timestamped directory under `docs/evidence/sprint-0/`, redacts workspace/profile paths from build logs and does not change volume, radios, playback, startup or registry. Read each probe result; runner exit 0 alone does not prove every capability.

For an interactive prototype after building:

```powershell
.\.tools\dotnet\dotnet.exe src\Lunar.Desktop\bin\Release\net10.0-windows\Lunar.Desktop.Spike.dll
```

Tray offers Open prototype and Exit. It has no settings, animated eyes, controls, media UI or converter. Mouse/focus/tray/manual OS/DPI matrix cases remain unverified. The floating prototype remains until Exit or keyboard Escape after intentional activation; the smoke command exits itself.

See [observed results and failures](docs/SPIKE_RESULTS.md), [documentation index](docs/README.md), [accepted ADRs](docs/ADR/README.md) and [limitations/manual matrix](docs/COMPATIBILITY.md). [User-approved ADR-0005 and Sprint 0 closure](docs/evidence/sprint-0/closure-20261027/APPROVAL.md) retain explicit later-task validation deadlines. The later BE-01 assignment authorizes only that task. Prototype reuse requires review/refactoring and acceptance verification.

The authorized Sprint 0 continuation added a disposable converter CLI, dependency/signature/advisory evidence and scoped synthetic image/PDF/DOCX-embedding experiments. See [converter results/reproduction](docs/CONVERTER_DISCOVERY.md) and [exit review](docs/SPRINT_0_EXIT_REVIEW.md). User accepted five ADRs and discovery closure on 2026-10-27; DOCX fidelity and target/manual shell evidence remain unverified with tracked deadlines. That closure was documentation/evidence-check maintenance only; the subsequent BE-01 assignment separately authorizes the current scaffold.
