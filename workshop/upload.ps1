# Steam Workshop upload for slay_the_streamer_2.
# Syncs dist/ into workshop/content/ and runs MegaCrit's ModUploader.
# Prereqs: pwsh -File build.ps1 has been run (dist/ is current); Steam is
# running and logged in as the publishing account.
# First upload creates workshop/mod_id.txt — COMMIT IT; later uploads update
# the same workshop item via that id.

param(
    [string]$Uploader = "C:\Tools\sts2-mod-uploader\ModUploader.exe"
)

$ErrorActionPreference = "Stop"
$workspace = $PSScriptRoot
$repoRoot  = Split-Path $workspace -Parent
$dist      = Join-Path $repoRoot "dist\slay_the_streamer_2"

if (-not (Test-Path $Uploader)) { throw "ModUploader not found: $Uploader" }
if (-not (Test-Path $dist))     { throw "dist not built: $dist (run build.ps1 first)" }
if (-not (Test-Path (Join-Path $workspace "image.png"))) { throw "image.png missing from workspace" }

# The workshop item content is exactly a local-mod folder: manifest + dll.
$content = Join-Path $workspace "content"
if (Test-Path $content) { Remove-Item -Recurse -Force $content }
New-Item -ItemType Directory $content | Out-Null
Copy-Item "$dist\*" $content -Recurse
Write-Host "Synced $dist -> $content"

$manifest = Get-Content (Join-Path $content "slay_the_streamer_2.json") -Raw | ConvertFrom-Json
Write-Host "Uploading mod version $($manifest.version)"

& $Uploader upload -w $workspace
if ($LASTEXITCODE -ne 0) { throw "ModUploader failed (exit $LASTEXITCODE) - see mod-uploader.log next to ModUploader.exe" }
Write-Host "Upload OK"
