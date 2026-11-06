<#
.SYNOPSIS
    Writes packaging\THIRD_PARTY_NOTICES.txt: the licence texts of every third-party component in a published payload (BE-30).

.DESCRIPTION
    The installer ships this file next to Lunar.Desktop.exe. MIT, Apache-2.0 and BSD terms ask that the copyright and
    licence notices travel with redistributed binaries; this file is that notice.

    It reads only local files. The licence texts come from the packages already restored into the NuGet cache. The
    Apache-2.0 text is the official one kept at packaging\licenses\Apache-2.0.txt. Each third-party binary in the payload
    is matched to its package by an identical file in the cache, so the versions named are the ones actually shipped.
    It fails when a binary in the payload belongs to no known component. That is the cue to add the new component here.
    Run it again after any dependency or runtime update, and commit the result.

.PARAMETER PayloadDirectory
    A publish folder from scripts\package-installer.ps1 (for example artifacts\installer-0.6.2\payload).
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PayloadDirectory)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

$root = Get-RepositoryRoot
$payload = (Resolve-Path -LiteralPath $PayloadDirectory).Path
$cache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$apache = Join-Path $root 'packaging\licenses\Apache-2.0.txt'
$output = Join-Path $root 'packaging\THIRD_PARTY_NOTICES.txt'

function Get-Mit([string]$Holder) {
    @"
MIT License

Copyright (c) $Holder

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
"@
}

# Which package each shipped third-party binary comes from. A binary not listed here (and not part of the runtime) fails the run.
$packages = [ordered]@{
    'DocumentFormat.OpenXml.dll'                            = 'documentformat.openxml'
    'DocumentFormat.OpenXml.Framework.dll'                  = 'documentformat.openxml.framework'
    'Microsoft.Extensions.DependencyInjection.dll'          = 'microsoft.extensions.dependencyinjection'
    'Microsoft.Extensions.DependencyInjection.Abstractions.dll' = 'microsoft.extensions.dependencyinjection.abstractions'
    'Serilog.dll'                                           = 'serilog'
    'Serilog.Sinks.File.dll'                                = 'serilog.sinks.file'
    'SkiaSharp.dll'                                         = 'skiasharp'
    'libSkiaSharp.dll'                                      = 'skiasharp.nativeassets.win32'
    'WinRT.Runtime.dll'                                     = 'microsoft.windows.sdk.net.ref'
    'Microsoft.Windows.SDK.NET.dll'                         = 'microsoft.windows.sdk.net.ref'
}

# The bundled runtime: its version is the one in the payload's runtimeconfig.
$config = Get-Content -LiteralPath (Join-Path $payload 'Lunar.Desktop.runtimeconfig.json') -Raw | ConvertFrom-Json
$frameworks = @{}
foreach ($framework in $config.runtimeOptions.includedFrameworks) { $frameworks[$framework.name] = $framework.version }
$core = Join-Path $cache "microsoft.netcore.app.runtime.win-x64\$($frameworks['Microsoft.NETCore.App'])"
$desktop = Join-Path $cache "microsoft.windowsdesktop.app.runtime.win-x64\$($frameworks['Microsoft.WindowsDesktop.App'])"
foreach ($folder in @($core, $desktop)) { if (-not (Test-Path -LiteralPath $folder)) { throw "Runtime pack not in the NuGet cache: $folder (restore once)." } }
$runtimeNames = @{}
foreach ($file in Get-ChildItem -LiteralPath $core, $desktop -Recurse -File) { $runtimeNames[$file.Name] = $true }

# Find the package version whose files contain the same bytes as the shipped binary.
$found = @{}
foreach ($file in Get-ChildItem -LiteralPath $payload -Recurse -File | Where-Object { $_.Extension -in '.dll', '.exe' }) {
    if ($file.Name -like 'Lunar.*' -or $runtimeNames.ContainsKey($file.Name)) { continue }
    if (-not $packages.Contains($file.Name)) { throw "No notice is known for '$($file.Name)'. Add its component to Build-ThirdPartyNotices.ps1." }
    $id = $packages[$file.Name]
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    $match = $null
    foreach ($versionFolder in Get-ChildItem -LiteralPath (Join-Path $cache $id) -Directory) {
        $same = Get-ChildItem -LiteralPath $versionFolder.FullName -Recurse -File -Filter $file.Name |
            Where-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash -eq $hash } | Select-Object -First 1
        if ($same) { $match = $versionFolder; break }
    }
    if (-not $match) { throw "'$($file.Name)' matches no version of $id in the NuGet cache." }
    $found[$id] = $match
}

