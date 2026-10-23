<#
.SYNOPSIS  Read-only preflight: host OS, toolchain, adb, target device, battery, phone app, virtual camera. Changes nothing.
#>
param([string]$Serial)
if (-not $Serial) {
    $cfg = Join-Path $env:APPDATA 'PixelBridge\settings.json'
    if (Test-Path $cfg) { $Serial = (Get-Content $cfg -Raw | ConvertFrom-Json).TargetSerial }
    if (-not $Serial) { throw 'No phone serial: pass -Serial or connect once with the PixelBridge app.' }
}
function Line($ok, $what, $detail) { $tag = if ($null -eq $ok) { 'INFO' } elseif ($ok) { ' OK ' } else { 'FAIL' }; Write-Host ("[{0}] {1}: {2}" -f $tag, $what, $detail) }
$os = Get-CimInstance Win32_OperatingSystem
Line $null 'Windows' "$($os.Caption) $($os.Version)"
Line ([int]$os.BuildNumber -ge 22000) 'Virtual camera support' "build $($os.BuildNumber) (needs 22000+)"
Line ([bool](Get-Command dotnet -ErrorAction SilentlyContinue)) '.NET SDK' ((dotnet --version) 2>$null)
$adb = @("$env:LOCALAPPDATA\PixelBridgeTools\android-sdk\platform-tools\adb.exe",
         (Get-ChildItem "$env:USERPROFILE\Downloads\scrcpy-win64-*" -Recurse -Filter adb.exe -ErrorAction SilentlyContinue | Select-Object -First 1).FullName,
         (Get-Command adb -ErrorAction SilentlyContinue).Source) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
Line ([bool]$adb) 'adb' $adb
if (-not $adb) { return }
$devs = & $adb devices -l | Select-Object -Skip 1 | Where-Object { $_.Trim() }
foreach ($d in $devs) { Line ($d -match "^$Serial\s+device") 'adb device' $d.Trim() }
$state = (& $adb -s $Serial get-state 2>$null)
Line ($state -eq 'device') "target $Serial" ($(if ($state) { $state } else { 'not attached' }))
if ($state -ne 'device') { return }
foreach ($p in 'ro.product.device', 'ro.build.version.release', 'ro.build.version.sdk', 'ro.build.id') { Line $null $p (& $adb -s $Serial shell getprop $p) }
$bat = & $adb -s $Serial shell dumpsys battery
$temp = [int](($bat | Select-String 'temperature: (\d+)').Matches[0].Groups[1].Value) / 10
$level = ($bat | Select-String 'level: (\d+)').Matches[0].Groups[1].Value
Line ($temp -lt 40) 'battery' "$level% $temp C (software reading only; inspect the phone physically)"
$ver = (& $adb -s $Serial shell dumpsys package dev.pixelbridge.app | Select-String 'versionName=(\S+)').Matches | Select-Object -First 1
Line ([bool]$ver) 'phone app' ($(if ($ver) { $ver.Groups[1].Value } else { 'not installed' }))
Line $null 'USB functions' (& $adb -s $Serial shell svc usb getFunctions)
Line (Test-Path 'HKLM:\SOFTWARE\Classes\CLSID\{8E3B4F1A-6C2D-4E8B-9A7F-3D5C1B2E4F60}\InprocServer32') 'PixelBridge Camera source' 'HKLM registration'
$ncm = Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object InterfaceDescription -match 'UsbNcm|NDIS'
Line $null 'USB tether adapter' ($(if ($ncm) { "$($ncm.Name) [$($ncm.Status)]" } else { 'none' }))
