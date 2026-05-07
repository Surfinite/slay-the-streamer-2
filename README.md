# slay-the-streamer-2

A Slay the Spire 2 mod that lets Twitch chat vote on the streamer's
in-game decisions. Inspired by [Tempus's StS1 Slay the Streamer](https://github.com/Tempus/SlayTheStreamer)
(Steam Workshop [1610759491](https://steamcommunity.com/sharedfiles/filedetails/?id=1610759491)).

Currently in research / preparation phase — no mod code yet.

## Repo layout

```
slay-the-streamer-2/
  README.md                 this file
  notes/                    research notes, hook-point map, design fragments
  references/               cloned reference repos (read-only — do not modify)
    SlayTheStreamer-sts1/     original StS1 mod, Java/LibGDX, feature reference only
    STS2FirstMod/             jiegec's StS2 example mod, the toolkit reference
  decompiled/               output of ILSpy on sts2.dll (regenerable, gitignored)
    sts2/                     per-namespace .cs tree + .csproj
    ilspy.log                 decompile log
```

## Scope (v0.1 MVP)

Chat votes on the **core decisions only**:

- Card rewards
- Neow blessings
- Event choices
- Boss reward picks
- Shop purchases
- Map path selection

Out of scope for v0.1 (post-MVP polish):
- Chat bubbles on monsters
- Custom monster names
- Twitch Extension overlays
- Anything requiring sending data back to Twitch

Twitch direction is **read-only IRC**. The streamer's screen is the
shared display; viewers type vote commands like `#1` in chat.

## Setting up a fresh workspace

`references/` and `decompiled/` are gitignored. To recreate them:

```sh
# Reference repos
git clone https://github.com/Tempus/SlayTheStreamer.git references/SlayTheStreamer-sts1
git clone https://github.com/jiegec/STS2FirstMod.git    references/STS2FirstMod

# Decompiled game source (requires ILSpy CLI - see Toolchain below)
ilspycmd "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64\sts2.dll" \
  -o decompiled/sts2 --nested-directories -p
```

## Toolchain

- Windows 11
- Git
- .NET 9 SDK (`9.0.313`)
- ILSpy CLI (`ilspycmd 9.1.0.7988`) for decompiling `sts2.dll`
- VS Code with Claude Code extension
- StS2 install at `C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\`
- Game DLL at `data_sts2_windows_x86_64\sts2.dll`

## Working notes

See `notes/` for the running research log, hook-point inventory,
and any open questions.
