# Original Slay the Streamer (StS1) feature inventory

Source read: `references/SlayTheStreamer-sts1/`. Tempus/Chronometrics, ~14 Java
files, ~2,000 LOC. Built on **BaseMod** + **ModTheSpire** + **robojumper's
Twitch Integration mod** (`de.robojumper.ststwitch`).

The headline finding: Slay the Streamer is *thin*. Most "chat votes on X"
features came from the base Twitch Integration mod. Slay the Streamer adds:
- A sealed-deck game-mode rule
- Two specific votes that Twitch Integration didn't cover (Neow, Act bosses)
- Cosmetic chat-as-monster features
- Some game tweaks to support all of the above

## Architecture

```
ModTheSpire (loader)
└─ BaseMod (mod API)
   └─ Twitch Integration (de.robojumper.ststwitch)   <- the real work lives here
      ├─ TwitchConnection      (IRC connection)
      ├─ TwitchVoter           (vote tally engine)
      ├─ TwitchVoteListener    (subscribe to tally events)
      ├─ TwitchMessageListener (subscribe to raw chat messages)
      ├─ TwitchPanel           (config UI)
      ├─ Native voting on card rewards, map paths, events, shops, etc.
      │   (CardRewardScreen.renderTwitchVotes is its method, not Tempus's)
      └─ Sends "VOTE NOW: ..." and "Voting ended..." back to chat
   └─ Slay the Streamer (chronometry.*)              <- this repo
      ├─ Sealed deck setup (StartGamePatch.chooseCards)
      ├─ Neow voting (StartGamePatch — wires NeowEvent into TwitchVoter)
      ├─ Boss-act voting (BossSelectScreen + BossSelectRoom + BossChoicePatch)
      ├─ Monster naming (MonsterNamesPatch + Map<String,String> displayNames)
      ├─ Chat speech bubbles (MonsterMessageRepeater — listens to all messages)
      ├─ Shopkeeper naming (ShopkeeperNamesPatch)
      ├─ No-skip boss relic (NoSkipBossRelicPatch — for sealed deck integrity)
      ├─ Force Whale + final act available (settings tweak)
      ├─ Remove Pandora's Box from boss relic pool (would nullify sealed deck)
      ├─ Cosmetic decorations (versus banner, FacesOfEvil image, Hexaghost spinner)
      └─ Hand-edited JSON config (ConfigPanel UI is *literally commented out*)
```

**Implication for our project**: there is no "Twitch Integration for StS2" we
can stand on. We must implement both layers. That argues strongly for a
**monolithic mod** in v0.1 (option B from session 1) — clean module
boundaries inside one mod is simpler than coordinating two mods, and the
original's reliance on `ReflectionHacks.getPrivate(...)` to reach the base
mod's listener list is exactly the kind of mess we'd reproduce by going
two-mod.

## Per-feature breakdown

### Voting features

| Feature | Source in StS1 | Lines | Comes from |
|---|---|---|---|
| Card reward vote | `CardRewardPatch.java` | 31 (decoration only) | **Twitch Integration** |
| Neow blessing vote | `StartGamePatch.java` | ~150 (of 314) | **Slay the Streamer** |
| Act boss vote | `BossSelectScreen` + `BossSelectRoom` + `BossChoicePatch` | 534 total | **Slay the Streamer** |
| Map path vote | not in this repo | — | **Twitch Integration** |
| Event choice vote | not in this repo | — | **Twitch Integration** |
| Shop purchase vote | not in this repo | — | **Twitch Integration** |
| Boss relic vote | `NoSkipBossRelicPatch.java` partial | 222 | **Twitch Integration** + tweak |

**Read for v0.1 design**: we'll need to reimplement the *Twitch Integration*-tier
votes (card, map, event, shop, boss relic) in our mod ourselves, since no
underlying mod exists for StS2.

### Sealed deck (`StartGamePatch.chooseCards`)

Replaces the player's starting deck with a chat-curated selection:
1. Wipe master deck, keep only Ascender's Bane.
2. Build a pool: N rares + M uncommons + K commons + (PoolSize - N - M - K) randoms.
3. Open `gridSelectScreen` for the player to pick `CardPickChoices` cards.
4. Default config: 2 rares, 5 uncommons, 10 commons, pool=30, choices=10.

Side effects required to make this work:
- Force `Settings.isTestingNeow = true` (guarantees Whale appears so vote happens)
- Force `Settings.isFinalActAvailable = true`
- Remove Pandora's Box from boss relic pool (would replace the curated deck)
- No-skip boss relic (forces engagement with chat-curated outcomes)

**Open question for v0.1 scoping**: is sealed deck *in* v0.1 or is it polish?
- *Pro v0.1*: it's the eponymous mechanic. Without it, "Slay the Streamer 2" is
  just "chat votes on streamer's choices" which is the more generic Twitch
  Integration concept.
- *Pro v0.1.5*: it's substantial work (deck wipe, pool gen, custom selection UI,
  game-mode tweaks). Could ship "votes-only v0.1" first and add sealed deck
  for v0.2. Lower risk, faster to first useful release.
- **Decision deferred** to design phase.

### Chat-as-monster features (all post-MVP polish)

| Feature | Source | Logic |
|---|---|---|
| Monsters named after chat voters | `MonsterNamesPatch.java` (256 lines) | uses `usedNames` / `displayNames` / `votedTimes` maps populated from votes |
| Chat speech bubbles on matched monsters | `MonsterMessageRepeater.java` (39 lines) | listens to all chat messages; if first word matches a monster's first word, draw a `SpeechBubble` for ≤64 chars |
| Shopkeeper named from pool | `ShopkeeperNamesPatch.java` (76 lines) | picks from config string `MerchantNames` (default: `Casey,Anthony`) |

