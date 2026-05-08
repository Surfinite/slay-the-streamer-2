param(
    [string]$GameInstall = "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"
)

$ErrorActionPreference = "Stop"

$dst = Join-Path $GameInstall "mods\slay_the_streamer_2"

if (Test-Path $dst) {
    Remove-Item -Recurse -Force $dst
    Write-Host "Removed: $dst"
} else {
    Write-Host "Not installed at: $dst"
}
