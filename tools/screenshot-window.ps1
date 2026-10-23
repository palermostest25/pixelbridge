<#
.SYNOPSIS
  Captures a PNG of a top-level window by process name (diagnostics/test evidence only, operator-initiated).
  Uses PrintWindow(PW_RENDERFULLCONTENT) so Mica/DirectComposition windows render correctly; falls back to screen copy.
#>
param([Parameter(Mandatory)][string]$Process, [Parameter(Mandatory)][string]$Out, [string]$TitleLike = '*', [switch]$Screen)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class PbWin {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
}
"@
[PbWin]::SetProcessDPIAware() | Out-Null
$p = Get-Process $Process -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -like $TitleLike } | Select-Object -First 1
if (-not $p) { throw "No window for $Process" }
$h = $p.MainWindowHandle
[PbWin]::ShowWindow($h, 9) | Out-Null
[PbWin]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 700
$r = New-Object PbWin+RECT
[PbWin]::GetWindowRect($h, [ref]$r) | Out-Null
$w = $r.R - $r.L; $hgt = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap $w, $hgt
$g = [System.Drawing.Graphics]::FromImage($bmp)
if ($Screen) { $g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size) }
else {
    $hdc = $g.GetHdc()
    [PbWin]::PrintWindow($h, $hdc, 2) | Out-Null
    $g.ReleaseHdc($hdc)
}
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host "saved $Out ($w x $hgt)"
