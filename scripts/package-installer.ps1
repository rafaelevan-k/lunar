<#
.SYNOPSIS
    Publishes Lunar self-contained for win-x64, checks the payload, and builds the per-user MSI with WiX (BE-29).

.DESCRIPTION
    Building the MSI restores the WiX Toolset SDK (ADR-0004), whose OSMF/EULA terms the OWNER must review and
    satisfy first (an agent cannot do that for them). The script therefore refuses to build the MSI unless
    -WixTermsReviewedByOwner is passed; without it, it still publishes and checks the payload and stops there.
    The MSI is unsigned unless -SigningCertificateThumbprint names a certificate in the current user's store.

.PARAMETER Version
    Three-part product version for the MSI (default 0.1.0).

.PARAMETER WixTermsReviewedByOwner
    Pass only after the owner has reviewed the WiX terms for the intended distribution.

.PARAMETER AcceptWixEula
    Passes AcceptEula=wix7 to the WiX build (WiX 7 refuses to build without it). Only the owner may choose this: it
    accepts the WiX OSMF EULA (and any fee that applies) on their behalf. Nothing is stored; it applies to this build only.

.PARAMETER SigningCertificateThumbprint
    Optional. Signs the MSI with signtool when that certificate exists. Without it the MSI is unsigned.
#>
[CmdletBinding()]
param(
    [string]$Version = '0.1.0',
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [string]$DotnetRoot,
    [string]$OutputDirectory,
    [switch]$WixTermsReviewedByOwner,
    [switch]$AcceptWixEula,
    [string]$SigningCertificateThumbprint
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

if ($Version -notmatch '^\d{1,3}\.\d{1,3}\.\d{1,5}$') { throw "Version '$Version' must look like 1.2.3." }

$root = Get-RepositoryRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts\installer' }
# WiX resolves relative paths against packaging\wix, so everything passed on is absolute (relative = from the current folder).
$OutputDirectory = [System.IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)).TrimEnd('\')
# The folder is deleted and recreated below: never let that be a drive root, the repository or one of its parents.
$rootFull = [System.IO.Path]::GetFullPath($root).TrimEnd('\')
if ($OutputDirectory.Length -le 3 -or $rootFull.StartsWith($OutputDirectory, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory '$OutputDirectory' is not a safe folder to replace; use a folder such as artifacts\installer-1.2.3."
}
$payload = Join-Path $OutputDirectory 'payload'

Initialize-DotnetEnvironment -DotnetRoot $DotnetRoot

if (Test-Path $OutputDirectory) { Remove-Item $OutputDirectory -Recurse -Force }
New-Item -ItemType Directory -Force $payload | Out-Null

# The win-x64 restore adds a runtime section to every packages.lock.json, which would make the normal locked
# restore fail afterwards (NU1004). Keep the committed files byte-for-byte: save them, publish, put them back.
$lockFiles = Get-ChildItem (Join-Path $root 'src') -Recurse -Filter '*packages.lock.json' -File
$savedLocks = @{}
foreach ($file in $lockFiles) { $savedLocks[$file.FullName] = [System.IO.File]::ReadAllBytes($file.FullName) }
try {
    Invoke-Dotnet 'Publish Lunar.Desktop (self-contained win-x64)' @(
        'publish', 'src\Lunar.Desktop\Lunar.Desktop.csproj', '-c', $Configuration, '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishTrimmed=false', '-p:PublishSingleFile=false', '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $payload)
}
finally {
    foreach ($path in $savedLocks.Keys) { [System.IO.File]::WriteAllBytes($path, $savedLocks[$path]) }
}

& (Join-Path $PSScriptRoot 'Test-InstallerPayload.ps1') -PayloadDirectory $payload
if ($LASTEXITCODE -ne 0) { throw 'The payload check failed; no installer was built.' }

if (-not $WixTermsReviewedByOwner) {
    Write-Host ''
    Write-Host 'Payload published and checked. The MSI was NOT built: pass -WixTermsReviewedByOwner once the owner has reviewed the WiX terms (ADR-0004).'
    exit 0
}

$wixArguments = @('build', 'packaging\wix\Lunar.Installer.wixproj', '-c', $Configuration,
    "-p:PayloadDir=$payload", "-p:ProductVersion=$Version", '-o', $OutputDirectory)
if ($AcceptWixEula) { $wixArguments += '-p:AcceptEula=wix7' }
Invoke-Dotnet 'Build per-user MSI (WiX)' $wixArguments

$msi = Join-Path $OutputDirectory 'Lunar.msi'
if (-not (Test-Path $msi)) { throw 'WiX finished without producing Lunar.msi.' }

if ($SigningCertificateThumbprint) {
    $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if (-not $signtool) { throw 'signtool.exe was not found; install the Windows SDK or build unsigned.' }
    & $signtool.Source sign /sha1 $SigningCertificateThumbprint /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $msi
    if ($LASTEXITCODE -ne 0) { throw 'Signing failed.' }
}
else {
    Write-Host 'No signing certificate given: the MSI is UNSIGNED (Defender SmartScreen may warn).'
}

$hash = (Get-FileHash $msi -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$msi.sha256" -Value "$hash  Lunar.msi" -Encoding Ascii
Write-Host "package-installer.ps1: OK. $msi"
Write-Host "SHA-256 $hash"