The monster-titles config string lists ~95 default adjectives/epithets
(`Painbringer`, `Snecko's Eye`, `Lord of Reptiles`, ... `Backward-Compatible`,
`Bleeding-Edge`, ... `Underappreciated`). Useful flavour reference if we ever
ship this feature.

### Game-mode tweaks (only relevant if sealed deck is in v0.1)

| Tweak | Source | Why |
|---|---|---|
| Force Whale (Neow always shows) | `Settings.isTestingNeow = true` in `SlayTheStreamer.receivePostInitialize` | so vote-on-Neow always has a Neow to vote on |
| Force final act | `Settings.isFinalActAvailable = true` in `receiveStartGame` | so the chat-curated run can reach the secret end |
| Remove Pandora's Box | `bossRelicPool.removeIf(...)` in `receivePostDungeonInitialize` | would replace the entire chat-curated deck |
| No-skip boss relic | `NoSkipBossRelicPatch.java` (222 lines) | forces engagement |

### Cosmetic / UI

| Feature | Source | Effect |
|---|---|---|
| Mod badge on main menu | `BaseMod.registerModBadge(...)` | standard mod banner with config panel |
| Versus banner during card votes | `CardRewardPatch.openHook` | banner reads "...for the player" (localised) |
| FacesOfEvil image overlay | `CardRewardPatch.renderTwitchVotes` | decorative image while card vote is in progress |
| Hexaghost spinner on boss-select screen | `HexaghostModel.java` (126 lines) | static-rendering Hexaghost monster (no AI), used as backdrop accent on boss-select |
| Title-logo override | `images/ui/title_logo/` | mod's own version of the StS title logo |

### Configuration surface (the JSON file)

Default values from `SlayTheStreamer.setDefaultPrefs()`:

```
CardPickPool        = 30          // sealed deck pool size
CardPickChoices     = 10          // how many the streamer picks
GuaranteedRares     = 2
GuaranteedUncommons = 5
GuaranteedCommons   = 10

VoteOnBosses        = true
VoteOnNeow          = true

MerchantNames       = "Casey,Anthony"
MonsterTitles       = "Painbringer,Snecko's Eye,...(~95 entries)..."
```

**The actual ConfigPanel UI is entirely commented out.** Users edit
`%LOCALAPPDATA%\ModTheSpire\config\SlayTheStreamer\config` JSON by hand.
This is a clear v2 improvement opportunity for our mod: ship an actual
in-game settings UI from day one.

### Known issues left in code

- `// crashes if you try to restart the run` — restart-during-run was buggy.
- `// I don't think this is activating when I want it to.` (StartGamePatch.WaitForDeckConstruction)
  — screen-state transitions during sealed deck construction were flaky.
- `Active monsters could have a listener that lets the user talk on screen`
  — confirms chat-bubbles was on the roadmap, not just "shipped polish".

### Localisation

English, Korean, Russian. Strings live under
`src/main/resources/SlayTheStreamer/localizations/{eng,kor,rus}/uiStrings.json`.
**TwirkPatch.java** patches the underlying Twirk IRC client to use UTF-8
encoding so non-ASCII (e.g. Korean) chat names render properly. We get this
for free with any modern .NET IRC library.

## Mapping to our v0.1 MVP

Recap of v0.1 scope from session 1:

> Card rewards, Neow blessings, event choices, boss reward picks, shop
> purchases, map path selection. Read-only IRC. No chat bubbles, no overlays.

Mapped against this inventory:

| Our v0.1 feature | Original location | What we inherit | What we build |
|---|---|---|---|
| Card reward vote | Twitch Integration's job | nothing — no StS2 base mod | full impl |
| Neow vote | StartGamePatch (+ deck construction) | feature design only | full impl |
| Event choice vote | TI's job | nothing | full impl |
| Boss reward pick vote | TI's job (NoSkipBossRelicPatch is a tweak) | nothing | full impl |
| Shop purchase vote | TI's job | nothing | full impl |
| Map path vote | TI's job | nothing | full impl |
| (Implied: act-boss vote) | BossSelectScreen + BossSelectRoom + BossChoicePatch | feature design only | maybe defer? |

**Unread files** (~556 lines remaining) — we should read these before
finalising design if act-boss voting and no-skip-boss-relic are in v0.1:
- `BossSelectScreen.java` (345)
- `NoSkipBossRelicPatch.java` (222)
- `BossChoicePatch.java` (145)
- `BossSelectRoom.java` (44)
- `MonsterNamesPatch.java` (256) — only matters if monster-naming is in v0.1
- `ShopkeeperNamesPatch.java` (76) — same
- `MainMenuDisplayPatch.java` (39) — purely cosmetic, low priority

## Decisions to make in design phase

1. **Architecture**: confirm monolithic v0.1 (recommended), or factor IRC layer
   into a separate "Twitch Integration for StS2" base mod for the modding
   community.
2. **Sealed deck in v0.1**: yes (preserves the "Slay the Streamer" identity,
   ~3-5 days extra work) or no (pure-voting v0.1, sealed deck for v0.2).
3. **Act-boss voting in v0.1**: yes (matches original) or no (it's
   substantial — 500+ lines of custom screen — and the underlying StS2 boss
   selection might already give us hooks that didn't exist in StS1).
4. **Outgoing chat messages**: per session 1, default to none. Confirmed.
5. **Localisation in v0.1**: probably English-only, structure for i18n from day 1.
