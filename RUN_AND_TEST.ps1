<#
.SYNOPSIS
  Safe end-to-end check: read-only doctor, unit tests, then bounded hardware tests on the configured Pixel if it is
  attached and authorised. Never changes USB modes, never installs anything without -Deploy, never runs unbounded.
.PARAMETER Deploy    Also install the freshly built APK on the target serial (only that serial).
.PARAMETER Hardware  Run the bounded hardware tests (camera/mic sessions are a few seconds each; supervise the phone).
#>
param([switch]$Deploy, [switch]$Hardware, [string]$Serial)
if (-not $Serial) {
    $cfg = Join-Path $env:APPDATA 'PixelBridge\settings.json'
    if (Test-Path $cfg) { $Serial = (Get-Content $cfg -Raw | ConvertFrom-Json).TargetSerial }
    if (-not $Serial) { throw 'No phone serial: pass -Serial or connect once with the PixelBridge app.' }
}
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
& "$root\tools\doctor.ps1" -Serial $Serial
& "$root\tools\smoke-test.ps1"
if ($Deploy) { & "$root\tools\deploy-android.ps1" -Serial $Serial }
if ($Hardware) {
    Write-Warning 'Hardware tests use the camera and microphone briefly. Make sure the phone is supervised and not hot.'
    & "$root\tools\test-device.ps1" -Serial $Serial
}
