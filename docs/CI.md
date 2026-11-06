# Continuous integration and quality gates

Owner: BE-02. Workflow: [`.github/workflows/ci.yml`](../.github/workflows/ci.yml). Scripts: [`scripts/`](../scripts).
CI runs on Windows runners only: a Linux or macOS build would not prove anything about WPF or Windows APIs
(ARCHITECTURE section 12).

## Run the same checks locally

Windows x64, SDK pinned in `global.json` (10.0.402). The scripts use `.tools\dotnet` when it exists, else the `dotnet`
on `PATH`; pass `-DotnetRoot <folder>` to choose another. They run in Windows PowerShell 5.1 and PowerShell 7.

```powershell
./scripts/build.ps1                    # locked restore, Release build (warnings and analyzers are errors), format check
./scripts/test.ps1 -Suite Gate -NoBuild    # the pull-request gate
./scripts/test.ps1 -Suite Hardware -NoBuild  # Windows-session tests (real windows and displays)
./scripts/package.ps1 -NoBuild         # unsigned build-output archive + SHA-256 in artifacts/package
```

## What the CI jobs do

| Job | Runners | Blocks a merge? | Content |
|---|---|---|---|
| `build-and-test` | `windows-2022`, `windows-latest` | Yes, once marked required (below) | `build.ps1`, `test.ps1 -Suite Gate`, `package.ps1`; uploads TRX results and the build archive, kept 14 days |
| `windows-session-tests` | `windows-latest` | No (`continue-on-error`) | `test.ps1 -Suite Hardware`: `Lunar.Windows.IntegrationTests` and `Lunar.Desktop.UITests` in full; uploads TRX results, kept 14 days |

The **gate suite** is: Domain, Application, Infrastructure and Conversion tests in full, plus the architecture
(layer-boundary) checks of the two Windows-session projects. The architecture checks are the `LayerBoundaryTests`
(declared references, compiled references, forbidden namespaces, no discovery code in production) and, in
Application, `ArchitectureRuleTests`. A failing test or a layer violation makes `test.ps1` exit 1 and fails the job.
A package change without a lock-file update, a warning, an analyzer finding or a formatting difference fails
`build.ps1`.

Why the Windows-session tests are not a gate: they create real WPF windows and enumerate the runner's displays, which
depends on the runner's desktop session. ARCHITECTURE section 12 says hardware-dependent tests must not decide a merge
on a headless runner. Their results are evidence and never replace the manual Windows checks recorded per task.

`package.ps1` is **not** the release package. It produces an unsigned, framework-dependent folder archive for
inspecting a build. The release shape accepted in ADR-0004 (win-x64 self-contained per-user MSI, WiX) belongs to
BE-29/BE-30; no WiX tool is restored or used by these scripts.

## One-time repository settings (not expressible in files)

A workflow can fail a pull request, but **blocking the merge is a GitHub setting**. After the first CI run, in the
repository: Settings, Branches (or Rules), add a rule for `main` that requires the status checks
`build-and-test (windows-2022)` and `build-and-test (windows-latest)` to pass, and optionally requires a pull request.
Without that rule a failed check is visible but does not stop a push or merge. Pushing directly to `main` bypasses pull
request checks entirely; CI then reports after the fact.

## Known limits

- The first GitHub run found one test that assumed a screen larger than the runner's; it now uses a small fake display.
- Actions are pinned by major version tag (`@v4`), not by commit SHA. Pinning by SHA, and keeping them updated, is a
  hardening step for a later decision.
- `setup-dotnet` must find SDK 10.0.402 for `windows-2022`; if a runner image lacks it the setup step fails loudly (the
  repository pins the SDK with `rollForward: disable`).
- Idle CPU/RAM, conversion memory and security-regression checks (ARCHITECTURE section 12, items 6 and 7) are not part
  of this workflow yet; they belong to the tasks that create the behavior they measure. Idle CPU/RAM can be sampled by
  hand from a running Lunar with `scripts/Measure-Idle.ps1` (FE-25); it is evidence, not a gate.
- No release signing, publishing, telemetry or network access beyond restoring NuGet packages and GitHub-hosted
  actions.
