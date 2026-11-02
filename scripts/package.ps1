<#
.SYNOPSIS
    Publishes Lunar.Desktop to a folder and zips it as a build-output archive with a SHA-256 file.

.DESCRIPTION
    This is a CI artifact for inspecting a build, NOT the release package. It is framework-dependent (it needs the
    .NET Desktop Runtime) and unsigned. The release shape accepted in ADR-0004 (win-x64 self-contained per-user MSI
    with WiX) belongs to BE-29/BE-30 and is deliberately not produced here: no WiX tool is restored or used.

.PARAMETER NoBuild
    Publish the existing build output (run build.ps1 first).
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [string]$DotnetRoot,
    [string]$OutputDirectory,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

$root = Get-RepositoryRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts\package' }
$publishDirectory = Join-Path $OutputDirectory 'Lunar.Desktop'

Initialize-DotnetEnvironment -DotnetRoot $DotnetRoot

# Identify the build by commit when git can tell, otherwise as 'local'.
$revision = 'local'
if (Get-Command git -ErrorAction SilentlyContinue) {
    $head = (& git -C $root rev-parse --short HEAD) 2>$null
    if ($LASTEXITCODE -eq 0 -and $head) { $revision = $head }
}

if (Test-Path $OutputDirectory) { Remove-Item $OutputDirectory -Recurse -Force }
New-Item -ItemType Directory -Force $publishDirectory | Out-Null

$arguments = @('publish', 'src\Lunar.Desktop\Lunar.Desktop.csproj', '-c', $Configuration, '--no-restore', '-o', $publishDirectory)
if ($NoBuild) { $arguments += '--no-build' }
Invoke-Dotnet 'Publish Lunar.Desktop (framework-dependent build output)' $arguments

$archive = Join-Path $OutputDirectory "Lunar.Desktop-build-$revision.zip"
Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $archive
$hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$archive.sha256" -Value "$hash  $(Split-Path $archive -Leaf)" -Encoding Ascii

Write-Host ''
Write-Host "package.ps1: OK. $archive"
Write-Host "SHA-256 $hash"
Write-Host 'Build-output archive only: unsigned, framework-dependent, not an installer (see ADR-0004, BE-29/BE-30).'
