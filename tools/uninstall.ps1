<#
.SYNOPSIS
  Removes PixelBridge: per-user files, Start Menu shortcut, startup entry, Settings > Apps entry, and (if present, with
  UAC approval) the PixelBridge Camera registration. -KeepData keeps settings/pairing in %APPDATA%\PixelBridge.
  Never touches the phone; to remove the phone app run: adb -s <serial> uninstall dev.pixelbridge.app
#>
param([switch]$KeepData)
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Get-Process PixelBridge -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name PixelBridge -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'PixelBridge.lnk') -ErrorAction SilentlyContinue
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\PixelBridge' -Recurse -ErrorAction SilentlyContinue
if (Test-Path 'HKLM:\SOFTWARE\Classes\CLSID\{8E3B4F1A-6C2D-4E8B-9A7F-3D5C1B2E4F60}') {
    $vc = @("$here\uninstall-vcam.ps1", "$here\..\tools\uninstall-vcam.ps1") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($vc) { & $vc }
}
if (-not $KeepData) { Remove-Item -Recurse -Force (Join-Path $env:APPDATA 'PixelBridge') -ErrorAction SilentlyContinue }
$dest = Join-Path $env:LOCALAPPDATA 'Programs\PixelBridge'
if (Test-Path $dest) {
    # Delete after this script exits (it may be running from that folder).
    Start-Process cmd.exe -ArgumentList '/c', "timeout /t 2 >nul & rmdir /s /q `"$dest`"" -WindowStyle Hidden
}
Write-Host 'PixelBridge removed from this PC.'
