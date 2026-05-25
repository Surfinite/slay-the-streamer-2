# Plan B prep build script.
# Refreshes sts2.dll + 0Harmony.dll from game install, builds Release config,
# runs Plan A regression tests, assembles dist/slay_the_streamer_2/.

param(
    [string]$GameInstall = "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $GameInstall)) {
    throw "Game install not found: $GameInstall"
}

$dataDir = Join-Path $GameInstall "data_sts2_windows_x86_64"
$gameSts2 = Join-Path $dataDir "sts2.dll"
$gameHarmony = Join-Path $dataDir "0Harmony.dll"

if (-not (Test-Path $gameSts2)) { throw "sts2.dll not found at $gameSts2." }
if (-not (Test-Path $gameHarmony)) { throw "0Harmony.dll not found at $gameHarmony." }

Copy-Item -Force $gameSts2 "src\sts2.dll"
Copy-Item -Force $gameHarmony "src\0Harmony.dll"
$harmonyVer = (Get-Item "src\0Harmony.dll").VersionInfo.FileVersion
Write-Host "Refreshed src\sts2.dll and src\0Harmony.dll (Harmony $harmonyVer)"

# Build via dotnet publish to a stable output path (avoid .godot/mono/temp drift).
dotnet publish src\slay_the_streamer_2.csproj -c Release -o dist\publish-tmp
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

dotnet test tests\slay_the_streamer_2.tests.csproj --nologo
if ($LASTEXITCODE -ne 0) { throw "Plan A regression tests failed" }

# Assemble dist/slay_the_streamer_2/
$out = "dist\slay_the_streamer_2"
New-Item -ItemType Directory -Force -Path $out | Out-Null
Copy-Item -Force "dist\publish-tmp\slay_the_streamer_2.dll" "$out\slay_the_streamer_2.dll"
Copy-Item -Force "src\slay_the_streamer_2.json" "$out\slay_the_streamer_2.json"
Copy-Item -Force "src\slay_the_streamer_2.json.example" "$out\slay_the_streamer_2.json.example"
Write-Host "Built $out\"
Write-Host "Plan B prep build cycle: OK"
