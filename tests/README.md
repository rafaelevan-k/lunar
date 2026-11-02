# Lunar tests

BE-01 supplies six canonical test projects. Each currently runs four architecture
fitness checks through the linked `LayerBoundaryTests.cs`: declared references,
compiled references, forbidden type namespaces and discovery-code exclusion.
Application tests also exercise allowed/forbidden guard cases and solution membership.
These are structural tests, including in the `IntegrationTests` and `UITests` projects;
they are not device integration, UI automation or manual Windows evidence.

Run from a checkout with SDK 10.0.402, on Windows x64 for the full solution. The scripts below are the same steps CI
runs (see [docs/CI.md](../docs/CI.md)); the raw commands also work:

```powershell
./scripts/build.ps1                          # locked restore, Release build, format check
./scripts/test.ps1 -Suite Gate -NoBuild      # pull-request gate: unit tests + architecture checks of every project
./scripts/test.ps1 -Suite Hardware -NoBuild  # Windows.IntegrationTests and Desktop.UITests in full

dotnet restore Lunar.sln --locked-mode
dotnet build Lunar.sln -c Release --no-restore
dotnet test Lunar.sln -c Release --no-build --no-restore
```

`Lunar.Desktop.UITests` runs its tests one at a time (`AssemblyConfiguration.cs`): WPF's XAML loader is not safe
under concurrent parsing and parallel runs failed intermittently.

The tests locate the source checkout above their output directory. Domain,
Application, Conversion and Infrastructure target portable `net10.0`; Windows
and Desktop tests target `net10.0-windows`. Only Windows execution was verified
for BE-01. Future feature tasks add their own behavior/integration tests here.

Guard limits: compiled metadata detects referenced types, not arbitrary strings,
reflection, native calls hidden behind other libraries, or unused source imports.
Declared-reference checks inspect the layer project file; evaluated MSBuild inputs
and build properties are additionally captured in BE-01 evidence. Adapter use in
Desktop remains restricted to the composition root by code review. An intentional
FE-02 production `App` requires updating the BE-01 discovery-entry-point assertion
alongside the reviewed promotion; no spike is automatically adopted.
