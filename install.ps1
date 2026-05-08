param(
    [string]$GameInstall = "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"
)

$ErrorActionPreference = "Stop"

$src = "dist\slay_the_streamer_2"
$dst = Join-Path $GameInstall "mods\slay_the_streamer_2"

if (-not (Test-Path $src)) {
    throw "Source not found: $src. Run build.ps1 first."
}

Write-Host "Installing from: $src"
Write-Host "Installing to:   $dst"

New-Item -ItemType Directory -Force -Path $dst | Out-Null
Copy-Item -Force -Recurse "$src\*" $dst

Write-Host ""
Write-Host "Done. Launch the game and look for the canary log line:"
Write-Host "  [slay_the_streamer_2] mod loading..."
Write-Host "in the Godot log at:"
Write-Host "  %APPDATA%\Godot\app_userdata\Slay the Spire 2\logs\"
