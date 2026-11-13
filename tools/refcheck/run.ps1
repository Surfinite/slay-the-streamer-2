<#
.SYNOPSIS
    Checks a built mod DLL against every archived game DLL (and optionally a decompiled tree).

.DESCRIPTION
    Builds tools/refcheck if needed, runs it once per game DLL, prints a short per-DLL summary
    plus the MemberCompat / FieldRefAccess resolutions, and writes full reports to
    tools/refcheck/out/. Exits 1 if any REAL failure was found (a compiled reference that does
    not bind, a load-breaking type gap, or a Harmony/lookup FAIL), 0 otherwise. GATED,
    OPTIONAL and UNDECIDED findings are printed but never fail the run; the textual check is
    informational too. Works from any directory.

.PARAMETER ModDll
    Mod DLL to check. Default: dist\slay_the_streamer_2\slay_the_streamer_2.dll (what build.ps1 produces).

.PARAMETER GameDlls
    Game DLLs to check against. Default: every decompiled\dll-archive\sts2-*.dll.

.PARAMETER TextualTree
    Optional flat ILSpy-decompiled tree (e.g. decompiled\sts2-v0.107.1) for a game version with no DLL.

.PARAMETER ExtDir
    Folder with GodotSharp.dll and the BCL, used to follow base types that leave sts2.
    Default: the Steam game data folder when it exists. -NoExt disables it.

.EXAMPLE
    pwsh -File tools\refcheck\run.ps1
.EXAMPLE
    pwsh -File tools\refcheck\run.ps1 -ModDll C:\some\slay_the_streamer_2.dll -TextualTree decompiled\sts2-v0.107.1
#>
[CmdletBinding()]
param(
    [string]$ModDll,
    [string[]]$GameDlls,
    [string]$TextualTree,
    [string]$ExtDir,
    [switch]$NoExt,
    [switch]$Rebuild
)
$ErrorActionPreference = 'Stop'
$toolDir = $PSScriptRoot
$repo = (Resolve-Path (Join-Path $toolDir '..\..')).Path

function Resolve-RepoPath([string]$p) {
    if ([IO.Path]::IsPathRooted($p)) { return $p }
    $inRepo = Join-Path $repo $p
    if (Test-Path $inRepo) { return (Resolve-Path $inRepo).Path }
    return (Resolve-Path $p).Path   # relative to the current directory
}

if (-not $ModDll) { $ModDll = Join-Path $repo 'dist\slay_the_streamer_2\slay_the_streamer_2.dll' }
$ModDll = Resolve-RepoPath $ModDll
if (-not (Test-Path $ModDll)) { throw "Mod DLL not found: $ModDll (run build.ps1, or pass -ModDll)" }

if (-not $GameDlls) {
    $GameDlls = Get-ChildItem (Join-Path $repo 'decompiled\dll-archive') -Filter 'sts2-*.dll' | Sort-Object Name | ForEach-Object FullName
} else {
    $GameDlls = $GameDlls | ForEach-Object { Resolve-RepoPath $_ }
}
if (-not $GameDlls) { throw "No game DLLs found (decompiled\dll-archive\sts2-*.dll); pass -GameDlls" }

if (-not $NoExt -and -not $ExtDir) {
    $default = 'C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64'
    if (Test-Path (Join-Path $default 'GodotSharp.dll')) { $ExtDir = $default }
}

# ---- build if needed (inside the repo, so global.json's SDK pin applies; net9.0)
$exe = Join-Path $toolDir 'bin\Release\net9.0\refcheck.exe'
$sources = Get-ChildItem $toolDir -File | Where-Object { $_.Extension -in '.cs', '.csproj' }
$stale = $Rebuild -or -not (Test-Path $exe) -or
    ($sources | Where-Object { $_.LastWriteTime -gt (Get-Item $exe).LastWriteTime })
if ($stale) {
    Write-Host 'building refcheck...'
    Push-Location $toolDir
    try { dotnet build -c Release -nologo -v q | Out-Host; if ($LASTEXITCODE -ne 0) { throw 'refcheck build failed' } }
    finally { Pop-Location }
}

$out = Join-Path $toolDir 'out'
New-Item -ItemType Directory -Force $out | Out-Null

$modInfo = Get-Item $ModDll
Write-Host "mod: $ModDll ($($modInfo.Length) bytes, $($modInfo.LastWriteTime.ToString('yyyy-MM-dd HH:mm')))"
if ($ExtDir) { Write-Host "ext: $ExtDir" } else { Write-Host 'ext: (none; base chains stop at the first non-game type)' }

