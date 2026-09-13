# Slay the Streamer 2

A Slay the Spire 2 mod that lets your Twitch chat (and optionally YouTube chat) vote on the choices you make during a run: Neow's and the Ancients' blessings, card rewards, the act boss, and the Act 1 variant. It also gives you a small budget to overrule chat, and chat gets to name your enemies.

> 🎮 Tested against Slay the Spire 2 Beta `v0.111.0`.

Inspired by [Tempus's StS1 Slay the Streamer](https://github.com/Tempus/SlayTheStreamer). No code from that mod is used here.

---

## ▶️ Demo

[Watch the v0.1 demo on Twitch](https://www.twitch.tv/videos/2782265574). The mod has grown since (overrides, relic choices, voter-named enemies, remove-one votes), but the core loop in the video is unchanged.

---

## ⬇️ Download

Subscribe on the [Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3761888849), or grab `slay_the_streamer_2-vX.Y.Z.zip` from the [latest GitHub release](https://github.com/Surfinite/slay-the-streamer-2/releases/latest).

---

## 🛠 Install (manual zip)

1. Open your Steam Slay the Spire 2 folder (Steam, right-click the game, Manage, Browse local files).
2. Inside it, open the `mods` folder. Create it if it isn't there.
3. Extract the zip into `mods/` so you end up with:
   ```
   Slay the Spire 2/
     mods/
       slay_the_streamer_2/
         slay_the_streamer_2.dll
         slay_the_streamer_2.json
         slay_the_streamer_2.json.example
   ```
4. Launch the game.

If the game doesn't see the mod, open `%APPDATA%\SlayTheSpire2\logs\godot.log` and look for a line starting with `[slay_the_streamer_2]`. If it isn't there, DM me (Surfinite) on Discord.

---

## 🔌 Connect your Twitch chat

Your credentials live in a settings file in the game's user-data folder, not in the mod install folder:

```
%APPDATA%\SlayTheSpire2\slay_the_streamer_2.json
```

There is also a `slay_the_streamer_2.json` inside `mods/slay_the_streamer_2/`. That one is the mod manifest the loader reads. Putting credentials in it does nothing.

1. Launch the game once with the mod installed. The mod creates a settings template at the path above.
2. Open the folder. The easiest way is in game: settings menu, mod list, Slay the Streamer 2, then the "Open settings folder" button. Or paste `%APPDATA%\SlayTheSpire2` into Explorer's address bar.
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

- `channel` is your Twitch channel name in lowercase, for example `"surfinite"`.
- `username` is the account that posts chat receipts ("Vote opened...", "Chat picked X"). Most solo streamers use their own account, or the bot account they already use for other tools.
- `oauthToken` is a chat token for the `username` account. It needs the `chat:read` and `chat:edit` scopes and must start with `oauth:`.

The `username` account's votes don't count. The mod ignores messages from that account because its own receipts contain vote-shaped text like `#0` and `#1`. Using your own account is fine, since chat does the voting, but you then can't test voting by typing in your own chat. Test from a second account or ask a friend.

Already have a chat bot (Nightbot, StreamElements, your own)? Paste its OAuth token with the `oauth:` prefix and its username here.

New to this? The fastest path is a token generator such as [twitchtokengenerator.com](https://twitchtokengenerator.com/). Pick "Bot Chat Token", log in as the bot account, copy the Access Token, and paste it with the `oauth:` prefix. Twitch's own documentation on chat scopes and OAuth is at [dev.twitch.tv/docs/authentication](https://dev.twitch.tv/docs/authentication/) and [dev.twitch.tv/docs/irc/authenticate-bot](https://dev.twitch.tv/docs/irc/authenticate-bot/).

Save the file and restart the game. The mod connects at launch. Within a few seconds you should see `slay-the-streamer-2 connected (Twitch).` in your Twitch chat, posted by the `username` account. During runs you'll get chat messages when votes open and close, and the tally overlay on screen while a vote runs.

### 🎥 Optional: also read YouTube chat

If you stream to YouTube as well, set `youtubeChannelId` to your YouTube channel ID, a string that starts with `UC` followed by 22 characters. The mod reads your YouTube live chat in parallel and merges YouTube and Twitch votes into one tally.

YouTube shows channels by `@handle` now, so the ID isn't in the URL. To find it:

1. Sign in at [studio.youtube.com](https://studio.youtube.com/) as the account that streams.
2. Click Settings (the gear, bottom-left of the sidebar).
3. Pick Channel, then Advanced settings.
4. Copy the value labelled Channel ID.

Google's reference: [Find your channel's user ID & channel ID](https://support.google.com/youtube/answer/3250431).

YouTube is read-only. Receipts still only post to Twitch, because posting to YouTube chat requires Google verification.

---

## 🗳 How chat votes

When a vote opens, chat types one of:

- `#0`, `#1`, `#2`, ... to pick that option. Options are numbered from 0, the same convention as the original StS1 mod.
- `#0` is the skip option on card rewards when chat-skip is enabled.
- Bare numbers (`0`, `1`, `2`) also work.
- `#1!42` votes for vote ID `42` specifically. Useful for stream-delayed YouTube viewers when two votes land close together.

The tally posts to Twitch chat at open, at intervals during the vote, and at close. It also shows as a small overlay on screen while the vote runs.

Votes close after 30 seconds by default. The duration is a setting (10 to 120 seconds).

---

## ✨ What chat votes on

| Decision | What happens |
|---|---|
| Card rewards after combat | Chat picks which of the three cards joins your deck. Chat can also vote to skip if you enable it. |
| Card rewards from anywhere else | Depends on the Non-combat card rewards setting. In the default Mixed mode, card rewards from Ancient relics (Kaleidoscope, Glass Eye, Lost Coffer, Hefty Tablet, Lead Paperweight) and from Dream Catcher use a remove-one vote: chat votes which option to remove, and you pick from the rest. Card rewards from events and shop relics (Orrery) can't be skipped and have no vote. Every affected relic and event says so in its own text. |
| Neow and the Ancients | At Neow and at every mid-run Ancient event (Pael, Tezcatara, Orobas, Nonupeipe, Tanx, Vakuu, Darv), chat picks the blessing. |
| Act boss | When you click Proceed out of a treasure chest, chat picks which of three candidate bosses you fight at the end of the act. The vote popup shows animated combat-idle portraits. At Ascension 10 the final act has two bosses and chat votes on both. |
| Act 1 variant | When you click Embark, chat picks Underdocks or Overgrowth. Toggleable. |

Two vanilla Custom Mode modifiers get special handling:

- Sealed Deck. You draft your starting 10 cards from the 30-card grid; chat doesn't vote on that. Once the run begins, chat votes on card rewards as normal. If Neow's blessings are available in your sealed run (Pikcube's Run Modifiers' Always Whale does that), two tweaks apply: Neow's Talisman upgrades 2 random cards and makes them Doomed (5 Doom to yourself each time you play one) instead of upgrading the Strikes and Defends you don't have, and Leafy Poultice and Precarious Shears are never offered. Outside sealed runs everything is vanilla.
- Draft. The run starts with 10 pick-1-of-3 screens. You draft; chat never votes on those picks, in any mode. Chat votes on card rewards once the run begins.

Sealed Deck and Draft are mutually exclusive in Custom Mode. Vanilla locks Custom Mode behind three standard-mode wins. On the modded save you can open the dev console (`~`) and run `unlock all` instead.

### 🎛 Streamer-side extras (not chat votes)

- Vote overrides. A per-act budget (default 1) to overrule chat. While a card-reward or Ancient vote is counting down, click the option you want, or Skip, and the vote ends with your pick. Chat is told, for example `Surfinite overrode the vote and took Ricochet. 0 overrides remaining this act`. Skipping mid-vote costs an override, not a card skip. After chat removes an option, clicking the removed option spends an override to take it anyway. The remaining budget shows on screen whenever an override is available.
- Relic choices. Treasure chests and elite kills can offer 2 to 4 relics instead of 1. You pick one and the rest go back into the pool.
- Cursed Overrides (off by default). Every override you spend also adds a random curse to your deck, with the vanilla card-added animation and a chat receipt naming it (`... Cursed Overrides: gained Injury!`). The curse is drawn from the game's generic curse pool; special-purpose curses like Ascender's Bane are excluded.
- Enemies named after voters (on by default). Enemies are named after chatters who vote, shown under their intent icons. Names are drawn by raffle: every vote you take part in earns one ticket (one per vote, so spamming numbers doesn't help), each enemy draws by ticket, and being drawn spends your tickets until you vote again. Repeat draws of the same chatter become "Jr.", then "III". With the companion setting on, a named enemy also speaks its chatter's messages as speech bubbles. Bubble text is the raw chat message, so your channel moderation is the filter. You can turn just the bubbles off.

---

## 🤝 Mod compatibility

- Slay the Relics reborn (listed in game as `SlayTheRelicsExporter`). Tested side by side, no conflicts. It pushes your run state to a Twitch extension overlay; this mod reads chat votes. The two don't touch the same code.
- Balls2, StS1 Boss Ancients, Haxxero's More Relics. Their combat card rewards vote normally, their custom Ancients get the Ancient vote, and More Relics' Strongbox is unskippable like Orrery. Checked against the decompiled mods; in-game validation is on the to-do list.

---

## ⚙️ In-game settings

Open the in-game settings menu and pick Slay the Streamer 2 in the mod list. You'll see:

- Vote duration. 10 to 120 seconds, default 30.
- Vote on Act 1 variant. Turns the pre-run Underdocks/Overgrowth vote on or off.
- Allow same boss twice (A10). Ascension 10's final act has two bosses and chat votes on both. When on, the second vote may pick the same boss again.
- Allow chat to skip. When on, chat can vote `#0` to skip a card reward.
- Non-combat card rewards. Since v0.4.0; it replaces the old "Card-reward votes only occur after combat" checkbox, which migrates automatically.
  - Mixed (default): Ancient-relic and Dream Catcher card rewards get a remove-one vote; event and shop-relic card rewards can't be skipped and have no vote.
  - Remove-one: every non-combat card reward gets the remove-one vote. Orrery becomes five removal votes and Colorful Philosophers three, so this mode is slower.
  - Free: chat only votes on combat card rewards. Everything else is a free streamer pick.
  - In Mixed and Remove-one, a blue "Slay the Streamer:" line on each relic and event explains the active rule. Beta branch only: the default branch lacks the hook this needs, so there every card reward is a normal pick vote.
- Streamer card skips / act. How many card rewards you can skip per act (0 / 1 / 2 / 3 / 5 / Unlimited).
- Streamer vote overrides / act. How many times per act you can override a running vote (0 / 1 / 2 / 3 / Unlimited, default 1). Clicks in the first 1.5 seconds of a countdown are ignored, so an accidental double-click that opened the vote can't spend an override.
- Cursed Overrides. Each override you spend also adds a random curse. Off by default.
- Name enemies after chat voters. On by default.
- Enemy chat message duration. How long a named enemy shows its chatter's messages as a speech bubble. Default 5 seconds; Off disables bubbles. Only applies while naming is on.
- Show vote tag. Shows the `[NN]` vote-ID tag in chat receipts and the on-screen tally. Helpful when your YouTube chat has stream delay.
- Vote tally side. Which side of the screen the tally overlay sits on.
- Relic choices. How many relics you choose from per chest and elite kill (1 to 4, default 1). 1 is vanilla.
- Settings file. A read-only path with an Open-folder button that reveals `%APPDATA%\SlayTheSpire2\` in Explorer.

During a card-reward vote, the skip counter under the cards swaps to your remaining vote overrides (in gold), so you always know whether an override click is available. The same line shows on the Ancient vote screen.

The settings panel is disabled mid-run. Change settings between runs; changes save automatically.

Twitch credentials and the YouTube channel ID stay in the JSON file. They're kept out of the in-game UI so they never appear on stream.

---

## ⚠️ Known caveats

- The game itself can crash silently right after a modded launch (opens, then closes within seconds, nothing in the log). This is a Slay the Spire 2 bug in its crash-reporter teardown, not specific to this mod, reported to MegaCrit ([details](https://github.com/megacrit/sts2-mod-uploader/issues/14)). It hasn't happened to me in months, but if it hits you repeatedly, add `--force-sentry` to the game's Steam launch options.
- The modded save is its own profile. Your unmodded progress is untouched, and modded runs don't count toward unlocks. The boss vote samples the act's full boss pool, so chat may pick bosses you haven't unlocked on your unmodded save. To unlock things on the modded save, open the dev console (`~`) and run `unlock all`.
- Twitch rate-limits chat receipts under heavy back-to-back voting (20 messages per 30 seconds for regular accounts). The vote still works, but some "Vote opened..." or "Chat picked X" messages may not appear.

---

## 🙏 Credits

- Concept from [Tempus's original StS1 Slay the Streamer mod](https://github.com/Tempus/SlayTheStreamer) ([Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=1610759491)). This is a from-scratch StS2 implementation of the same idea; none of that repo's code is used.
- MIT licensed. See [LICENSE](LICENSE).

---

## 🧰 For developers and contributors

Everything below is about the code. Streamers can stop reading here.

The mod is split into two namespaces:

- `src/Ti/` is the game-agnostic chat-integration layer (Twitch IRC, YouTube chat scraper, voting state machine, tally overlay). It has no `MegaCrit.Sts2.*` references, so it can be lifted out into a base mod later.
- `src/Game/` is the StS2-specific glue: Harmony patches, settings, popups. It depends on `Ti/` and on `sts2.dll`.

### Repo layout

```
slay-the-streamer-2/
  README.md                 this file
  LICENSE                   MIT
  CLAUDE.md                 project workflow rules and landmines (for AI-assisted dev)
  src/                      the mod
    Ti/                       game-agnostic chat-integration core
      Chat/                     IChatConsumer / IChatService
                                TwitchIrcChatService (IRC client + send queue)
                                MultiChatService (N-platform aggregator)
                                YouTubeChat/  read-only youtubei scraper
      Voting/                   VoteSession / VoteCoordinator / Voter / EnglishReceipts
                                per-platform tally, vote-tag (!NN) parsing
      Internal/                 IClock / ITimerScheduler / IMainThreadDispatcher / TiLog and fakes
      Ui/                       VoteTallyLabel (the corner tally overlay)
    Godot/                    GodotMainThreadDispatcher + DispatcherAutoload
    Game/                     StS2-specific glue
      Bootstrap/                ModEntry init, ModSettings (JSON config), SettingsBootstrap
      DecisionVotes/            Harmony patches per voted decision, reward classifier, text
      Content/                  sealed-deck Neow tweaks (Talisman rework, relic disables)
      DevCommands/              dev-console commands (rerollvote, resetskips, votenow)
      Ui/                       per-vote popups, budget counters, in-game settings panel
    ModEntry.cs               [ModInitializer] entry point
    slay_the_streamer_2.csproj
    slay_the_streamer_2.json  mod manifest
    slay_the_streamer_2.json.example  template config (the loader skips .json.example)
    icon.svg, project.godot   needed for Godot.NET.Sdk compilation
  tests/                    xUnit test project (source-referenced, no DLL refs)
  workshop/                 Steam Workshop upload workspace (workshop.json, image.png)
  docs/superpowers/         specs and implementation plans
  notes/                    research notes, operator test matrices, follow-ups
  build.ps1                 refresh DLLs from the game install, dotnet publish, dotnet test, assemble dist/
  install.ps1               copy dist/ to <game-install>/mods/
  uninstall.ps1             remove from <game-install>/mods/
```

Not in the repo (gitignored, created locally):

```
  references/               reference repos cloned per workspace (not redistributable)
    SlayTheStreamer-sts1/     Tempus's StS1 original, feature reference only
    STS2FirstMod/             jiegec's StS2 example mod
  decompiled/sts2-vX.Y.Z/   ILSpy output on sts2.dll, one folder per game version
  decompiled/sts2-assets/   extracted game assets (scenes, images)
  src/sts2.dll              copied per build from the game install
  src/0Harmony.dll          copied per build from the game install
  dist/                     build artefacts
```

### Build and install from source

Requires the .NET 9 SDK, Godot 4.5.1 Mono (for the `Godot.NET.Sdk` csproj), and a Slay the Spire 2 install. Then:

```powershell
pwsh -File build.ps1     # publish, run tests, assemble dist/
pwsh -File install.ps1   # copy dist/ to <game>/mods/
```

`build.ps1` copies `sts2.dll` and `0Harmony.dll` from your game install each run. They aren't redistributed in the repo. `install.ps1` only copies; it doesn't rebuild.

### Recreating the gitignored workspace dirs

```sh
git clone https://github.com/Tempus/SlayTheStreamer.git references/SlayTheStreamer-sts1
git clone https://github.com/jiegec/STS2FirstMod.git    references/STS2FirstMod

# Requires ILSpy CLI (ilspycmd 9.x). Decompile from a copy of the DLL with no sts2.xml
# beside it, or ILSpy embeds doc comments into every file and diffs become useless.
ilspycmd "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64\sts2.dll" \
  -o decompiled/sts2-v0.111.0 -p
```

### Design notes

- Specs and implementation plans live under `docs/superpowers/`.
- Research notes, operator matrices, and follow-ups live under `notes/`.
- Workflow rules and the landmine list live in `CLAUDE.md`.
