# FE-04 runtime smoke. Starts Lunar.Desktop.exe, reads the ItemStatus of its own window through UI Automation
# (read-only; the search is limited to this process id, so no other application is inspected) and closes it with
# WM_CLOSE. It changes no system setting. Usage: Invoke-StatusSmoke.ps1 -Exe <path> -Out <new json file>
param([Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$Out)
$ErrorActionPreference = 'Stop'
if (Test-Path $Out) { throw "Output exists: $Out" }
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class S4 {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  public static List<IntPtr> Windows(uint pid) {
    var l = new List<IntPtr>();
    EnumWindows((h, _) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid) l.Add(h); return true; }, IntPtr.Zero);
    return l;
  }
}
'@
$proc = Start-Process -FilePath $Exe -PassThru
$result = [ordered]@{ Timestamp = (Get-Date).ToUniversalTime().ToString('o') }
try {
  $deadline = (Get-Date).AddSeconds(15); $hwnd = [IntPtr]::Zero
  while ((Get-Date) -lt $deadline -and $hwnd -eq [IntPtr]::Zero) {
    Start-Sleep -Milliseconds 250
    foreach ($h in [S4]::Windows([uint32]$proc.Id)) { if ([S4]::IsWindowVisible($h)) { $hwnd = $h; break } }
  }
  if ($hwnd -eq [IntPtr]::Zero) { throw 'No visible window within 15 seconds.' }
  Start-Sleep -Seconds 1
  $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
  $items = $root.FindAll([System.Windows.Automation.TreeScope]::Subtree, [System.Windows.Automation.Condition]::TrueCondition)
  $found = @()
  foreach ($e in $items) {
    $status = $e.Current.ItemStatus
    if ($status) { $found += [ordered]@{ ControlType = $e.Current.ControlType.ProgrammaticName; ItemStatus = $status } }
  }
  $result.WindowName = $root.Current.Name
  $result.ItemStatusElements = $found
  [void][S4]::PostMessage($hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
  $result.ExitedAfterClose = $proc.WaitForExit(10000)
  if ($result.ExitedAfterClose) { $result.ExitCode = $proc.ExitCode }
} finally {
  if (-not $proc.HasExited) { $proc.Kill(); $result.Killed = $true }
  $result.Note = 'Read-only inspection of this process only; no system setting changed.'
  $result | ConvertTo-Json -Depth 5 | Out-File -FilePath $Out -Encoding utf8
}
Get-Content $Out
