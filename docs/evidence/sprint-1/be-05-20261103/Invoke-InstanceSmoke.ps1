# BE-05 runtime smoke: single-instance behavior of the real Lunar.Desktop.exe (ADR-0008).
# Requires DOTNET_ROOT to point at the .NET 10 runtime. Aborts if a Lunar.Desktop process already runs.
# It launches the app several times, closes it with WM_CLOSE (or kills it for the crash case), and holds the
# production mutex name from this script for the "unreachable primary" case. It changes no system settings.
param([Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$Out)
$ErrorActionPreference = 'Stop'
if (Test-Path $Out) { throw "Output exists: $Out" }
if (Get-Process Lunar.Desktop -ErrorAction SilentlyContinue) { throw 'A Lunar.Desktop process is already running.' }
Add-Type -TypeDefinition @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Text;
public static class I {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  public static List<IntPtr> Visible(uint pid) {
    var l = new List<IntPtr>();
    EnumWindows((h, _) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) l.Add(h); return true; }, IntPtr.Zero);
    return l;
  }
  public static uint Pid(IntPtr h) { uint p; GetWindowThreadProcessId(h, out p); return p; }
}
'@
function Wait-Window($proc, $seconds = 15) {
  $end = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $end) { Start-Sleep -Milliseconds 200; $w = [I]::Visible([uint32]$proc.Id); if ($w.Count -gt 0) { return $w[0] } }
  return [IntPtr]::Zero
}
function Close-App($proc, $hwnd) {
  [void][I]::PostMessage($hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
  $exited = $proc.WaitForExit(10000)
  [ordered]@{ ExitedWithin10s = $exited; ExitCode = $(if ($exited) { $proc.ExitCode } else { $null }) }
}
$r = [ordered]@{ Timestamp = (Get-Date).ToUniversalTime().ToString('o') }
$p1 = $null; $p3 = $null
try {
  # 1. First launch becomes the primary and shows exactly one window.
  $p1 = Start-Process $Exe -PassThru
  $h1 = Wait-Window $p1
  $r.Primary = [ordered]@{ WindowShown = ($h1 -ne [IntPtr]::Zero); VisibleWindows = @([I]::Visible([uint32]$p1.Id)).Count }
  Start-Sleep -Seconds 1

  # 2. Two more launches: each must exit 0 quickly and leave exactly one Lunar.Desktop process with one visible window.
  $second = @()
  foreach ($n in 1, 2) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p2 = Start-Process $Exe -PassThru
    $exited = $p2.WaitForExit(10000)
    $second += [ordered]@{ Launch = $n; ExitedWithin10s = $exited; ExitCode = $(if ($exited) { $p2.ExitCode } else { $null }); Milliseconds = $sw.ElapsedMilliseconds }
    if (-not $exited) { Stop-Process -Id $p2.Id -Force }
  }
  $r.SecondLaunches = $second
  $fg = [I]::GetForegroundWindow()
  $r.AfterSecondLaunch = [ordered]@{
    LunarProcessCount = @(Get-Process Lunar.Desktop -ErrorAction SilentlyContinue).Count
    PrimaryStillRunning = (-not $p1.HasExited)
    PrimaryVisibleWindows = @([I]::Visible([uint32]$p1.Id)).Count
    PrimaryIsForeground = ([I]::Pid($fg) -eq $p1.Id)
  }

  # 3. Clean close of the primary.
  $r.CloseWm = Close-App $p1 $h1
  $r.ProcessGoneAfterClose = [bool](-not (Get-Process -Id $p1.Id -ErrorAction SilentlyContinue))

  # 4. After a clean exit the next launch is a primary again; after a kill (crash) too.
  $p3 = Start-Process $Exe -PassThru
  $h3 = Wait-Window $p3
  $afterClean = ($h3 -ne [IntPtr]::Zero)
  Stop-Process -Id $p3.Id -Force; $p3.WaitForExit(5000) | Out-Null
  $p3 = Start-Process $Exe -PassThru
  $h3 = Wait-Window $p3
  $r.Restart = [ordered]@{ PrimaryAfterCleanExit = $afterClean; PrimaryAfterKill = ($h3 -ne [IntPtr]::Zero) }
  $r.RestartClose = Close-App $p3 $h3
  $p3 = $null

  # 5. Unreachable primary: this script owns the instance name but nobody listens, so a launch must give up.
  $created = $false
  $foreign = New-Object System.Threading.Mutex($false, 'Local\Lunar.Desktop.v1.instance', [ref]$created)
  try {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p4 = Start-Process $Exe -PassThru
    $exited = $p4.WaitForExit(15000)
    $r.UnreachablePrimary = [ordered]@{ MutexCreatedByScript = $created; ExitedWithin15s = $exited; ExitCode = $(if ($exited) { $p4.ExitCode } else { $null }); Milliseconds = $sw.ElapsedMilliseconds }
    if (-not $exited) { Stop-Process -Id $p4.Id -Force }
  } finally { $foreign.Dispose() }
}
finally {
  foreach ($p in $p1, $p3) { if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force } }
  $r.LeftoverProcesses = @(Get-Process Lunar.Desktop -ErrorAction SilentlyContinue).Count
  $r | ConvertTo-Json -Depth 6 | Set-Content -Path $Out -Encoding utf8
}
$r | ConvertTo-Json -Depth 6

