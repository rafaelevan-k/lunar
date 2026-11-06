<#
.SYNOPSIS
    Checks that the installed Lunar is byte-for-byte the payload it was built from (BE-30).

.DESCRIPTION
    A successful msiexec exit code does not prove that an upgrade replaced the program files: Windows Installer keeps a versioned
    file whose version is not lower than the new one. This script compares every file of a publish payload with the installed
    copy by SHA-256 and lists the ones that differ or are missing. Developer files the MSI leaves out (*.pdb) are skipped.
    Run it in the user's own shell: the installed folder is under %LocalAppData%.

.PARAMETER PayloadDirectory
    The payload the MSI was built from (for example artifacts\installer-0.6.5\payload).

.PARAMETER InstallDirectory
    The installed Lunar folder. Defaults to %LocalAppData%\Programs\Lunar.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PayloadDirectory,
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\Lunar')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$payload = (Resolve-Path -LiteralPath $PayloadDirectory).Path
if (-not (Test-Path -LiteralPath $InstallDirectory -PathType Container)) { throw "Lunar is not installed at '$InstallDirectory'." }
$installed = (Resolve-Path -LiteralPath $InstallDirectory).Path

$checked = 0
$problems = New-Object System.Collections.Generic.List[string]
foreach ($file in Get-ChildItem -LiteralPath $payload -Recurse -File | Where-Object Extension -ne '.pdb') {
    $relative = $file.FullName.Substring($payload.Length).TrimStart('\')
    $target = Join-Path $installed $relative
    $checked++
    if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { $problems.Add("missing   $relative"); continue }
    if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) {
        $version = (Get-Item -LiteralPath $target).VersionInfo.FileVersion
        $problems.Add("different $relative (installed file version: $version)")
    }
}

# Files left from an older version (for example language folders that are no longer shipped) are reported too.
foreach ($file in Get-ChildItem -LiteralPath $installed -Recurse -File) {
    $relative = $file.FullName.Substring($installed.Length).TrimStart('\')
    if (-not (Test-Path -LiteralPath (Join-Path $payload $relative) -PathType Leaf)) { $problems.Add("extra     $relative (not in this payload)") }
}

$exe = Get-Item -LiteralPath (Join-Path $installed 'Lunar.Desktop.exe')
Write-Host "Installed Lunar.Desktop.exe: file version $($exe.VersionInfo.FileVersion), product version $($exe.VersionInfo.ProductVersion)"
if ($problems.Count -gt 0) {
    $problems | Select-Object -First 40 | ForEach-Object { Write-Host "FAIL  $_" }
    Write-Host "Test-InstalledPayload: $($problems.Count) of $checked files are not the payload's."
    exit 1
}
Write-Host "Test-InstalledPayload: OK ($checked files identical to the payload)."
