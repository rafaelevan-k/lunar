param([Parameter(Mandatory=$true)][string]$Workspace)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$root=[IO.Path]::GetFullPath((Join-Path $repo 'artifacts\sprint-0-converter')) + '\'
$work=[IO.Path]::GetFullPath($Workspace)
if(-not $work.StartsWith($root,[StringComparison]::OrdinalIgnoreCase)){throw 'Only controlled experiment workspace is allowed'}
$inputFile=Join-Path $work 'fixture-layout.docx'
$outputFile=Join-Path $work 'word-reference.pdf'
if(-not (Test-Path -LiteralPath $inputFile) -or (Test-Path -LiteralPath $outputFile)){throw 'Reference input missing or output already exists'}
$beforeHash=(Get-FileHash -LiteralPath $inputFile -Algorithm SHA256).Hash
$beforePids=@(Get-Process WINWORD -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
if($beforePids.Count -ne 0){throw 'Existing Word process detected; reference experiment will not share a user session'}
$word=$null; $document=$null; $owned=$false; $wordPid=0
$phase='CreateWordInstance'
function Write-Phase([string]$Name) {
    [ordered]@{Timestamp=[DateTimeOffset]::UtcNow.ToString('o');Experiment='DOCX-WordReferencePhase';Phase=$Name} |
        ConvertTo-Json -Compress | Write-Output
}
$launchStart=[DateTime]::UtcNow
try {
    Write-Phase $phase
    $word=New-Object -ComObject Word.Application
    $phase='CheckIsolation'
    Write-Phase $phase
    $candidates=@(Get-CimInstance Win32_Process -Filter "Name='WINWORD.EXE'" | Where-Object {
        $beforePids -notcontains $_.ProcessId -and $_.CreationDate.ToUniversalTime() -ge $launchStart -and
        $_.CommandLine -match '(?i)(automation|embedding)'})
    if($candidates.Count -ne 1){throw 'Automation process ownership could not be proven'}
    $wordPid=[int]$candidates[0].ProcessId
    $owned=$true
    $ownedProcess=Get-Process -Id $wordPid
    [ordered]@{ProcessId=$wordPid;StartTimeUtc=$ownedProcess.StartTime.ToUniversalTime().ToString('o')} |
        ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $work 'word-owned-process.json')
    $initialDocuments=$word.Documents.Count
    if($beforePids -contains $wordPid -or $initialDocuments -ne 0){throw 'Automation did not create an isolated empty Word instance; user session will not be modified'}
    $word.Visible=$false
    $phase='ConfigureOwnedInstance'
    Write-Phase $phase
    $word.DisplayAlerts=0
    $word.AutomationSecurity=3 # Force disable macros even though fixtures contain none.
    $phase='OpenSyntheticFixture'
    Write-Phase $phase
    $document=$word.Documents.Open($inputFile,$false,$true,$false)
    $phase='ComputePageCount'
    Write-Phase $phase
    $pages=$document.ComputeStatistics(2)
    $phase='ExportSyntheticPdf'
    Write-Phase $phase
    $document.ExportAsFixedFormat($outputFile,17)
    $version=$word.Version
    $document.Close(0)
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($document); $document=$null
    if((Get-FileHash -LiteralPath $inputFile -Algorithm SHA256).Hash -ne $beforeHash){throw 'DOCX fixture changed'}
    [ordered]@{Timestamp=[DateTimeOffset]::UtcNow.ToString('o');Experiment='DOCX-WordReferenceExport';Status='PASS';
        Details=@{Pages=$pages;WordVersion=$version;ReadOnly=$true;OriginalUnchanged=$true;IsolatedNewProcess=$true;
        Boundary='Reference only; Word is not a Lunar provider and LibreOffice was not tested'}} | ConvertTo-Json -Compress -Depth 4
}
catch {
    [ordered]@{Timestamp=[DateTimeOffset]::UtcNow.ToString('o');Experiment='DOCX-WordReferenceExport';Status='FAIL';
        Details=@{Phase=$phase;ErrorType=$_.Exception.GetType().Name;HResult=('0x{0:X8}' -f $_.Exception.HResult);IsolatedNewProcess=$owned;
        ExistingProcessReused=($beforePids -contains $wordPid);InitialDocumentCount=$initialDocuments;
        SafeReason=$(if($_.Exception.Message -eq 'Automation did not create an isolated empty Word instance; user session will not be modified'){'Isolation guard rejected instance'}else{'Automation exception; no raw message logged'})}} |
        ConvertTo-Json -Compress -Depth 4
    exit 1
}
finally {
    if($document -ne $null){$document.Close(0);[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($document)}
    if($word -ne $null){if($owned -and $word.Documents.Count -eq 0){$word.Quit(0)};[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($word)}
}
