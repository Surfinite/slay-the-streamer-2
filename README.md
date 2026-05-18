# slay-the-streamer-2

A Slay the Spire 2 mod that lets chat (Twitch and optionally YouTube)
vote on the streamer's in-game decisions. Inspired by [Tempus's StS1
Slay the Streamer](https://github.com/Tempus/SlayTheStreamer) (Steam
Workshop [1610759491](https://steamcommunity.com/sharedfiles/filedetails/?id=1610759491)).

**Status**: v0.1 in active development, with v0.2 multi-platform chat operationally validated.

- **B.1 Neow vote** shipped 2026-05-10 (`plan-b-1-complete` tag) — chat votes on Neow's blessing end-to-end with real Twitch IRC.
- **B.2.1 card reward vote** shipped 2026-05-11 (`plan-b-2-1-complete` tag) — chat votes on which of the typically-3 cards the streamer adds to deck; mandatory-look skip gate prevents skipping-without-engaging; per-act skip budget (default `cardSkipsPerAct: 1`) caps how often the streamer can override chat.
- **v0.2 YouTube chat parallel integration** landed 2026-05-12 — optional `youtubeChannelId` setting wires a read-only YouTube live-chat reader alongside Twitch via a `MultiChatService` aggregator. Votes from both platforms merge into a single tally; in-game label renders per-platform rows when YT is configured. Chat receipts continue to fire on Twitch only (YouTube posting requires Google verification / OAuth, intentionally not pursued). Per-vote nonce (`!NN` suffix, optional opt-in) lets stream-delayed YT viewers vote precisely on a specific vote ID without colliding with back-to-back votes; bare `#N` still works, with option numbers stable across votes (no Noita-style alternation) so chat doesn't have to remember a shifting numbering scheme. End-to-end validated 2026-05-12 against a real live YouTube broadcast (chain: discovery → page-parse → poll → JSON-extract → vote-regex → tally → UI rendering → game-state apply).
- **B.2.2 ancient vote** shipped 2026-05-14 (`plan-b-2-2-complete` tag) — chat votes on the Ancient-rarity relic offered by mid-run Ancient events: Pael, Tezcatara, Orobas (Act 2), Nonupeipe, Tanx, Vakuu (Act 3), and Darv (cross-act via `AllSharedAncients`). Implemented as predicate-widening on the B.1 Neow patch (renamed `NeowBlessingVotePatch` → `AncientVotePatch`) — the patch now bails to vanilla unless the event model is `AncientEventModel and not DeprecatedAncientEvent`, so future ancients MegaCrit ships will auto-work. Neow's vote title regresses cosmetically from "Neow's Bonus" to "Neow's Offering" as the auto-derivation trade-off.
- **B.3 act boss vote** shipped 2026-05-15 (`plan-b-3-complete` tag) — chat votes on the upcoming act boss every chest-room exit. Up to 3 candidates sampled from `runState.Act.AllBossEncounters` via a stable FNV-1a-32 seed of `(StringSeed, ActIndex)` so save-reload shows the same options; A10+ DoubleBoss runs exclude the pre-rolled second boss from the candidate pool. Resolved via `MapCmd.SetBossEncounter` through the established two-flag suspend-and-resume pattern. First slice to ship a modal `CanvasLayer` popup: portrait + title + live tally per column, hides on dev-console / pause-menu open (preserving access to all vanilla overlays), cancels promptly on mid-vote run-death. Idempotency-with-verification handles Golden Compass's two-chest map, the map-screen back-arrow, and StS2 save-quit rollback (silently re-applies the original chat-picked boss instead of re-voting).
- **B.3.1 combat-idle boss portraits** shipped 2026-05-16 (`plan-b-3-1-complete` tag) — replaces B.3's static PNG portraits with animated combat-idle sprites rendered via `MonsterModel.CreateVisuals()`, fixing the empty-column bug for Spine-only bosses (Ceremonial Beast was the canary; future Spine-only bosses inherit the fix). Bounds-aware centering anchors each sprite correctly regardless of native origin convention. Pre-warm Stopwatch telemetry shows 76–82ms cold-load per vote — within "harmless" envelope. Pause-menu / dev-console occlusion freezes Spine playback via Godot-native `ProcessMode.Disabled` cascade (no `SetTimeScale` API contact). Multi-monster encounter handling picks the visual primary (`THE_KIN_BOSS → KIN_PRIEST`; `QUEEN_BOSS`, `KAISER_CRAB_BOSS` use index 0). New `rerollvote` dev console command re-opens the current vote with a fresh sample for fast iteration; generation-tracking guarantees the cancelled session's stale resume bails cleanly. Public-interface MegaCrit-free seam preserved with two `private static` casts localized to `BossVotePopup`. `PortraitFit.ComputeFitScale` carved out as a pure-math unit-testable helper.
- **B.3.2 act-variant vote** shipped 2026-05-18 (`plan-b-3-2-complete` tag) — chat votes between the two Act 1 variants (Underdocks vs Overgrowth) on Embark, before the run starts. Implemented as a Harmony prefix on `OnEmbarkPressed` rather than the downstream `BeginRunLocally`, so the vanilla UI-mutation sequence (`Disable` embark/back/character buttons → `SetReady(true)`) never runs on cancel — popup teardown + ESC backout become clean no-ops, no UI restoration code path needed. `TargetMethods()` covers both `NCharacterSelectScreen` (Standard mode) and `NCustomRunScreen` (Custom mode + Sealed-deck / Draft modifier runs) from one patch class with a small type-dispatch. The 50/50 split popup pre-warms each variant's full layered combat scene via `BackgroundAssets` + `NCombatBackground.Create`, parented under a Center-anchored zero-size Control to mirror vanilla's `BgContainer` framing (FullRect anchoring shifts the texture's center off-screen — surfaced and fixed mid-validation). Title bands use Spectral Bold gold over a dim `ColorRect` strip styled from vanilla's `act_banner.tscn`; countdown timer occupies the "Act N" slot above. On confirm, the patch sets `Lobby.Act1` and reflectively re-invokes the same `OnEmbarkPressed` with a synthetic-resume flag so vanilla's full body runs unmodified. Operator validation surfaced and resolved four real bugs (popup leaked on `VoteSession.Cancelled` because it only listened to `Closed`; popup Control was never added to the scene tree so `_Process`/`_Input` never fired; backgrounds rendered top-half-only from FullRect anchoring; Custom mode missed because the patch was single-target on `NCharacterSelectScreen`) — all four are now CLAUDE.md Tier-4 landmines for future maintainers.

Remaining v0.1 slices: B.2.4 in-game settings UI. **Not yet for end users** — installation requires manual JSON config and the modded save is its own profile (no unlock progression yet).

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

Chat votes on the **core decisions**:

- Neow blessings (✅ shipped in B.1, 2026-05-10)
- Card rewards (✅ shipped in B.2.1, 2026-05-11)
- Start-of-act Ancient-rarity relic picks via Pael / Tezcatara / etc. — StS2's replacement for StS1's boss relics (✅ shipped in B.2.2, 2026-05-14)
- Act boss (✅ shipped in B.3, 2026-05-15; combat-idle animated portraits in B.3.1, 2026-05-16; pre-run Act-1 variant pick in B.3.2, 2026-05-18)

Works via vanilla Custom Mode with no mod-side code:
- **Sealed-deck draft start** — vanilla StS2 already ships a `SealedDeck` modifier in Custom Mode. Streamer ticks it on the Custom Run screen, picks the character, embarks. The Neow event becomes a single "Sealed Deck" option that opens a 30-card grid; the streamer drafts 10 from 30 (vanilla numbers, hardcoded). Run continues from there with our existing B.2.1 card-reward / Ancients / etc. voting intact. Tempus's StS1 mod had a streamer-drafted sealed deck and then chat antagonised via subsequent voting; vanilla StS2's Custom Mode produces the same experience without any mod-side draft screen of our own. See [`notes/08`](notes/08-sealed-deck-custom-mode-investigation.md) for the full investigation. Note: vanilla Custom Mode is locked behind 3 standard-mode wins.
- **Chat-controlled deck construction** — the sibling `Draft` modifier (mutually exclusive with `SealedDeck`) opens 10 sequential pick-1-of-3 reward screens for the streamer to build the run's deck. Because those screens are exactly the surface our B.2.1 card-reward voting hooks, ticking `Draft` in Custom Mode produces a fully chat-controlled deck construction with zero new code from us.

Deferred to v0.2 as new-design problems:
- Event choice voting
- Shop purchase voting
- Chat bubbles on monsters
- Custom monster names
- Map path selection

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
