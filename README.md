# Slay the Streamer 2

A **Slay the Spire 2** mod that lets your Twitch chat (and optionally YouTube
chat) vote on the choices you make during a run — Ancient blessings, card
rewards, the act boss, and the Act 1 variant.

Inspired by [Tempus's StS1 Slay the Streamer](https://github.com/Tempus/SlayTheStreamer).

---

## ▶️ Demo

**[Watch the v0.1 demo on Twitch →](https://www.twitch.tv/videos/2782265574)**

---

## ⬇️ Download

**[Get the latest release →](https://github.com/Surfinite/slay-the-streamer-2/releases/latest)**

Grab the `slay_the_streamer_2-vX.Y.Z.zip` asset from the release page.

---

## 🛠 Install

1. Open your Steam Slay the Spire 2 folder. (Steam → right-click the game → Manage → Browse local files.)
2. Inside it, open the `mods` folder (create it if it isn't there).
3. Extract the zip from above into `mods/` so you end up with:
   ```
   Slay the Spire 2/
     mods/
       slay_the_streamer_2/
         slay_the_streamer_2.dll
         slay_the_streamer_2.json
         slay_the_streamer_2.json.example
   ```
4. Launch the game.

If the game can't see the mod, check the log at `%APPDATA%\SlayTheSpire2\logs\godot.log` — you should see a line that starts with `[slay_the_streamer_2]`.  DM me (Surfinite) on Discord if you encounter problems.

---

## 🔌 Connect your Twitch chat

Your credentials live in a settings file in the game's **user-data folder** — *not* in the mod install folder:

```
%APPDATA%\SlayTheSpire2\slay_the_streamer_2.json
```

> ⚠️ There is also a `slay_the_streamer_2.json` inside `mods/slay_the_streamer_2/` — that one is the **mod manifest** (loader metadata). Putting credentials in it does nothing.

1. **Launch the game once** with the mod installed — the mod creates a settings template at the path above. (On mod versions up to 0.1.1 it doesn't yet: copy `mods/slay_the_streamer_2/slay_the_streamer_2.json.example` into `%APPDATA%\SlayTheSpire2\` and rename it to `slay_the_streamer_2.json`.)
2. **Open the folder** — easiest is in-game: settings menu → mod list → **Slay the Streamer 2** → the **Open settings folder** button. Or paste `%APPDATA%\SlayTheSpire2` into Explorer's address bar.
3. Open `slay_the_streamer_2.json` in any text editor. You'll see:

```json
{
  "schemaVersion": 1,
  "channel": "your_twitch_channel",
  "username": "your_twitch_bot_username",
  "oauthToken": "oauth:your_30_character_lowercase_alphanumeric_token",
  "youtubeChannelId": null,
  ...
}
```

Fill in three fields:

- **`channel`** — your Twitch channel name in lowercase (e.g. `"surfinite"`).
- **`username`** — the account that will post chat receipts ("Vote opened…", "Chat picked X"). Most solo streamers use their own account here, or the same bot account they already use for other tools.
- **`oauthToken`** — a chat token for the `username` account. It needs the `chat:read` and `chat:edit` scopes, and must be prefixed with `oauth:` (e.g. `"oauth:abc123…"`).

**Already have a chat bot?** If you've already got a Twitch bot set up (Nightbot, StreamElements, your own, etc.), you can reuse its credentials — just paste the existing OAuth token (with the `oauth:` prefix) and the bot's username here.

**New to this?** The fastest path is a token generator like [twitchtokengenerator.com](https://twitchtokengenerator.com/) — pick "Bot Chat Token", log in as the bot account, copy the Access Token, paste it with the `oauth:` prefix. For the official Twitch documentation on chat scopes and OAuth flows, see [dev.twitch.tv/docs/authentication](https://dev.twitch.tv/docs/authentication/) and [dev.twitch.tv/docs/irc/authenticate-bot](https://dev.twitch.tv/docs/irc/authenticate-bot/).

Save the file and **restart the game** (the mod connects at launch). To verify it worked, watch your Twitch chat: within a few seconds of the game starting you should see a message like `slay-the-streamer-2 connected (Twitch).` posted by the `username` account. During runs you'll then get chat-side messages when votes open and close, and the in-game tally overlay during votes.

### 🎥 (Optional) Also read YouTube chat

If you stream to YouTube as well, set `youtubeChannelId` to your YouTube channel ID — a string that starts with `UC` followed by 22 characters (e.g. `"UCabcdefghijklmnopqrstuv"`). The mod will read your YouTube live chat in parallel — votes from YT and Twitch are merged into a single tally.

**Finding your YouTube channel ID:**

YouTube now displays your channel via your `@handle` rather than the channel ID, so the ID isn't shown in the URL anymore on most channels. To look it up:

1. Sign in at [studio.youtube.com](https://studio.youtube.com/) as the YouTube account that streams.
2. Click **Settings** (gear icon, bottom-left of the sidebar).
3. In the popup, pick **Channel** → **Advanced settings**.
4. Copy the value labelled **Channel ID** (starts with `UC`).

Official Google reference: [Find your channel's user ID & channel ID](https://support.google.com/youtube/answer/3250431).

**Note:** YouTube is read-only. Chat receipts ("Vote opened…", etc.) still only post in Twitch chat because posting to YT chat requires Google verification.

---

## 🗳 How chat votes

When a vote opens, chat types one of:

- `#0`, `#1`, `#2`, ... — pick the corresponding option.
- `#0` is the **skip** option for card rewards (when chat-skip is enabled).
- Bare numbers (`0`, `1`, `2`, ...) also work, but `#1` is the established convention from the original Slay the Streamer mod and the format chat is most likely to recognise.
- `#1!42` — vote precisely for vote ID `42`. Useful for stream-delayed YT viewers if back-to-back votes might collide.

The tally renders in chat (Twitch only) at vote open, periodically during the vote, and at close. It also appears on screen as a small overlay during the vote.

Votes time out after 30 seconds by default (configurable, 10–120s).

---

## ✨ What chat votes on (v0.1)

| Decision | Description |
|---|---|
| **Card rewards** | After each fight, chat picks which of the 3 cards is added to your deck. Chat can also skip if you enable it. |
| **Ancient relics** | When you encounter an Ancient event (Pael, Tezcatara, Orobas, Nonupeipe, Tanx, Vakuu, Darv), chat picks the relic. |
| **Act boss** | When you click "Proceed" out of a treasure chest, chat picks which of 3 candidate bosses you'll face at the end of the act. Bosses get animated combat-idle portraits. |
| **Act 1 variant** | When you click "Embark", chat picks Underdocks vs Overgrowth before the run starts. (Toggleable.) |

The mod also plays nicely with two vanilla Custom Mode modifiers:

- **Sealed Deck** — **you** draft your starting 10 cards from a 30-card grid (chat does *not* vote on this draft). Once the run begins, chat votes on every card reward that follows, exactly like a normal run.
- **Draft** — the run starts with 10 sequential pick-1-of-3 screens. Because these reuse the standard card-reward UI, **chat votes on every pick** — i.e. chat fully drafts your starting deck.

`SealedDeck` and `Draft` are mutually exclusive in Custom Mode. Note: vanilla Custom Mode is locked behind 3 standard-mode wins (or unlock everything on the modded save via `unlock all` in the dev console, as in the caveats below).

---

## 🤝 Mod compatibility

- **Slay the Relics reborn** (appears as `SlayTheRelicsExporter` in the in-game mod list) — tested side-by-side and they play fine together. The two mods do disjoint things: Slay the Relics pushes your run state to a Twitch extension overlay (viewers hover relics/cards on the stream), and this mod reads chat votes. No known conflicts.

---

## ⚙️ In-game settings

Open the in-game settings menu and pick **Slay the Streamer 2** in the mod list. You'll see:

- **Vote duration** — 10 to 120 seconds (default 30s).
- **Vote on Act 1 variant** — turn the pre-run Underdocks/Overgrowth vote on or off.
- **Allow chat to skip** — when on, chat can vote `#0` to skip a card reward.
- **Show vote tag** — show the `[NN]` vote-ID tag in chat receipts and the on-screen tally. Helpful if your YT chat has stream delay.
- **Card skips per act** — how many card rewards **you** can skip per act (0 / 1 / 2 / 3 / 5 / Unlimited).
- **Settings file** — read-only path with an Open-folder button to reveal `%APPDATA%\SlayTheSpire2\` in Explorer.

The settings panel is disabled mid-run — change settings between runs. Changes save automatically.

**Twitch credentials and the YouTube channel ID stay in the JSON file** — they're not in the in-game UI to keep them off-screen during stream.

---

## ⚠️ Known caveats for v0.1

- **Modded save is its own profile** — your unmodded progress is untouched, and modded runs don't count toward unlocks. The boss vote samples the act-variant's full boss pool, so chat may pick bosses that aren't unlocked on your unmodded save. If you want to also unlock things on the modded save, open the dev console (`~`) and run `unlock all`.
- **Twitch chat receipts can get rate-limited** under heavy back-to-back voting (Twitch caps regular accounts at 20 messages per 30 seconds). The vote still works, but some "Vote opened…" / "Chat picked X" messages may not appear in chat.

---

## 🙏 Credits

- Concept and design inspired by [**Tempus**'s original StS1 Slay the Streamer mod](https://github.com/Tempus/SlayTheStreamer) ([Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=1610759491)). No code from that repo is incorporated; this is a from-scratch StS2 implementation that derives from the *concept*.
- MIT licensed — see [LICENSE](LICENSE).

---

## 🧰 For developers / contributors

Everything below is for poking at the code. Streamers can stop reading here.

The mod is split into two namespaces:

- `src/Ti/` — extractable, game-agnostic chat-integration layer (Twitch IRC, YouTube chat scraper, voting state machine, UI). No `MegaCrit.Sts2.*` references.
- `src/Game/` — StS2-specific Harmony patches + settings loader. Depends on `Ti/` and on `sts2.dll`.

### Repo layout

```
slay-the-streamer-2/
  README.md                 this file
  LICENSE                   MIT
  CLAUDE.md                 project workflow rules + landmines (for AI-assisted dev)
  src/                      the mod
    Ti/                       extractable multi-platform chat-integration core
      Chat/                     IChatConsumer / IChatService surface
                                TwitchIrcChatService (IRC client + send queue)
                                MultiChatService (N-platform aggregator)
                                YouTubeChat/  read-only youtubei scraper
      Voting/                   VoteSession / VoteCoordinator / Voter / EnglishReceipts
                                per-platform tally side-dict; vote-nonce (!NN) parsing
      Internal/                 IClock / ITimerScheduler / IMainThreadDispatcher / TiLog + fakes
      Ui/                       Ti-side Godot UI (VoteTallyLabel — split per-platform rendering)
    Godot/                    GodotMainThreadDispatcher + DispatcherAutoload
    Game/                     StS2-specific glue (Harmony patches, settings, popups)
      Bootstrap/                ModEntry init + ModSettings (JSON config reader)
      DecisionVotes/            Harmony patches per voted decision
      DevCommands/              dev-console commands (rerollvote, resetskips, votenow)
      Ui/                       per-vote popups (BossVotePopup, ActVariantVotePopup,
                                CardRewardVotePopup, AncientVotePopup, CardSkipCounterLabel,
                                in-game Settings panel)
    ModEntry.cs               [ModInitializer] entry point
    slay_the_streamer_2.csproj
    slay_the_streamer_2.json  mod manifest
    slay_the_streamer_2.json.example  template config (mod loader skips .json.example)
    icon.svg, project.godot   needed for Godot.NET.Sdk compilation
  tests/                    xUnit test project (source-referenced, no DLL refs)
  docs/superpowers/         specs + implementation plans + meta-reviews (the build-out story)
  notes/                    research notes, hook-point inventory, follow-ups
  build.ps1                 refresh DLLs from game install → dotnet publish → dotnet test → assemble dist/
  install.ps1               copy dist/ to <game-install>/mods/
  uninstall.ps1             remove from <game-install>/mods/
```

Not in the repo — gitignored, created locally:

```
  references/               reference repos cloned per workspace (not redistributable)
    SlayTheStreamer-sts1/     Tempus's StS1 original, feature reference only
    STS2FirstMod/             jiegec's StS2 example mod
  decompiled/sts2/          ILSpy output on sts2.dll (regenerable)
  src/sts2.dll              copied per-build from the game install
  src/0Harmony.dll          copied per-build from the game install
  dist/                     build artefacts
```

### Build + install from source

Requires .NET 9 SDK, Godot 4.5.1 Mono (for the `Godot.NET.Sdk` csproj), and a Slay the Spire 2 install. Then:

```powershell
pwsh -File build.ps1     # publish + run tests + assemble dist/
pwsh -File install.ps1   # copy dist/ → <game>/mods/
```

`build.ps1` copies `sts2.dll` and `0Harmony.dll` from your game install each run; they're not redistributed in the repo.

### Recreating gitignored workspace dirs

```sh
git clone https://github.com/Tempus/SlayTheStreamer.git references/SlayTheStreamer-sts1
git clone https://github.com/jiegec/STS2FirstMod.git    references/STS2FirstMod

# Requires ILSpy CLI (ilspycmd 9.x)
ilspycmd "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64\sts2.dll" \
  -o decompiled/sts2 --nested-directories -p
```

### Design notes

- Architecture, slice plans, and meta-reviews live under `docs/superpowers/`.
- Research notes, hook-point inventory, and follow-ups live under `notes/`.
- Per-project workflow rules and landmines live in `CLAUDE.md`.
