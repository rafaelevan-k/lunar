<#
.SYNOPSIS
    Checks a built Lunar.msi against the release rules (BE-30). Read-only: it installs nothing.

.DESCRIPTION
    Opens the MSI database read-only through the Windows Installer COM API and checks what the package promises:
      - the version asked for, and its .sha256 file when present;
      - per-user install that needs no elevation (no ALLUSERS=1, Word Count bit "elevation not required");
      - the uninstall clean-up custom action, deferred and impersonated, scheduled only on a full uninstall;
      - a major upgrade that removes the old version inside the install transaction (rollback keeps it);
      - nothing written to HKLM, and no Run value written by the installer (Start with Windows stays opt-in);
      - no debug symbols or test assemblies in the File table, and the executable present;
      - the size of the MSI, and its Authenticode signature (reported; required only with -RequireSigned).
    Prints one line per check and exits 1 when any required check fails.

.PARAMETER MsiPath
    The Lunar.msi to check.

.PARAMETER ExpectedVersion
    Optional three-part version the MSI must carry.

.PARAMETER RequireSigned
    Fail when the MSI has no valid Authenticode signature.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$MsiPath,
    [string]$ExpectedVersion,
    [switch]$RequireSigned
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $MsiPath -PathType Leaf)) { throw "MSI '$MsiPath' does not exist." }
$msi = [string](Resolve-Path -LiteralPath $MsiPath).Path
$results = New-Object System.Collections.Generic.List[object]
function Add-Result([string]$Check, [bool]$Pass, [string]$Detail, [bool]$Required = $true) {
    $results.Add([pscustomobject]@{ Check = $Check; Pass = $Pass; Required = $Required; Detail = $Detail })
}

$installer = $null; $database = $null
$com = New-Object System.Collections.Generic.List[object]
function Invoke-Com($Target, [string]$Name, [string]$Kind, [object[]]$Arguments) {
    $Target.GetType().InvokeMember($Name, $Kind, $null, $Target, $Arguments)
}
function Get-Rows([string]$Sql, [int]$Columns) {
    $rows = New-Object System.Collections.Generic.List[object]
    $view = Invoke-Com $database 'OpenView' 'InvokeMethod' @($Sql)
    $com.Add($view)
    [void](Invoke-Com $view 'Execute' 'InvokeMethod' $null)
    while ($true) {
        $record = Invoke-Com $view 'Fetch' 'InvokeMethod' $null
        if ($null -eq $record) { break }
        $com.Add($record)
        $values = @(for ($i = 1; $i -le $Columns; $i++) { [string](Invoke-Com $record 'StringData' 'GetProperty' @($i)) })
        $rows.Add($values)
    }
    [void](Invoke-Com $view 'Close' 'InvokeMethod' $null)
    return , $rows
}
function Test-Table([string]$Table) {
    $rows = Get-Rows "SELECT ``Name`` FROM ``_Tables`` WHERE ``Name``='$Table'" 1
    return $rows.Count -gt 0
}

