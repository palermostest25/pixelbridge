<#
.SYNOPSIS  Sanitised diagnostics ZIP: doctor output + recent logs + settings without the API token. No media, SMS or coordinates.
#>
param([string]$Serial)
if (-not $Serial) {
    $cfg = Join-Path $env:APPDATA 'PixelBridge\settings.json'
    if (Test-Path $cfg) { $Serial = (Get-Content $cfg -Raw | ConvertFrom-Json).TargetSerial }
    if (-not $Serial) { throw 'No phone serial: pass -Serial or connect once with the PixelBridge app.' }
}
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$tmp = Join-Path $env:TEMP "pixelbridge-diag-$(Get-Date -f yyyyMMddHHmmss)"
New-Item -ItemType Directory -Force $tmp | Out-Null
& "$root\tools\doctor.ps1" -Serial $Serial *> "$tmp\doctor.txt"
$logs = Join-Path $env:APPDATA 'PixelBridge\logs'
if (Test-Path $logs) { Get-ChildItem $logs -Filter *.log | Sort-Object LastWriteTime -Descending | Select-Object -First 3 | Copy-Item -Destination $tmp }
$settings = Join-Path $env:APPDATA 'PixelBridge\settings.json'
if (Test-Path $settings) { (Get-Content $settings -Raw) -replace '"ApiToken"\s*:\s*"[^"]*"', '"ApiToken": "<redacted>"' | Set-Content "$tmp\settings.json" }
$zip = "$root\artifacts\pixelbridge-diagnostics-$(Get-Date -f yyyyMMdd-HHmmss).zip"
Compress-Archive -Path "$tmp\*" -DestinationPath $zip
Remove-Item -Recurse -Force $tmp
Write-Host "Saved $zip"
