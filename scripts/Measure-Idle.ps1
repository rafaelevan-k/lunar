<#
.SYNOPSIS
  Samples a RUNNING Lunar process while it sits idle and writes CPU and memory figures (FE-25, PRD "Performance budgets").

.DESCRIPTION
  Read-only: it only reads the operating system's own counters for the process (CPU time, working set, private bytes, handles,
  threads). It starts nothing, attaches nothing and changes nothing in Lunar. Leave the island collapsed and do not touch it while it
  runs; any use of the island during the run is activity, not idle, and the result must be described that way.

  CPU % is reported two ways because the PRD says only "idle CPU <1%":
    * of the whole machine  = CPU time used / (elapsed time x logical processors)   <- the figure compared with the 1 % budget
    * of one core           = CPU time used / elapsed time                           <- stricter, for reference

.PARAMETER Minutes    How long to sample (PRD: averaged over 5 minutes).
.PARAMETER IntervalSeconds  Seconds between samples.
.PARAMETER ProcessId  The Lunar process; default: the single process named Lunar.Desktop.
.PARAMETER OutDirectory  Where the CSV and the summary go; default docs/evidence/sprint-7.
.PARAMETER Label      Short text added to the file names and the summary (for example "collapsed-idle").
#>
[CmdletBinding()]
param(
    [double] $Minutes = 5,
    [int] $IntervalSeconds = 5,
    [int] $ProcessId = 0,
    [string] $OutDirectory = '',
    [string] $Label = 'idle'
)

$ErrorActionPreference = 'Stop'
if ($OutDirectory -eq '') {
    $OutDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'docs\evidence\sprint-7'
}
New-Item -ItemType Directory -Force -Path $OutDirectory | Out-Null

if ($ProcessId -eq 0) {
    $found = @(Get-Process -Name 'Lunar.Desktop' -ErrorAction SilentlyContinue)
    if ($found.Count -ne 1) { throw "Expected exactly one Lunar.Desktop process, found $($found.Count). Pass -ProcessId." }
    $proc = $found[0]
}
else {
    $proc = Get-Process -Id $ProcessId
}

$cores = [Environment]::ProcessorCount
$samples = [System.Collections.Generic.List[object]]::new()
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$csv = Join-Path $OutDirectory "FE-25-$Label-$stamp.csv"

$proc.Refresh()
$lastCpu = $proc.TotalProcessorTime
$lastTime = [DateTime]::UtcNow
$endAt = $lastTime.AddMinutes($Minutes)
$startedAt = $lastTime

while ([DateTime]::UtcNow -lt $endAt) {
    Start-Sleep -Seconds $IntervalSeconds
    $proc.Refresh()
    if ($proc.HasExited) { throw 'Lunar exited during the measurement.' }
    $now = [DateTime]::UtcNow
    $cpu = $proc.TotalProcessorTime
    $elapsed = ($now - $lastTime).TotalSeconds
    $used = ($cpu - $lastCpu).TotalSeconds
    $samples.Add([pscustomobject]@{
            Time               = $now.ToString('o')
            ElapsedSeconds     = [math]::Round($elapsed, 2)
            CpuSecondsUsed     = [math]::Round($used, 4)
            CpuPercentMachine  = [math]::Round(100 * $used / ($elapsed * $cores), 4)
            CpuPercentOneCore  = [math]::Round(100 * $used / $elapsed, 4)
            WorkingSetMB       = [math]::Round($proc.WorkingSet64 / 1MB, 1)
            PrivateBytesMB     = [math]::Round($proc.PrivateMemorySize64 / 1MB, 1)
            Handles            = $proc.HandleCount
            Threads            = $proc.Threads.Count
        })
    $lastCpu = $cpu
    $lastTime = $now
}

$samples | Export-Csv -Path $csv -NoTypeInformation -Encoding utf8

$total = ($samples | Measure-Object CpuSecondsUsed -Sum).Sum
$span = ($samples | Measure-Object ElapsedSeconds -Sum).Sum
$ws = $samples | Measure-Object WorkingSetMB -Average -Maximum -Minimum
$pv = $samples | Measure-Object PrivateBytesMB -Average -Maximum -Minimum
$firstSample = $samples[0]
$lastSample = $samples[$samples.Count - 1]

$summary = [pscustomobject]@{
    Label                  = $Label
    ProcessId              = $proc.Id
    ProcessStartedLocal    = $proc.StartTime.ToString('s')
    Samples                = $samples.Count
    MeasuredSeconds        = [math]::Round($span, 1)
    LogicalProcessors      = $cores
    CpuPercentMachineAvg   = [math]::Round(100 * $total / ($span * $cores), 4)
    CpuPercentOneCoreAvg   = [math]::Round(100 * $total / $span, 4)
    CpuPercentOneCoreMax   = ($samples | Measure-Object CpuPercentOneCore -Maximum).Maximum
    WorkingSetMB_First     = $firstSample.WorkingSetMB
    WorkingSetMB_Last      = $lastSample.WorkingSetMB
    WorkingSetMB_Avg       = [math]::Round($ws.Average, 1)
    WorkingSetMB_Max       = $ws.Maximum
    PrivateBytesMB_First   = $firstSample.PrivateBytesMB
    PrivateBytesMB_Last    = $lastSample.PrivateBytesMB
    PrivateBytesMB_Max     = $pv.Maximum
    Handles_First          = $firstSample.Handles
    Handles_Last           = $lastSample.Handles
    Threads_First          = $firstSample.Threads
    Threads_Last           = $lastSample.Threads
    Csv                    = (Resolve-Path -LiteralPath $csv).Path
}
$summary | Format-List
$summary | ConvertTo-Json | Set-Content -Path ([IO.Path]::ChangeExtension($csv, '.json')) -Encoding utf8
