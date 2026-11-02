# FE-02 runtime smoke for the production shell. Starts Lunar.Desktop.exe, inspects its real top-level window with
# Win32 calls (read-only), performs one real left click on the pill, and closes it with WM_CLOSE.
# It changes no system settings. Usage: Invoke-ShellSmoke.ps1 -Exe <path> -Out <new json file>
param([Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$Out)
$ErrorActionPreference = 'Stop'
if (Test-Path $Out) { throw "Output exists: $Out" }
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Text;
public static class W {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out P p);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rt, B; }
  [StructLayout(LayoutKind.Sequential)] public struct P { public int X, Y; }
    [DllImport("user32.dll")] public static extern IntPtr SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(P p);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint f);
  [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint c);
  public static string Cls(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }
  public static List<IntPtr> Windows(uint pid) {
    var l = new List<IntPtr>();
    EnumWindows((h, _) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid) l.Add(h); return true; }, IntPtr.Zero);
    return l;
  }
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  public static uint Pid(IntPtr h) { uint p; GetWindowThreadProcessId(h, out p); return p; }
}
'@
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class G {
  [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
  [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
  [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, uint rop);
}
"@
# Sample one screen pixel including layered windows (SRCCOPY | CAPTUREBLT).
function Pixel($x, $y) {
  $bmp = New-Object System.Drawing.Bitmap 1, 1
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $dst = $g.GetHdc(); $src = [G]::GetDC([IntPtr]::Zero)
  [void][G]::BitBlt($dst, 0, 0, 1, 1, $src, $x, $y, 0x40CC0020)
  [void][G]::ReleaseDC([IntPtr]::Zero, $src); $g.ReleaseHdc($dst); $g.Dispose()
  $c = $bmp.GetPixel(0, 0); $bmp.Dispose()
  '#{0:X2}{1:X2}{2:X2}' -f $c.R, $c.G, $c.B
}[void][W]::SetProcessDpiAwarenessContext([IntPtr](-4))
$fgBefore = [W]::GetForegroundWindow()
$fgBeforePid = [W]::Pid($fgBefore)
$proc = Start-Process -FilePath $Exe -PassThru
$result = [ordered]@{ Timestamp = (Get-Date).ToUniversalTime().ToString('o'); ExeStarted = $true }
try {
  $deadline = (Get-Date).AddSeconds(15); $hwnd = [IntPtr]::Zero
  while ((Get-Date) -lt $deadline -and $hwnd -eq [IntPtr]::Zero) {
    Start-Sleep -Milliseconds 250
    foreach ($h in [W]::Windows([uint32]$proc.Id)) { if ([W]::IsWindowVisible($h)) { $hwnd = $h; break } }
  }
  if ($hwnd -eq [IntPtr]::Zero) { throw 'No visible window within 15 seconds.' }
  Start-Sleep -Seconds 1
  $r = New-Object W+R; [void][W]::GetWindowRect($hwnd, [ref]$r)
  $ex = [int64][W]::GetWindowLongPtr($hwnd, -20)
  $visible = @([W]::Windows([uint32]$proc.Id) | Where-Object { [W]::IsWindowVisible($_) }).Count
  $cn = New-Object System.Text.StringBuilder 256; [void][W]::GetClassName($hwnd, $cn, 256)
  $result.Window = [ordered]@{
    VisibleTopLevelWindows = $visible; Left = $r.L; Top = $r.T; Width = $r.Rt - $r.L; Height = $r.B - $r.T
    Dpi = [W]::GetDpiForWindow($hwnd)
    ExStyleHex = ('0x{0:X}' -f $ex)
    TopMost = [bool]($ex -band 0x8); ToolWindow = [bool]($ex -band 0x80); NoActivate = [bool]($ex -band 0x08000000)
    AppWindow = [bool]($ex -band 0x40000); Layered = [bool]($ex -band 0x80000)
    IsForeground = ([W]::GetForegroundWindow() -eq $hwnd); HasOwnerWindow = ([W]::GetWindow($hwnd, 4) -ne [IntPtr]::Zero); Class = $cn.ToString()
  }
  $cx = [int](($r.L + $r.Rt) / 2); $cy = [int](($r.T + $r.B) / 2)
  $result.Pixels = [ordered]@{ Center = (Pixel $cx $cy); OuterCornerTopLeft = (Pixel ($r.L + 1) ($r.T + 1)); OuterCornerBottomRight = (Pixel ($r.Rt - 2) ($r.B - 2)) }
  $pc = New-Object W+P; $pc.X = $r.L + 1; $pc.Y = $r.T + 1
  $pm = New-Object W+P; $pm.X = $cx; $pm.Y = $cy
  $result.HitTest = [ordered]@{
    CenterBelongsToPill = ([W]::GetAncestor([W]::WindowFromPoint($pm), 2) -eq $hwnd)
    OuterCornerBelongsToPill = ([W]::GetAncestor([W]::WindowFromPoint($pc), 2) -eq $hwnd)
  }
  $fgMid = [W]::GetForegroundWindow()
  $result.Foreground = [ordered]@{ BeforePid = $fgBeforePid; BeforeClass = [W]::Cls($fgBefore); AfterStartIsSameWindow = ($fgMid -eq $fgBefore) }
  # One real left click on the pill center, then restore the cursor.
  $old = New-Object W+P; [void][W]::GetCursorPos([ref]$old)
  [void][W]::SetCursorPos($cx, $cy); Start-Sleep -Milliseconds 200
  $pt = New-Object W+P; $pt.X = $cx; $pt.Y = $cy
  $target = [W]::GetAncestor([W]::WindowFromPoint($pt), 2)
  $targetIsPill = ($target -eq $hwnd)
  if ($targetIsPill) {
    [W]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero); [W]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    $samples = @()
    1..15 | ForEach-Object { Start-Sleep -Milliseconds 100; $f = [W]::GetForegroundWindow(); $samples += ('{0}:{1}:{2}' -f [W]::Pid($f), [W]::Cls($f), ($f -eq $fgBefore)) }
    $result.ForegroundSamplesAfterClick = $samples
  }
  [void][W]::SetCursorPos($old.X, $old.Y)
  $fgAfter = [W]::GetForegroundWindow()
  $result.Click = [ordered]@{ HitTestTargetIsPill = $targetIsPill; ClickSent = $targetIsPill; ForegroundUnchanged = ($fgAfter -eq $fgBefore); PillIsForeground = ($fgAfter -eq $hwnd); ForegroundAfterClass = [W]::Cls($fgAfter); ProcessStillRunning = (-not $proc.HasExited) }
  if ($fgAfter -eq [IntPtr]::Zero) { $result.Click.RestoredForegroundAfterNull = [W]::SetForegroundWindow($fgBefore) }
  [void][W]::PostMessage($hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)  # WM_CLOSE
  $exited = $proc.WaitForExit(10000)
  $result.Close = [ordered]@{ ExitedWithin10s = $exited; ExitCode = $(if ($exited) { $proc.ExitCode } else { $null }) }
}
finally {
  if (-not $proc.HasExited) { $result.ForcedKill = $true; Stop-Process -Id $proc.Id -Force }
  $result.ProcessGone = [bool](-not (Get-Process -Id $proc.Id -ErrorAction SilentlyContinue))
  $result | ConvertTo-Json -Depth 6 | Set-Content -Path $Out -Encoding utf8
}
$result | ConvertTo-Json -Depth 6