function Read-Text([string]$Path) { (Get-Content -LiteralPath $Path -Raw).TrimEnd() }
function Get-Version([string]$Id) { if ($found.ContainsKey($Id)) { $found[$Id].Name } else { throw "Package $Id was expected in the payload but not found." } }
function Get-PackageFile([string]$Id, [string]$Name) { Read-Text (Join-Path $found[$Id].FullName $Name) }

$rule = '=' * 78
$sections = New-Object System.Collections.Generic.List[string]
function Add-Section([string]$Title, [string]$Body) { $sections.Add("$rule`n$Title`n$rule`n`n$($Body.TrimEnd())`n") }

Add-Section ".NET Runtime (Microsoft.NETCore.App $($frameworks['Microsoft.NETCore.App']), bundled)" (
    (Read-Text (Join-Path $core 'LICENSE.TXT')) + "`n`n--- Third-party notices of the .NET Runtime ---`n`n" + (Read-Text (Join-Path $core 'THIRD-PARTY-NOTICES.TXT')))
Add-Section ".NET Windows Desktop Runtime: WPF and Windows Forms (Microsoft.WindowsDesktop.App $($frameworks['Microsoft.WindowsDesktop.App']), bundled)" (Read-Text (Join-Path $desktop 'LICENSE'))
Add-Section "Microsoft.Extensions.DependencyInjection $(Get-Version 'microsoft.extensions.dependencyinjection') and .Abstractions $(Get-Version 'microsoft.extensions.dependencyinjection.abstractions')" (
    (Get-Mit 'Microsoft Corporation') + "`n`n--- Third-party notices of the package ---`n`n" + (Get-PackageFile 'microsoft.extensions.dependencyinjection' 'THIRD-PARTY-NOTICES.TXT'))
Add-Section "Open XML SDK: DocumentFormat.OpenXml $(Get-Version 'documentformat.openxml') and .Framework $(Get-Version 'documentformat.openxml.framework')" (Get-Mit 'Microsoft Corporation')
Add-Section "SkiaSharp $(Get-Version 'skiasharp')" (Get-PackageFile 'skiasharp' 'LICENSE.txt')
Add-Section "SkiaSharp native library: libSkiaSharp (SkiaSharp.NativeAssets.Win32 $(Get-Version 'skiasharp.nativeassets.win32')), including Skia" (
    (Get-PackageFile 'skiasharp.nativeassets.win32' 'LICENSE.txt') + "`n`n--- Third-party notices of the native library ---`n`n" + (Get-PackageFile 'skiasharp.nativeassets.win32' 'THIRD-PARTY-NOTICES.txt'))
Add-Section "C#/WinRT runtime: WinRT.Runtime (from Microsoft.Windows.SDK.NET.Ref $(Get-Version 'microsoft.windows.sdk.net.ref'))" (Get-Mit 'Microsoft Corporation')
Add-Section "Windows SDK for .NET projection: Microsoft.Windows.SDK.NET (from Microsoft.Windows.SDK.NET.Ref $(Get-Version 'microsoft.windows.sdk.net.ref'))" (
    "Copyright (c) Microsoft Corporation. All rights reserved.`nDistributed under the Microsoft Windows SDK license terms: https://aka.ms/WinSDKLicenseURL")
Add-Section "Serilog $(Get-Version 'serilog') and Serilog.Sinks.File $(Get-Version 'serilog.sinks.file')" (
    "Copyright (c) Serilog Contributors`n`nLicensed under the Apache License, Version 2.0 (full text below).`n`n" + (Read-Text $apache))

$header = @"
Lunar: third-party notices

Lunar includes the components below. Each is used under the licence shown with it. Lunar's own code is not covered
by these licences. This file is generated by scripts\Build-ThirdPartyNotices.ps1 from the packages shipped in this build.

"@
$text = $header + "`n" + ($sections -join "`n")
[System.IO.File]::WriteAllText($output, ($text -replace "`r?`n", "`r`n"), [System.Text.UTF8Encoding]::new($false))
Write-Host "Build-ThirdPartyNotices: wrote $output ($($sections.Count) components, $([math]::Round((Get-Item $output).Length / 1KB)) KB)."
