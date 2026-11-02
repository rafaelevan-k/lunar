# Generates the temporary placeholder tray icon (a lavender crescent) as a PNG-compressed multi-size .ico.
# Placeholder art only: final branding is a design/packaging decision (BE-29). No external assets or downloads.
param([Parameter(Mandatory)][string]$Out)
$ErrorActionPreference = 'Stop'
if (Test-Path $Out) { throw "Output exists: $Out" }
Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48
$pngs = foreach ($s in $sizes) {
  $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
  $m = [single]($s * 0.08); $d = [single]($s - 2 * $m)
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $path.AddEllipse($m, $m, $d, $d)
  $cut = New-Object System.Drawing.Drawing2D.GraphicsPath
  $cut.AddEllipse([single]($m + $d * 0.28), [single]($m - $d * 0.06), $d * 0.86, $d * 0.86)
  $region = New-Object System.Drawing.Region $path
  $region.Exclude($cut)
  $g.FillRegion((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x8B, 0x85, 0xF0))), $region)
  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  , $ms.ToArray()
}
$fs = [System.IO.File]::Create($Out); $w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]
  $w.Write([byte]$s); $w.Write([byte]$s); $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
  $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Dispose(); $fs.Dispose()
"{0} bytes" -f (Get-Item $Out).Length
