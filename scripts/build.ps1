<#
.SYNOPSIS
    Restores (locked), builds and checks formatting for Lunar.sln.

.DESCRIPTION
    The same steps CI runs. Analyzer findings and warnings fail the build (Directory.Build.props treats warnings
    as errors); restore uses --locked-mode so a package change without a lock-file update fails; formatting is
    verified, never rewritten. Run on Windows x64 with the SDK pinned in global.json.

.PARAMETER Configuration
    Release (default) or Debug.

.PARAMETER DotnetRoot
    Folder containing dotnet.exe. Defaults to .tools\dotnet in the repository when present, else PATH.

.PARAMETER SkipFormat
    Skip 'dotnet format --verify-no-changes'.
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [string]$DotnetRoot,
    [switch]$SkipFormat
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

Initialize-DotnetEnvironment -DotnetRoot $DotnetRoot
Invoke-Dotnet 'Restore (locked mode)' @('restore', 'Lunar.sln', '--locked-mode')
Invoke-Dotnet "Build ($Configuration)" @('build', 'Lunar.sln', '-c', $Configuration, '--no-restore')
if (-not $SkipFormat) {
    Invoke-Dotnet 'Verify formatting' @('format', 'Lunar.sln', '--verify-no-changes', '--no-restore')
}
Write-Host ''
Write-Host 'build.ps1: OK'
