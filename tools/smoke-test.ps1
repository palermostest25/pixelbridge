<#
.SYNOPSIS  Unit/protocol tests that need no phone (simulated): Windows xUnit + Android JUnit. JUnit XML to artifacts\tests\junit.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$out = "$root\artifacts\tests\junit"
New-Item -ItemType Directory -Force $out | Out-Null
& dotnet test "$root\windows\PixelBridge.Tests" --logger "junit;LogFilePath=$out\windows-tests.xml"
if ($LASTEXITCODE -ne 0) { throw 'Windows tests failed' }
if (-not $env:JAVA_HOME -and (Test-Path "$env:LOCALAPPDATA\PixelBridgeTools\jdk21")) { $env:JAVA_HOME = "$env:LOCALAPPDATA\PixelBridgeTools\jdk21" }
Push-Location "$root\android"
try { & .\gradlew.bat testDebugUnitTest --no-daemon -q; if ($LASTEXITCODE -ne 0) { throw 'Android tests failed' } } finally { Pop-Location }
Copy-Item "$root\android\app\build\test-results\testDebugUnitTest\*.xml" $out -Force
Write-Host 'Smoke tests passed (simulated; no hardware involved).' -ForegroundColor Green
