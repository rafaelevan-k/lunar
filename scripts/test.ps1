<#
.SYNOPSIS
    Runs the Lunar test projects and writes TRX results.

.DESCRIPTION
    Suites:
      Gate      The pull-request gate. Domain, Application, Infrastructure and Conversion tests in full, plus only the
                architecture (layer-boundary) checks of the two Windows-session projects.
      Hardware  Windows.IntegrationTests and Desktop.UITests in full. They create real windows and enumerate real
                displays, so they need an interactive Windows session; results are evidence, not a merge gate.
      All       Every test project in full.
    All projects of a suite run even if one fails; the script exits 1 when any failed. Unit-test results are not
    manual Windows evidence.

.PARAMETER Suite
    Gate (default), Hardware or All.

.PARAMETER NoBuild
    Use the existing build output (run build.ps1 first).
#>
[CmdletBinding()]
param(
    [ValidateSet('Gate', 'Hardware', 'All')][string]$Suite = 'Gate',
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [string]$DotnetRoot,
    [string]$ResultsDirectory,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

$root = Get-RepositoryRoot
if (-not $ResultsDirectory) { $ResultsDirectory = Join-Path $root 'artifacts\test-results' }
$architectureFilter = 'FullyQualifiedName~LayerBoundaryTests|FullyQualifiedName~ArchitectureRuleTests'

# Name, filter ('' = every test).
$portable = @(
    @{ Name = 'Lunar.Domain.Tests'; Filter = '' },
    @{ Name = 'Lunar.Application.Tests'; Filter = '' },
    @{ Name = 'Lunar.Infrastructure.Tests'; Filter = '' },
    @{ Name = 'Lunar.Conversion.Tests'; Filter = '' }
)
$windowsFull = @(
    @{ Name = 'Lunar.Windows.IntegrationTests'; Filter = '' },
    @{ Name = 'Lunar.Desktop.UITests'; Filter = '' }
)
$windowsArchitectureOnly = @(
    @{ Name = 'Lunar.Windows.IntegrationTests'; Filter = $architectureFilter },
    @{ Name = 'Lunar.Desktop.UITests'; Filter = $architectureFilter }
)

switch ($Suite) {
    'Gate' { $projects = $portable + $windowsArchitectureOnly }
    'Hardware' { $projects = $windowsFull }
    'All' { $projects = $portable + $windowsFull }
}

Initialize-DotnetEnvironment -DotnetRoot $DotnetRoot
if (-not $NoBuild) {
    Invoke-Dotnet "Build ($Configuration)" @('build', 'Lunar.sln', '-c', $Configuration, '--no-restore')
}
New-Item -ItemType Directory -Force $ResultsDirectory | Out-Null

$failed = @()
foreach ($project in $projects) {
    $label = $project.Name
    if ($project.Filter) { $label = "$label (architecture checks only)" }
    $trx = if ($project.Filter) { "$($project.Name).architecture.trx" } else { "$($project.Name).trx" }
    $arguments = @(
        'test', "tests\$($project.Name)\$($project.Name).csproj", '-c', $Configuration, '--no-build', '--no-restore',
        '--logger', "trx;LogFileName=$trx", '--results-directory', $ResultsDirectory
    )
    if ($project.Filter) { $arguments += @('--filter', $project.Filter) }

    try {
        Invoke-Dotnet "Test $label" $arguments
    }
    catch {
        Write-Host "FAILED: $label" -ForegroundColor Red
        $failed += $label
    }
}

Write-Host ''
if ($failed.Count -gt 0) {
    Write-Host "test.ps1 ($Suite): FAILED in $($failed.Count) project(s):" -ForegroundColor Red
    $failed | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}
Write-Host "test.ps1 ($Suite): OK. Results in $ResultsDirectory"
