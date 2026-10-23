<#
.SYNOPSIS
  Bounded hardware tests on the real Pixel via pbctl. Each test has a hard time cap; camera/mic runs are short.
  Results (stdout + exit codes) go to artifacts\tests\hw-<timestamp>\. Coordinates and media stay local and gitignored.
#>
param([string]$Serial, [switch]$SkipCamera, [switch]$SkipMic)
if (-not $Serial) {
    $cfg = Join-Path $env:APPDATA 'PixelBridge\settings.json'
    if (Test-Path $cfg) { $Serial = (Get-Content $cfg -Raw | ConvertFrom-Json).TargetSerial }
    if (-not $Serial) { throw 'No phone serial: pass -Serial or connect once with the PixelBridge app.' }
}
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$pb = @("$root\windows\PixelBridge.Cli\bin\Debug\net10.0-windows\pbctl.exe", "$root\artifacts\windows\PixelBridge\tools\pbctl.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $pb) { throw 'pbctl.exe not built (dotnet build windows\PixelBridge.Cli)' }
$out = "$root\artifacts\tests\hw-$(Get-Date -f yyyyMMdd-HHmmss)"; New-Item -ItemType Directory -Force $out | Out-Null
$results = @()
function Run($id, $name, [string[]]$a) {
    Write-Host "--- $id $name" -ForegroundColor Cyan
    $log = "$out\$id.txt"
    & $pb @a *> $log
    $code = $LASTEXITCODE
    Get-Content $log | Select-String -NotMatch '^CAPABILITIES' | Select-Object -Last 8 | ForEach-Object { "    $_" }
    $script:results += [pscustomobject]@{ id = $id; test = $name; exit = $code; result = $(if ($code -eq 0) { 'PASS' } else { 'FAIL' }) }
}
Run 'T04' 'connect + auth + capabilities' @('status')
Run 'T05-T06' 'battery + IMU 10 s' @('sensors', '--seconds', '10', '--out', "$out\sensors.jsonl")
if (-not $SkipCamera) {
    Run 'T07' 'rear camera 6 s' @('video', '--camera', '0', '--seconds', '6', '--snapshot', "$out\rear.bmp")
    Run 'T08' 'front camera 6 s' @('video', '--camera', '1', '--seconds', '6', '--snapshot', "$out\front.bmp")
}
if (-not $SkipMic) { Run 'T10' 'microphone 5 s' @('audio', '--seconds', '5', '--out', "$out\mic.wav") }
Run 'LOC' 'location 15 s' @('location', '--seconds', '15')
Run 'RAD' 'radio capabilities' @('radio', 'caps')
Run 'BLE' 'BLE scan 8 s' @('radio', 'ble.scan.start', '--args', '{"durationMs":8000}', '--seconds', '9')
$results | Format-Table -AutoSize
$results | ConvertTo-Json | Set-Content "$out\summary.json"
Write-Host "Evidence in $out (media files stay local; artifacts\ is gitignored)."
