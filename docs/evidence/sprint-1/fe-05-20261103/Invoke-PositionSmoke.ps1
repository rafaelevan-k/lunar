# FE-05 startup placement smoke. Starts Lunar.Desktop.exe (diagnostics on), reads its real window rectangle and DPI
# plus the primary work area with read-only Win32 calls, then closes it with WM_CLOSE. It changes no system setting.
# Usage: Invoke-PositionSmoke.ps1 -Exe <path> -Out <new json file>
param([Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$Out)
$ErrorActionPreference = 'Stop'
if (Test-Path $Out) { throw "Output exists: $Out" }
Add-Type -TypeDefinition @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class P5 {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint a, uint b, out R r, uint c);
  [DllImport("user32.dll")] public static extern IntPtr SetProcessDpiAwarenessContext(IntPtr c);
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rt, B; }
  public static List<IntPtr> Windows(uint pid) {
    var l = new List<IntPtr>();
    EnumWindows((h, _) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid) l.Add(h); return true; }, IntPtr.Zero);
    return l;
  }
}
'@
[void][P5]::SetProcessDpiAwarenessContext([IntPtr](-4))
$env:LUNAR_DIAGNOSTICS = '1'
$proc = Start-Process -FilePath $Exe -PassThru
$result = [ordered]@{ Timestamp = (Get-Date).ToUniversalTime().ToString('o') }
try {
  $deadline = (Get-Date).AddSeconds(15); $hwnd = [IntPtr]::Zero
  while ((Get-Date) -lt $deadline -and $hwnd -eq [IntPtr]::Zero) {
    Start-Sleep -Milliseconds 250
    foreach ($h in [P5]::Windows([uint32]$proc.Id)) { if ([P5]::IsWindowVisible($h)) { $hwnd = $h; break } }
  }
  if ($hwnd -eq [IntPtr]::Zero) { throw 'No visible window within 15 seconds.' }
  Start-Sleep -Seconds 1
  $r = New-Object P5+R; [void][P5]::GetWindowRect($hwnd, [ref]$r)
  $wa = New-Object P5+R; [void][P5]::SystemParametersInfo(0x0030, 0, [ref]$wa, 0)
  $result.WindowRect = [ordered]@{ Left = $r.L; Top = $r.T; Width = $r.Rt - $r.L; Height = $r.B - $r.T }
  $result.WindowDpi = [P5]::GetDpiForWindow($hwnd)
  $result.PrimaryWorkArea = [ordered]@{ Left = $wa.L; Top = $wa.T; Width = $wa.Rt - $wa.L; Height = $wa.B - $wa.T }
  $result.LeftMarginPx = $r.L - $wa.L; $result.RightMarginPx = $wa.Rt - $r.Rt; $result.TopMarginPx = $r.T - $wa.T
  [void][P5]::PostMessage($hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
  $result.ExitedAfterClose = $proc.WaitForExit(10000)
  if ($result.ExitedAfterClose) { $result.ExitCode = $proc.ExitCode }
} finally {
  if (-not $proc.HasExited) { $proc.Kill(); $result.Killed = $true }
  $result.Note = 'Read-only inspection; no system setting changed.'
  $result | ConvertTo-Json -Depth 5 | Out-File -FilePath $Out -Encoding utf8
}
Get-Content $Out
