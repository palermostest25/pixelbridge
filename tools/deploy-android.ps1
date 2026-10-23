<#
.SYNOPSIS  Installs the built APK on the target serial ONLY (refuses emulators and unknown devices) and opens the app.
#>
param([string]$Serial, [string]$Apk)
if (-not $Serial) {
    $cfg = Join-Path $env:APPDATA 'PixelBridge\settings.json'
    if (Test-Path $cfg) { $Serial = (Get-Content $cfg -Raw | ConvertFrom-Json).TargetSerial }
    if (-not $Serial) { throw 'No phone serial: pass -Serial or connect once with the PixelBridge app.' }
}
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $Apk) { $Apk = "$root\android\app\build\outputs\apk\debug\app-debug.apk" }
if (-not (Test-Path $Apk)) { throw "APK not found: $Apk (run BUILD_ALL.ps1)" }
if ($Serial -like 'emulator-*') { throw 'Refusing to deploy to an emulator.' }
$adb = @("$env:LOCALAPPDATA\PixelBridgeTools\android-sdk\platform-tools\adb.exe", (Get-Command adb -ErrorAction SilentlyContinue).Source) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if ((& $adb -s $Serial get-state 2>$null) -ne 'device') { throw "$Serial is not attached/authorised" }
$dev = & $adb -s $Serial shell getprop ro.product.device
if ($dev.Trim() -ne 'panther') { throw "Device '$dev' is not pre-approved (expected panther)" }
& $adb -s $Serial install -r $Apk
if ($LASTEXITCODE -ne 0) { throw 'install failed' }
& $adb -s $Serial shell am start -n dev.pixelbridge.app/.MainActivity
