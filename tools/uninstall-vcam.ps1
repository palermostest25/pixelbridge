<#
.SYNOPSIS
  Removes the PixelBridge Camera media source registration and files (reverses install-vcam.ps1).
#>
$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $p = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    exit $p.ExitCode
}
$dest = Join-Path $env:ProgramFiles 'PixelBridge\VirtualCam'
$target = Join-Path $dest 'PixelBridge.VirtualCam.dll'
if (Test-Path $target) { Start-Process regsvr32.exe -ArgumentList '/s', '/u', "`"$target`"" -Wait | Out-Null }
Remove-Item -ErrorAction SilentlyContinue -Recurse 'HKLM:\SOFTWARE\Classes\CLSID\{8E3B4F1A-6C2D-4E8B-9A7F-3D5C1B2E4F60}'
# The Frame Server may still hold the DLL; schedule removal on reboot if it is locked.
try { Remove-Item -Recurse -Force $dest -ErrorAction Stop } catch { Write-Warning "Files in use; delete $dest after the next reboot." }
$parent = Split-Path $dest
if ((Test-Path $parent) -and -not (Get-ChildItem $parent)) { Remove-Item $parent }
Write-Host 'PixelBridge Camera media source unregistered.'
