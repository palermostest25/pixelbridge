<#
.SYNOPSIS
  Builds everything: Android APK, native DLLs, Windows app (self-contained), tests, and the per-user package.
  No device or system changes. Outputs to artifacts\.
.PARAMETER SkipAndroid / SkipNative / SkipTests  Skip a stage.
#>
param([switch]$SkipAndroid, [switch]$SkipNative, [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$art = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Force "$art\android", "$art\windows", "$art\tests\junit" | Out-Null
$tools = Join-Path $env:LOCALAPPDATA 'PixelBridgeTools'
function Step($name) { Write-Host "`n=== $name ===" -ForegroundColor Cyan }

# --- prerequisites
Step 'Checking prerequisites'
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { throw '.NET 10 SDK not found (https://dot.net)' }
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = if (Test-Path $vswhere) { & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\amd64\MSBuild.exe' | Select-Object -First 1 }
if (-not $env:JAVA_HOME -and (Test-Path "$tools\jdk21")) { $env:JAVA_HOME = "$tools\jdk21" }
Write-Host "dotnet:  $dotnet"; Write-Host "msbuild: $msbuild"; Write-Host "JAVA_HOME: $env:JAVA_HOME"

if (-not $SkipAndroid) {
    Step 'Android APK'
    if (-not $env:JAVA_HOME) { throw 'JDK 17+ required (set JAVA_HOME)' }
    if (-not (Test-Path "$root\android\local.properties") -and (Test-Path "$tools\android-sdk")) {
        "sdk.dir=$($tools -replace '\\','/')/android-sdk" | Set-Content "$root\android\local.properties"
    }
    Push-Location "$root\android"
    try {
        & .\gradlew.bat assembleDebug $(if (-not $SkipTests) { 'testDebugUnitTest' }) --no-daemon -q
        if ($LASTEXITCODE -ne 0) { throw "gradle failed ($LASTEXITCODE)" }
    } finally { Pop-Location }
    Copy-Item "$root\android\app\build\outputs\apk\debug\app-debug.apk" "$art\android\PixelBridge-debug.apk" -Force
    if (-not $SkipTests) { Copy-Item "$root\android\app\build\test-results\testDebugUnitTest\*.xml" "$art\tests\junit\" -Force }
}

if (-not $SkipNative) {
    Step 'Native DLLs (decoder, virtual camera)'
    if (-not $msbuild) { throw 'Visual Studio with the C++ workload is required for the native DLLs' }
    foreach ($p in 'PixelBridge.Native', 'PixelBridge.VirtualCam', 'PixelBridge.VCamTest') {
        & $msbuild "$root\windows\$p\$p.vcxproj" /p:Configuration=Release /p:Platform=x64 /nologo /v:m
        if ($LASTEXITCODE -ne 0) { throw "$p build failed" }
    }
}

if (-not $SkipTests) {
    Step 'Windows unit tests'
    & dotnet test "$root\windows\PixelBridge.Tests" --logger "junit;LogFilePath=$art\tests\junit\windows-tests.xml"
    if ($LASTEXITCODE -ne 0) { throw 'Windows tests failed' }
}

Step 'Windows app (self-contained publish)'
$pub = "$art\windows\PixelBridge"
Remove-Item -Recurse -Force $pub -ErrorAction SilentlyContinue
& dotnet publish "$root\windows\PixelBridge.App" -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o $pub
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
& dotnet publish "$root\windows\PixelBridge.Cli" -c Release -r win-x64 --self-contained true -o "$pub\tools"
Copy-Item "$root\windows\native-bin\Release\PixelBridge.Native.dll", "$root\windows\native-bin\Release\PixelBridge.VirtualCam.dll" $pub -Force
Copy-Item "$root\windows\native-bin\Release\PixelBridge.VCamTest.exe" "$pub\tools" -Force
if (Test-Path "$art\android\PixelBridge-debug.apk") { Copy-Item "$art\android\PixelBridge-debug.apk" "$pub\PixelBridge.apk" -Force }
Copy-Item "$root\tools\install-vcam.ps1", "$root\tools\uninstall-vcam.ps1", "$root\tools\install.ps1", "$root\tools\uninstall.ps1" $pub -Force
Copy-Item "$root\docs\QUICKSTART.md" $pub -Force -ErrorAction SilentlyContinue
"@echo off`r`npowershell -NoProfile -ExecutionPolicy Bypass -File ""%~dp0install.ps1""`r`npause" | Set-Content "$pub\Install.cmd" -Encoding ascii

Step 'Package'
$zip = "$art\windows\PixelBridge-Setup.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path "$pub\*" -DestinationPath $zip
Get-FileHash $zip -Algorithm SHA256 | ForEach-Object { "$($_.Hash)  PixelBridge-Setup.zip" } | Set-Content "$zip.sha256"
Write-Host "`nDone. Package: $zip" -ForegroundColor Green