try {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = Invoke-Com $installer 'OpenDatabase' 'InvokeMethod' @($msi, 0)

    $properties = @{}
    foreach ($row in (Get-Rows 'SELECT `Property`, `Value` FROM `Property`' 2)) { $properties[$row[0]] = $row[1] }

    $version = $properties['ProductVersion']
    if ($ExpectedVersion) { Add-Result 'Version' ($version -eq $ExpectedVersion) "MSI says '$version', expected '$ExpectedVersion'" }
    else { Add-Result 'Version' ([bool]$version) "MSI says '$version'" }
    Add-Result 'Product name' ($properties['ProductName'] -eq 'Lunar') "ProductName='$($properties['ProductName'])'"
    # Lunar's icon in Settings > Apps and on its Start menu shortcut (so in Windows Search); without it both were blank (Sprint 8).
    $icons = @(Get-Rows 'SELECT `Name` FROM `Icon`' 1 | ForEach-Object { $_[0] })
    $shortcutIcons = @(Get-Rows 'SELECT `Shortcut`, `Icon_` FROM `Shortcut`' 2 | Where-Object { $_[1] })
    Add-Result 'Lunar icon for Apps and Start' ($properties['ARPPRODUCTICON'] -and $icons -contains $properties['ARPPRODUCTICON'] -and $shortcutIcons.Count -gt 0) "ARPPRODUCTICON='$($properties['ARPPRODUCTICON'])', icons: $($icons -join ', '), shortcuts with an icon: $($shortcutIcons.Count)"

    $allUsers = $properties['ALLUSERS']
    Add-Result 'Per-user install' (-not $allUsers -or $allUsers -eq '2') "ALLUSERS='$allUsers', MSIINSTALLPERUSER='$($properties['MSIINSTALLPERUSER'])'"

    $summary = Invoke-Com $database 'SummaryInformation' 'GetProperty' @(0)
    $com.Add($summary)
    $wordCount = [int](Invoke-Com $summary 'Property' 'GetProperty' @(15))
    Add-Result 'No elevation needed' (($wordCount -band 8) -ne 0) "Word Count=$wordCount (bit 8 = elevated privileges not required)"

    # Uninstall clean-up (ADR-0018): deferred (0x400) + no impersonation flag absent (0x800 off) + ignore return (0x40).
    $actions = Get-Rows 'SELECT `Action`, `Type`, `Target` FROM `CustomAction`' 3
    $cleanup = @($actions | Where-Object { $_[0] -eq 'LunarUninstallCleanup' })
    if ($cleanup.Count -eq 1) {
        $type = [int]$cleanup[0][1]
        $deferred = ($type -band 0x400) -ne 0
        $impersonated = ($type -band 0x800) -eq 0
        Add-Result 'Uninstall clean-up action' ($deferred -and $impersonated -and $cleanup[0][2] -match '--uninstall-cleanup') "Type=$type deferred=$deferred impersonated=$impersonated target='$($cleanup[0][2])'"
        $condition = (Get-Rows 'SELECT `Condition` FROM `InstallExecuteSequence` WHERE `Action`=''LunarUninstallCleanup''' 1)
        $text = if ($condition.Count -gt 0) { $condition[0][0] } else { '' }
        Add-Result 'Clean-up only on full uninstall' ($text -match 'REMOVE="ALL"' -and $text -match 'NOT UPGRADINGPRODUCTCODE') "Condition='$text'"
    }
    else {
        Add-Result 'Uninstall clean-up action' $false "found $($cleanup.Count) LunarUninstallCleanup actions"
    }

    $sequence = @{}
    foreach ($row in (Get-Rows 'SELECT `Action`, `Sequence` FROM `InstallExecuteSequence`' 2)) { $sequence[$row[0]] = [int]$row[1] }
    $rep = $sequence['RemoveExistingProducts']; $init = $sequence['InstallInitialize']
    Add-Result 'Upgrade keeps rollback' ($null -ne $rep -and $null -ne $init -and $rep -gt $init -and $rep -lt ($sequence['InstallFinalize'])) "InstallInitialize=$init RemoveExistingProducts=$rep InstallFinalize=$($sequence['InstallFinalize'])"

    $hklm = 0; $run = 0
    if (Test-Table 'Registry') {
        foreach ($row in (Get-Rows 'SELECT `Root`, `Key`, `Name` FROM `Registry`' 3)) {
            if ($row[0] -eq '2') { $hklm++ }
            if ($row[1] -match 'CurrentVersion\\Run') { $run++ }
        }
    }
    Add-Result 'Nothing in HKLM' ($hklm -eq 0) "$hklm HKLM rows"
    Add-Result 'Start with Windows stays opt-in' ($run -eq 0) "$run Run-key rows written by the installer"

    $files = Get-Rows 'SELECT `FileName`, `FileSize` FROM `File`' 2
    # Upgrades replace a versioned file only when its version is higher: Lunar's own assemblies must carry this product's version.
    $own = Get-Rows 'SELECT `FileName`, `Version` FROM `File`' 2
    $ownLunar = @($own | Where-Object { (($_[0] -split '\|')[-1]) -match '^Lunar\..*\.(dll|exe)$' })
    $wrong = @($ownLunar | Where-Object { $_[1] -ne "$version.0" } | ForEach-Object { "$((($_[0] -split '\|')[-1]))=$($_[1])" })
    Add-Result 'Lunar files carry the product version' ($ownLunar.Count -gt 0 -and $wrong.Count -eq 0) ($(if ($wrong.Count) { 'expected ' + "$version.0" + ': ' + ($wrong -join ', ') } else { "$($ownLunar.Count) Lunar files at $version.0" }))
    $names = @($files | ForEach-Object { ($_[0] -split '\|')[-1] })
    $payloadBytes = ($files | ForEach-Object { [long]$_[1] } | Measure-Object -Sum).Sum
    Add-Result 'Executable present' ($names -contains 'Lunar.Desktop.exe') "$($files.Count) files, $([math]::Round($payloadBytes / 1MB, 1)) MB unpacked"
    $dev = @($names | Where-Object { $_ -like '*.pdb' -or $_ -like '*.Tests.dll' -or $_ -like 'xunit*' })
    Add-Result 'No developer files' ($dev.Count -eq 0) ($(if ($dev.Count) { $dev -join ', ' } else { 'none' }))
    $notices = @($names | Where-Object { $_ -eq 'THIRD_PARTY_NOTICES.txt' })
    Add-Result 'Third-party notices shipped' ($notices.Count -gt 0) ($(if ($notices.Count) { $notices -join ', ' } else { 'THIRD_PARTY_NOTICES.txt is not in the package' }))
    $docs = @('USER_GUIDE.md', 'PRIVACY.md', 'LICENSE.txt', 'NOTICE.txt' | Where-Object { $names -notcontains $_ })
    Add-Result 'Guide, privacy, licence shipped' ($docs.Count -eq 0) ($(if ($docs.Count) { 'missing: ' + ($docs -join ', ') } else { 'USER_GUIDE.md, PRIVACY.md, LICENSE.txt, NOTICE.txt' }))
    # Satellite resources only for English and Indonesian: the Directory table names each language folder.
    $dirRows = Get-Rows 'SELECT `DefaultDir` FROM `Directory`' 1
    $folders = @(foreach ($row in $dirRows) { ($row[0] -split '\|')[-1] })
    $extra = @($folders | Where-Object { $_ -match '^[a-z]{2}(-[A-Za-z]{2,4})?$' -and $_ -notin @('en', 'id') })
    Add-Result 'Only English and Indonesian translations' ($extra.Count -eq 0) ($(if ($extra.Count) { 'unused language folders: ' + ($extra -join ', ') } else { 'no other language folders' }))
}
finally {
    for ($i = $com.Count - 1; $i -ge 0; $i--) { [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($com[$i]) }
    foreach ($object in @($database, $installer)) {
        if ($null -ne $object) { [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($object) }
    }
}

$size = (Get-Item -LiteralPath $msi).Length
Add-Result 'MSI size' $true "$([math]::Round($size / 1MB, 1)) MB" $false
$sha = "$msi.sha256"
if (Test-Path -LiteralPath $sha) {
    $expected = ((Get-Content -LiteralPath $sha -Raw).Trim() -split '\s+')[0]
    $actual = (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash
    Add-Result 'SHA-256 file matches' ($expected -ieq $actual) "file=$expected actual=$($actual.ToLowerInvariant())"
}
$signature = Get-AuthenticodeSignature -LiteralPath $msi
Add-Result 'Signed' ($signature.Status -eq 'Valid') "Authenticode status: $($signature.Status)" ([bool]$RequireSigned)

$failed = 0
foreach ($result in $results) {
    $mark = if ($result.Pass) { 'PASS' } elseif ($result.Required) { 'FAIL'; $failed++ } else { 'WARN' }
    Write-Host ('{0,-5} {1,-34} {2}' -f $mark, $result.Check, $result.Detail)
}
if ($failed -gt 0) { Write-Host "Test-ReleasePackage: $failed required check(s) failed."; exit 1 }
Write-Host 'Test-ReleasePackage: OK (required checks pass; WARN lines are reported, not required).'
