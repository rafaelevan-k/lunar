<#
.SYNOPSIS
    Checks that a publish folder holds everything the installer must ship (BE-29).

.DESCRIPTION
    The installer harvests the whole folder, so this is the check that the folder itself is right: the executable,
    the self-contained runtime host and every localized satellite assembly the Desktop build produces
    (for example id\Lunar.Desktop.resources.dll for Indonesian, FE-24). It also fails if the folder contains
    developer-only files (pdb, local settings, test assemblies). It runs no installer and needs no WiX tool.

.PARAMETER PayloadDirectory
    The folder produced by 'dotnet publish -r win-x64 --self-contained'.

.PARAMETER SatelliteCultures
    Cultures whose resources must be present. Defaults to the languages the app offers.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PayloadDirectory,
    [string[]]$SatelliteCultures = @('id')
)

$ErrorActionPreference = 'Stop'
$problems = New-Object System.Collections.Generic.List[string]

if (-not (Test-Path -LiteralPath $PayloadDirectory -PathType Container)) {
    throw "Payload folder '$PayloadDirectory' does not exist."
}
$root = (Resolve-Path -LiteralPath $PayloadDirectory).Path

$required = @('Lunar.Desktop.exe', 'Lunar.Desktop.dll', 'Lunar.Application.dll', 'Lunar.Domain.dll',
    'Lunar.Infrastructure.dll', 'Lunar.Windows.dll', 'Lunar.Conversion.dll', 'Lunar.Desktop.runtimeconfig.json',
    'hostfxr.dll', 'coreclr.dll', 'THIRD_PARTY_NOTICES.txt', 'USER_GUIDE.md', 'PRIVACY.md', 'LICENSE.txt', 'NOTICE.txt')
foreach ($culture in $SatelliteCultures) { $required += "$culture\Lunar.Desktop.resources.dll" }

foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $relative) -PathType Leaf)) { $problems.Add("Missing: $relative") }
}

# The runtime must be bundled: a self-contained build has no dependency on an installed .NET.
$config = Join-Path $root 'Lunar.Desktop.runtimeconfig.json'
if (Test-Path -LiteralPath $config) {
    $runtime = (Get-Content -LiteralPath $config -Raw | ConvertFrom-Json).runtimeOptions
    if ($runtime.PSObject.Properties.Name -contains 'framework' -or $runtime.PSObject.Properties.Name -contains 'frameworks') {
        $problems.Add('Not self-contained: runtimeconfig.json names a shared framework (ADR-0004 requires self-contained).')
    }
}

# Only the languages Lunar offers ship translations (SatelliteResourceLanguages): any other folder of *.resources.dll is dead weight.
$allowedCultures = @('en') + $SatelliteCultures
foreach ($folder in Get-ChildItem -LiteralPath $root -Directory) {
    if (@(Get-ChildItem -LiteralPath $folder.FullName -Filter '*.resources.dll' -File).Count -gt 0 -and $allowedCultures -notcontains $folder.Name) {
        $problems.Add("Unused language folder ships: $($folder.Name)\")
    }
}

# The notices must name every bundled third-party binary's component (BE-30): an empty or stale file is not a notice.
$notices = Join-Path $root 'THIRD_PARTY_NOTICES.txt'
if (Test-Path -LiteralPath $notices) {
    $text = Get-Content -LiteralPath $notices -Raw
    foreach ($component in @('.NET Runtime', 'Windows Desktop Runtime', 'SkiaSharp', 'libSkiaSharp', 'Serilog', 'Open XML SDK', 'DependencyInjection', 'WinRT.Runtime', 'Apache License')) {
        if ($text -notmatch [regex]::Escape($component)) { $problems.Add("THIRD_PARTY_NOTICES.txt does not mention '$component'.") }
    }
}

$forbidden = Get-ChildItem -LiteralPath $root -Recurse -File |
    Where-Object { ($_.Extension -eq '.pdb' -and $_.Name -like 'Lunar.*') -or $_.Extension -eq '.user' -or $_.Name -like '*.Tests.dll' -or $_.Name -like '*.private.log' -or $_.Name -like 'xunit*' }
foreach ($file in $forbidden) { $problems.Add("Must not ship: $($file.FullName.Substring($root.Length + 1))") }

if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host "FAIL  $_" }
    exit 1
}
Write-Host "Test-InstallerPayload: OK ($((Get-ChildItem -LiteralPath $root -Recurse -File).Count) files; satellites: $($SatelliteCultures -join ', '))."
