# slay-the-streamer-2

A Slay the Spire 2 mod that lets chat (Twitch and optionally YouTube)
vote on the streamer's in-game decisions. Inspired by [Tempus's StS1
Slay the Streamer](https://github.com/Tempus/SlayTheStreamer) (Steam
Workshop [1610759491](https://steamcommunity.com/sharedfiles/filedetails/?id=1610759491)).

**Status**: v0.1 in active development, with v0.2 multi-platform chat operationally validated.

- **B.1 Neow vote** shipped 2026-05-10 (`plan-b-1-complete` tag) — chat votes on Neow's blessing end-to-end with real Twitch IRC.
- **B.2.1 card reward vote** shipped 2026-05-11 (`plan-b-2-1-complete` tag) — chat votes on which of the typically-3 cards the streamer adds to deck; mandatory-look skip gate prevents skipping-without-engaging; per-act skip budget (default `cardSkipsPerAct: 1`) caps how often the streamer can override chat.
- **v0.2 YouTube chat parallel integration** landed 2026-05-12 — optional `youtubeChannelId` setting wires a read-only YouTube live-chat reader alongside Twitch via a `MultiChatService` aggregator. Votes from both platforms merge into a single tally; in-game label renders per-platform rows when YT is configured. Chat receipts continue to fire on Twitch only (YouTube posting requires Google verification / OAuth, intentionally not pursued). Per-vote nonce (`!NN` suffix, optional opt-in) lets stream-delayed YT viewers vote precisely on a specific vote ID without colliding with back-to-back votes; bare `#N` still works, with option numbers stable across votes (no Noita-style alternation) so chat doesn't have to remember a shifting numbering scheme. End-to-end validated 2026-05-12 against a real live YouTube broadcast (chain: discovery → page-parse → poll → JSON-extract → vote-regex → tally → UI rendering → game-state apply).

Remaining v0.1 slices: B.2.2 start-of-act Ancient-rarity relic vote (StS2 replaced StS1 boss relics with these — granted by Event encounters like Pael / Tezcatara), B.2.3 map path vote, B.2.4 in-game settings UI, B.3 act boss. **Not yet for end users** — installation requires manual JSON config and the modded save is its own profile (no unlock progression yet).

## Repo layout

What's in the repo:

```
slay-the-streamer-2/
  README.md                  this file
  LICENSE                    MIT
  src/                       the mod
    Ti/                        extractable multi-platform chat-integration core (no Godot, no sts2.dll refs)
      Chat/                      IChatConsumer / IChatService surface
                                 TwitchIrcChatService (IRC client + chat message + send queue)
                                 MultiChatService (N-platform aggregator)
                                 YouTubeChat/  read-only youtubei scraper (discovery + poller)
      Voting/                    VoteSession / VoteCoordinator / Voter / EnglishReceipts
                                   per-platform tally side-dict; vote-nonce (!NN) parsing
      Internal/                  IClock / ITimerScheduler / IMainThreadDispatcher / TiLog + fakes
      Ui/                        Godot UI (VoteTallyLabel — split per-platform rendering)
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
- Start-of-act Ancient-rarity relic picks via Pael / Tezcatara / etc. (StS2's replacement for StS1's boss relics)
- Map path selection
- Act boss (custom screen — likely needs its own sub-plan, B.3)

Works via vanilla Custom Mode with no mod-side code:
- **Sealed-deck draft start** — vanilla StS2 already ships a `SealedDeck` modifier in Custom Mode. Streamer ticks it on the Custom Run screen, picks the character, embarks. The Neow event becomes a single "Sealed Deck" option that opens a 30-card grid; the streamer drafts 10 from 30 (vanilla numbers, hardcoded). Run continues from there with our existing B.2.1 card-reward / Ancients / etc. voting intact. Tempus's StS1 mod had a streamer-drafted sealed deck and then chat antagonised via subsequent voting; vanilla StS2's Custom Mode produces the same experience without any mod-side draft screen of our own. See [`notes/08`](notes/08-sealed-deck-custom-mode-investigation.md) for the full investigation. Note: vanilla Custom Mode is locked behind 3 standard-mode wins.
- **Chat-controlled deck construction** — the sibling `Draft` modifier (mutually exclusive with `SealedDeck`) opens 10 sequential pick-1-of-3 reward screens for the streamer to build the run's deck. Because those screens are exactly the surface our B.2.1 card-reward voting hooks, ticking `Draft` in Custom Mode produces a fully chat-controlled deck construction with zero new code from us.

Deferred to v0.2 as new-design problems (not in the original mod or its
base-mod dependency, so each is a fresh design pass rather than a port):
- Event choice voting
- Shop purchase voting
- Chat bubbles on monsters
- Custom monster names

Out of scope entirely:
- Twitch Extension overlays
- Sending data back to Twitch beyond outgoing chat receipts

Twitch direction is **read-only IRC + outgoing chat receipts**.
YouTube (optional, v0.2) is **read-only scraping** via the public
`youtubei` internal endpoint (no quota, no OAuth) — receipts never
fire on YouTube; YT viewers see the in-game tally label only. The
streamer's screen is the shared display; viewers type vote commands
like `#0`, `#1`, `#2` in chat (or `#1!42` for vote-ID-precise voting
when stream delay risks landing on the wrong vote). Vote tally is
rendered both in chat (periodic + open + close receipts, Twitch
only) and in-game (small overlay label; per-platform rows when YT
is configured).

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
