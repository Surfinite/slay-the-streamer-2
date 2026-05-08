# Plan A build script.
# Refreshes the local sts2.dll from the game install, then builds the mod
# csproj and runs the test suite. Plan B will extend this to drive Godot's
# headless export and copy outputs into <game-install>/mods/<id>/.

$ErrorActionPreference = "Stop"

$gameDll = "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64\sts2.dll"
$srcDll = "src\sts2.dll"

if (-not (Test-Path $gameDll)) {
    throw "sts2.dll not found at $gameDll. Is the game installed?"
}

Copy-Item -Force $gameDll $srcDll
Write-Host "Refreshed $srcDll from game install"

dotnet build src\slay_the_streamer_2.csproj
if ($LASTEXITCODE -ne 0) { throw "main build failed" }

dotnet test tests\slay_the_streamer_2.tests.csproj
if ($LASTEXITCODE -ne 0) { throw "tests failed" }

Write-Host "Plan A build + test cycle: OK"
