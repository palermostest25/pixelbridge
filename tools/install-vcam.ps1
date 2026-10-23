<#
.SYNOPSIS
  Installs the PixelBridge Camera media source (the only machine-wide, elevated component).

.DESCRIPTION
  1. Copies PixelBridge.VirtualCam.dll to "C:\Program Files\PixelBridge\VirtualCam\" (readable by the
     Windows Camera Frame Server service, which runs as LOCAL SERVICE and cannot read user profiles).
  2. Registers its COM class {8E3B4F1A-6C2D-4E8B-9A7F-3D5C1B2E4F60} under HKLM\SOFTWARE\Classes\CLSID.
  No driver, no test signing, no service, no firewall rule. The camera device itself is created per
  session by the PixelBridge app (MFCreateVirtualCamera, session lifetime) and disappears when it exits.
  Reverse with tools\uninstall-vcam.ps1.
#>
[CmdletBinding()]
param([string]$Source)

$ErrorActionPreference = 'Stop'
if (-not $Source) { $Source = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\windows\native-bin\Release\PixelBridge.VirtualCam.dll' }
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Requesting elevation (UAC) to register the PixelBridge Camera media source...'
    $p = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Source', "`"$((Resolve-Path $Source).Path)`"")
    exit $p.ExitCode
}

$log = Join-Path $env:ProgramData 'PixelBridge\install-vcam.log'
New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null
Start-Transcript -Path $log -Append | Out-Null
try {
    if ([Environment]::OSVersion.Version.Build -lt 22000) { throw 'MFCreateVirtualCamera requires Windows 11 (build 22000+). Use the OBS fallback.' }
    $dest = Join-Path $env:ProgramFiles 'PixelBridge\VirtualCam'
    New-Item -ItemType Directory -Force $dest | Out-Null
    $target = Join-Path $dest 'PixelBridge.VirtualCam.dll'
    if (Test-Path $target) { & regsvr32.exe /s /u $target; Start-Sleep -Milliseconds 300 }
    Copy-Item -Force $Source $target
    $proc = Start-Process regsvr32.exe -ArgumentList '/s', "`"$target`"" -Wait -PassThru
    if ($proc.ExitCode -ne 0) { throw "regsvr32 failed with exit code $($proc.ExitCode)" }
    $key = 'HKLM:\SOFTWARE\Classes\CLSID\{8E3B4F1A-6C2D-4E8B-9A7F-3D5C1B2E4F60}\InprocServer32'
    Write-Host "Registered: $((Get-ItemProperty $key).'(default)')"
    exit 0
} catch {
    Write-Error $_
    exit 1
} finally {
    Stop-Transcript | Out-Null
}
