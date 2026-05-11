# slay-the-streamer-2

A Slay the Spire 2 mod that lets Twitch chat vote on the streamer's
in-game decisions. Inspired by [Tempus's StS1 Slay the Streamer](https://github.com/Tempus/SlayTheStreamer)
(Steam Workshop [1610759491](https://steamcommunity.com/sharedfiles/filedetails/?id=1610759491)).

**Status**: v0.1 in active development.

- **B.1 Neow vote** shipped 2026-05-10 (`plan-b-1-complete` tag) — chat votes on Neow's blessing end-to-end with real Twitch IRC.
- **B.2.1 card reward vote** shipped 2026-05-11 (`plan-b-2-1-complete` tag) — chat votes on which of the typically-3 cards the streamer adds to deck; mandatory-look skip gate prevents skipping-without-engaging; per-act skip budget (default `cardSkipsPerAct: 1`) caps how often the streamer can override chat.

Remaining slices: B.2.2 start-of-act boon-god special-relic vote (StS2 replaced StS1 boss relics with these), B.2.3 map path vote, B.2.4 in-game settings UI, B.3 act boss. **Not yet for end users** — installation requires manual JSON config and the modded save is its own profile (no unlock progression yet).

## Repo layout

What's in the repo:

```
slay-the-streamer-2/
  README.md                  this file
  LICENSE                    MIT
  src/                       the mod
    Ti/                        extractable Twitch-integration core (no Godot, no sts2.dll refs)
      Chat/                      IRC client + chat message + send queue
      Voting/                    VoteSession / VoteCoordinator / Voter / EnglishReceipts
      Internal/                  IClock / ITimerScheduler / IMainThreadDispatcher / TiLog + fakes
      Ui/                        Godot UI (VoteTallyLabel)
      Godot/                     GodotMainThreadDispatcher + DispatcherAutoload
    Game/                      StS2-specific glue (Harmony patches, settings)
      Bootstrap/                 ModSettings (JSON config reader)
      DecisionVotes/             Harmony patches per voted decision
    ModEntry.cs                [ModInitializer] entry point
    slay_the_streamer_2.csproj
    slay_the_streamer_2.json   mod manifest
  tests/                     xUnit test project (source-referenced, no DLL refs)
  docs/superpowers/          specs + implementation plans + meta-reviews (the build-out story)
  notes/                     research notes, hook-point inventory, follow-ups
  build.ps1                  refresh DLLs from game install -> dotnet publish -> dotnet test -> assemble dist/
  install.ps1                copy dist/ to <game-install>/mods/
  uninstall.ps1              remove from <game-install>/mods/
```

Not in the repo — gitignored, created locally by the "Setting up a fresh workspace" steps below:

```
  references/                cloned reference repos (not our code; redistributing isn't ok)
    SlayTheStreamer-sts1/      original StS1 mod, Java/LibGDX, feature reference only
    STS2FirstMod/              jiegec's StS2 example mod
  decompiled/sts2/           output of ILSpy on sts2.dll (MegaCrit's source, regenerable)
  src/sts2.dll               copied per-build from the game install
  src/0Harmony.dll           copied per-build from the game install
  dist/                      build artefacts
```

## Scope (v0.1 MVP)

Chat votes on the **core decisions** the original StS1 mod (or its
underlying Twitch Integration base mod) covered:

- Neow blessings (✅ shipped in B.1, 2026-05-10)
- Card rewards (✅ shipped in B.2.1, 2026-05-11)
- Start-of-act boon-god special-relic picks (StS2's replacement for StS1's boss relics)
- Map path selection
- Act boss (custom screen — likely needs its own sub-plan, B.3)

Deferred to v0.2 as new-design problems (not in the original mod or its
base-mod dependency, so each is a fresh design pass rather than a port):
- Event choice voting
- Shop purchase voting
- **Sealed-deck draft start** — Tempus's StS1 mod opened a run with a sealed deck that chat drafted, then chat played the role of "make the deck worse" through ongoing voting. Major mode pivot, not a small feature; specced as a post-B.3 sub-plan.

Out of scope entirely:
- Chat bubbles on monsters
- Custom monster names
- Twitch Extension overlays
- Sending data back to Twitch beyond outgoing chat receipts

Twitch direction is **read-only IRC + outgoing chat receipts**. The
streamer's screen is the shared display; viewers type vote commands
like `#0`, `#1`, `#2` in chat. Vote tally is rendered both in chat
(periodic + open + close receipts) and in-game (small overlay label).

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
- Godot 4.5.1 Mono at `C:\Tools\Godot_4.5.1_mono\` — required for the build pipeline (`Godot.NET.Sdk` csproj)
- VS Code with Claude Code extension
- StS2 install at `C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\`
- Game DLL at `data_sts2_windows_x86_64\sts2.dll`

## Working notes

See `notes/` for the running research log, hook-point inventory,
and any open questions.

## Licence

[MIT](LICENSE) — do whatever you want with this code as long as the
licence + copyright stays attached. The original StS1 mod (Tempus's
[Slay the Streamer](https://github.com/Tempus/SlayTheStreamer)) has no
declared licence, so this project derives from its *concept* only — no
code from that repo is incorporated.