$anyFail = $false
$firstDump = $null
foreach ($g in $GameDlls) {
    $name = [IO.Path]::GetFileNameWithoutExtension($g)
    $report = Join-Path $out "$name.txt"
    $dump = Join-Path $out "$name.refs.tsv"
    if (-not $firstDump) { $firstDump = $dump }
    $toolArgs = @($ModDll, $g, '--dump', $dump)
    if ($ExtDir) { $toolArgs += @('--ext', $ExtDir) }
    & $exe @toolArgs *> $report
    $code = $LASTEXITCODE
    $lines = Get-Content $report
    $summary = $lines | Where-Object { $_ -like 'REFCHECK-SUMMARY *' } | Select-Object -Last 1
    Write-Host ''
    if (-not $summary) {
        Write-Host "== $name : TOOL ERROR (exit $code); see $report" -ForegroundColor Red
        $lines | Select-Object -Last 15 | ForEach-Object { Write-Host "   $_" }
        $anyFail = $true
        continue
    }
    $kv = @{}
    foreach ($pair in ($summary -replace '^REFCHECK-SUMMARY ', '').Split(' ')) { $k, $v = $pair.Split('=', 2); $kv[$k] = $v }
    $color = if ($kv.result -eq 'PASS') { 'Green' } else { 'Red' }
    Write-Host ("== {0} : {1}" -f $name, $kv.result) -ForegroundColor $color
    Write-Host ("   compiled refs {0} ({1} fail, {2} unverifiable) | load-breaking {3} | Harmony targets OK {4} | FAIL {5} | GATED {6} | UNDECIDED {7} | optional not found {8}" -f `
        $kv.compiledRefs, $kv.compiledFail, $kv.unverifiable, $kv.loadBreaking, $kv.harmonyTargetsOk, $kv.fail, $kv.gated, $kv.undecided, $kv.optionalNotFound)

    # failure details: compiled-ref failures and Harmony/lookup sections
    function Show-Section([string]$header, [string]$label, [string]$color) {
        $i = [Array]::FindIndex([string[]]$lines, [Predicate[string]] { param($l) $l.StartsWith($header) })
        if ($i -lt 0) { return }
        $j = $i + 1
        while ($j -lt $lines.Count -and -not $lines[$j].StartsWith('===')) {
            if ($lines[$j].Trim()) { Write-Host "   $label $($lines[$j].Trim())" -ForegroundColor $color }
            $j++
        }
    }
    if ([int]$kv.compiledFail -gt 0) {
        $lines | Where-Object { $_ -match '^\[(TYPE MISSING|MEMBER MISSING|SIGNATURE MISMATCH|ACCESS)' } | ForEach-Object { Write-Host "   FAIL $_" -ForegroundColor Red }
    }
    if ([int]$kv.loadBreaking -gt 0) {
        $lines | Where-Object { $_ -match 'LOAD-BREAKING' } | ForEach-Object { Write-Host "   FAIL $($_.Trim())" -ForegroundColor Red }
    }
    Show-Section '=== HARMONY/LOOKUP FAILURES' 'FAIL' 'Red'
    Show-Section '=== GATED' 'GATED' 'Yellow'
    Show-Section '=== UNDECIDED' 'UNDECIDED' 'Yellow'
    Show-Section '=== OPTIONAL LOOKUPS NOT FOUND' 'optional' 'DarkGray'
    Write-Host '   resolutions:'
    $lines | Where-Object { $_ -like 'REFCHECK-RESOLUTION *' } | ForEach-Object { Write-Host ('     ' + ($_ -replace '^REFCHECK-RESOLUTION ', '')) }
    Write-Host "   full report: $report"
    if ($code -ne 0) { $anyFail = $true }
}

if ($TextualTree) {
    $tree = Resolve-RepoPath $TextualTree
    $treeName = Split-Path $tree -Leaf
    $treport = Join-Path $out "textual-$treeName.txt"
    & $exe --textual $firstDump $tree --out $treport | Out-Null
    Write-Host ''
    Write-Host "== textual check against $treeName (regex-level, informational; dump from $(Split-Path $firstDump -Leaf))"
    Get-Content $treport | Where-Object { $_ -match '^(TYPE|METHOD|FIELD|HARMONY|LOOKUP)\s+\d+:' } | ForEach-Object { Write-Host "   $_" }
    $tl = Get-Content $treport
    foreach ($sec in '=== ABSENT', '=== NEEDS-MANUAL-CHECK') {
        $i = [Array]::FindIndex([string[]]$tl, [Predicate[string]] { param($l) $l.StartsWith($sec) })
        if ($i -lt 0) { continue }
        $j = $i + 1
        while ($j -lt $tl.Count -and -not $tl[$j].StartsWith('===')) {
            if ($tl[$j] -match '^  \[') { Write-Host ("   {0} {1}" -f ($sec -replace '=== ', ''), $tl[$j].Trim()) }
            $j++
        }
    }
    Write-Host "   full report: $treport"
}

Write-Host ''
if ($anyFail) { Write-Host 'refcheck: REAL FAILURES FOUND' -ForegroundColor Red; exit 1 }
Write-Host 'refcheck: no real failures' -ForegroundColor Green
exit 0
