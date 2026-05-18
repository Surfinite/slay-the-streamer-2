# StS2 game-asset extraction

**Purpose**: Unpack `SlayTheSpire2.pck` so Claude sessions (and you) can browse the game's real asset paths instead of guessing at `res://` URLs. Especially useful when picking thumbnails/backdrops/icons for mod UI — LLMs can't look at images, but they can grep paths once the tree exists on disk.

**Output lives at** `decompiled/sts2-assets/` (gitignored via `decompiled/`).

## Tool

[GDRE Tools (gdsdecomp)](https://github.com/GDRETools/gdsdecomp) — the actively-maintained Godot RE Tools fork. Handles Godot 4 specifics that simpler `.pck` extractors don't:

- Decodes `.ctex` (Godot 4 CompressedTexture2D) back to viewable `.png`.
- Converts binary `.scn` scenes to text `.tscn`.
- Decompiles `.gdc` scripts to `.gd` (not relevant for StS2 since it's C#).
- Recovers `.import` metadata so paths in code match files on disk.

**Installed at**: `C:\Tools\gdre\gdre_tools.exe` (v2.5.0-beta.5 as of 2026-05-18).

To refresh the binary later:
```powershell
$url = (gh release view --repo GDRETools/gdsdecomp --json assets `
        | ConvertFrom-Json).assets `
        | Where-Object { $_.name -like "*windows.zip" } `
        | Select-Object -ExpandProperty url
Invoke-WebRequest -Uri $url -OutFile "C:\Tools\gdre\GDRE_tools-windows.zip" -UseBasicParsing
Expand-Archive "C:\Tools\gdre\GDRE_tools-windows.zip" -DestinationPath "C:\Tools\gdre\" -Force
```

## Extraction command

```powershell
& "C:\Tools\gdre\gdre_tools.exe" --headless `
    --recover="C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\SlayTheSpire2.pck" `
    --output-dir="C:\Users\Surfinite\slay-the-streamer-2\decompiled\sts2-assets"
```

Runs ~2 minutes, produces ~2.65 GB / ~27k files. Re-run after a game update (the `.pck` changes).

`--recover` does full project recovery (decode textures, convert scenes, decompile scripts). For a faster raw extract that leaves `.ctex` files as-is, use `--extract=` instead — but you'll need a Godot editor to view the textures, so `--recover` is the right default for browsing.

## Output structure

Mirrors `res://`. Useful entry points:

| Need | Where |
|---|---|
| Combat-room backdrop layers (PNG, per-zone) | `images/rooms/<zone>/<zone>_NN_X.png` |
| Composite combat-backdrop scene | `scenes/backgrounds/<zone>/<zone>_background.tscn` |
| Map node icons | `images/map/...` |
| Map zone backgrounds (the strips behind the map) | `images/packed/map/map_bgs/<zone>/map_{top,middle,bottom}_<zone>.png` |
| Card art | `images/packed/card_art/...` |
| Card frames / overlays | `images/card_overlays/...` |
| Relics | `images/relics/...` |
| Monster portraits | `images/monsters/...` |
| Character art | `images/characters/...` |
| Boss assets (Spine + PNG fallbacks) | `images/map/<zone>_boss/...` and `scenes/backgrounds/<boss>_boss/` |
| Decompiled C# (sts2.dll → reformatted .cs) | NOT here — see `decompiled/sts2/` (ILSpy output) |

`scenes/backgrounds/<zone>/overgrowth_background.tscn` is a `Control` node running `NCombatBackground.cs`, composing 5 parallax `Layer_NN` slots + a `Foreground` slot + particles (`fireflies`, etc.). Each slot is filled by an `overgrowth_bg_NN_X.tscn` from the `layers/` subdir.

### Don't confuse "map background" with "combat backdrop"

`images/packed/map/map_bgs/overgrowth/map_middle_overgrowth.png` is the **strip behind the map screen** (vertical scrolling map). It is **not** what you see during a combat encounter. If you want the combat scene for a zone, go to `images/rooms/<zone>/` (layered PNGs) or `scenes/backgrounds/<zone>/<zone>_background.tscn` (composite).

For a single-image preview thumbnail (e.g. in a chat-vote popup), `<zone>_00.png` is usually the back/sky layer and reads well at small size.

## Notes

- Some sprites are Spine, not PNG. Boss creatures especially — see the `BossNodeSpineResource` landmine in `CLAUDE.md`. Spine resources surface as `.tres` files referencing `.skel` + atlas; you can't view these as flat images without rendering them.
- Recovery reports `Lossy: N` for textures that were imported with quality-loss compression in the original pipeline. Re-export is bit-identical to what the game ships; it's only "lossy" relative to the source PSDs MegaCrit holds privately.
- Some scripts fail to decompile (19/3415 on the 2026-05-18 run). Those are usually trivial — Godot's `.gdc` decompiler doesn't matter for an all-C# game like StS2.
- The output tree includes a working `.godot/`, `global.json`, and `sts2.sln` — you could in theory open it in Godot editor 4.5.1 Mono and explore interactively. Useful if a `.tscn` doesn't make sense from text alone.
