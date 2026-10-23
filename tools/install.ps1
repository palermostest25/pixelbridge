<#
.SYNOPSIS
  Per-user PixelBridge install (no admin): copies the app to %LOCALAPPDATA%\Programs\PixelBridge, adds a Start Menu
  shortcut and an entry in Settings > Apps (HKCU), and optionally offers the one elevated step (PixelBridge Camera).
  Reverse with uninstall.ps1 or Settings > Apps > PixelBridge > Uninstall.
#>
$ErrorActionPreference = 'Stop'
$src = Split-Path -Parent $MyInvocation.MyCommand.Path
$dest = Join-Path $env:LOCALAPPDATA 'Programs\PixelBridge'
Get-Process PixelBridge -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item "$src\*" $dest -Recurse -Force -Exclude 'Install.cmd'

$exe = Join-Path $dest 'PixelBridge.exe'
$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'PixelBridge.lnk'
$sh = New-Object -ComObject WScript.Shell
$s = $sh.CreateShortcut($lnk); $s.TargetPath = $exe; $s.WorkingDirectory = $dest; $s.Description = 'Use your Pixel as a webcam, mic, sensors and network over USB'; $s.Save()

$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\PixelBridge'
New-Item -Force $key | Out-Null
$ver = (Get-Item $exe).VersionInfo.ProductVersion
Set-ItemProperty $key -Name DisplayName -Value 'PixelBridge'
Set-ItemProperty $key -Name DisplayVersion -Value $ver
Set-ItemProperty $key -Name Publisher -Value 'PixelBridge'
Set-ItemProperty $key -Name DisplayIcon -Value $exe
Set-ItemProperty $key -Name InstallLocation -Value $dest
Set-ItemProperty $key -Name UninstallString -Value "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$dest\uninstall.ps1`""
Set-ItemProperty $key -Name NoModify -Value 1 -Type DWord
Write-Host "Installed PixelBridge $ver to $dest"

$build = [Environment]::OSVersion.Version.Build
$registered = Test-Path 'HKLM:\SOFTWARE\Classes\CLSID\{8E3B4F1A-6C2D-4E8B-9A7F-3D5C1B2E4F60}\InprocServer32'
if ($build -ge 22000 -and -not $registered) {
    $a = Read-Host 'Also install "PixelBridge Camera" so Zoom/Teams/browsers can use the phone camera? This one step needs administrator approval. [Y/n]'
    if ($a -notmatch '^[nN]') { & "$dest\install-vcam.ps1" -Source "$dest\PixelBridge.VirtualCam.dll" }
}
Start-Process $exe
