# Session log

Running diary of research sessions. Newest entry at the top.

---

## 2026-05-07 — Session 1: workspace + tooling setup

**Goal**: get the foundations in place so future research sessions are fast.

**Done**:
- Confirmed StS2 install at `C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\`.
- Located the game DLL: `data_sts2_windows_x86_64\sts2.dll` (~8.7 MB).
- Installed .NET 9 SDK (9.0.313) via winget.
- Installed ILSpy CLI as a dotnet global tool (`ilspycmd 9.1.0.7988`).
  - Pinned to 9.1.x because the latest 10.0.1 NuGet package has a malformed
    `DotnetToolSettings.xml` and `dotnet tool install` rejects it.
- Cloned reference repos into `references/`:
  - `Tempus/SlayTheStreamer` (StS1 original) — feature spec, not transferable code.
  - `jiegec/STS2FirstMod` — the toolkit reference for StS2 modding.
- Kicked off `ilspycmd` against `sts2.dll` into `decompiled/sts2/` with `--nested-directories -p`.
- Initialised the project repo with README and gitignore.

**Things learned**:
- StS2 ships **HarmonyLib** (`0Harmony.dll`) and **MonoMod** alongside the
  game runtime. Translation: runtime patching is a sanctioned modding mechanism,
  not just a hack. We can patch any game method when the official API doesn't
  expose what we need.
- StS2 bundles **.NET 9 runtime** with the game (`mscordaccore_amd64_amd64_9.0.725...dll`).
  Mods must target .NET 9.
- StS2 mods aren't plain .NET class libraries — they're **Godot projects**
  (jiegec's example has `project.godot` and `export_presets.cfg`). The build
  pipeline goes through Godot's exporter. Need to understand exactly why and
  what that buys us — open question for next session.
- StS2 has `Steamworks.NET.dll`, `Sentry.dll`, `SmartFormat.dll` shipped.

**Decompile output**:
- 3,386 `.cs` files, 24 MB. Top-level namespaces under `MegaCrit.sts2.Core/`:
  Achievements, Animation, Assets, Audio, AutoSlay, Bindings, CardSelection,
  **Combat**, Commands, Context, ControllerInput, Daily, Debug, DevConsole,
  Entities, **Events**, Exceptions, Extensions, Factories, GameActions,
  Helpers, **Hooks**, HoverTips, Leaderboard, Localization, Logging, **Map**,
  **Modding**, Models, MonsterMoves, Multiplayer, Nodes, Odds, Platform, Random,
  **Rewards**, RichTextTags, **Rooms**, **Runs**, Saves, Settings, TestSupport,
  TextEffects, Timeline, Unlocks, ValueProps.
- The bolded ones are the ones we'll spend most time in.
- `MegaCrit.sts2.Core.Modding/` is *tiny* — 13 files. The full surface:
  `Mod.cs`, `ModInitializerAttribute.cs`, `ModManager.cs`, `ModManifest.cs`,
  `ModLoadState.cs`, `ModSource.cs`, `ModSettings.cs`, `SettingsSaveMod.cs`,
  `ModHelper.cs`, `IModManagerFileIo.cs`, `ModManagerFileIo.cs`,
  `CombatHookSubscriptionDelegate.cs`, `RunHookSubscriptionDelegate.cs`.

**Big de-risking signal**: The presence of `RunHookSubscriptionDelegate.cs`
suggests an *official* event-hook system for run-level events (Neow, map,
rewards, events, shops). Every v0.1 MVP feature on our list is a run-level
event. There's a real chance the bulk of v0.1 can be built against the
official API without Harmony patching. Confirm next session by reading the
delegate files.

**Open questions** (to chase next session):
1. Why are mods Godot projects? Read `STS2FirstMod/README.md` and `project.godot`,
   work out what part of the export goes where.
2. What does the official `MegaCrit.Sts2.Core.Modding` API expose? Search the
   decompiled output for `Modding` namespace once decompile finishes.
3. What's the `[ModInitializer]` attribute lifecycle? Where does the game look
   for mods, and what does it call on them?
4. What's the mods folder location on Windows? Conventionally
   `%APPDATA%\Slay the Spire 2\mods\` or under the game install — confirm.
5. How do other read-listening mods (e.g. UndoAndRedo, BetterSpire2) wire into
   the input/decision pipeline? Study one of them as a working reference.

**Things explicitly not done yet** (deliberate scope discipline):
- ~~Reading any of the original mod's source — that's session 2's job.~~ Started in session 1.5 below.
- Mapping hook points in the decompiled DLL — needs the decompile to finish first.
- Writing any code at all — we're in research mode.

---

## 2026-05-07 — Session 1.5: original-mod feature inventory

Same day, continued straight on. Renamed workspace from `sts2-streamer` to
`slay-the-streamer-2` (PowerShell rename was racy, ended up nesting via Bash
`mv` quirk; cleaned up by re-flattening and re-initing git from scratch).
Made the initial commit `c86bb35`.

Then dove into option 3: original mod feature inventory.

**Read in full**:
- `SlayTheStreamer.java` (main entrypoint)
- `TwirkPatch.java` (UTF-8 encoding fix for Korean chat)
- `ConfigPanel.java` (commented-out UI — users edit JSON by hand)
- `CardRewardPatch.java` (decoration only — voting comes from base mod)
- `MonsterMessageRepeater.java` (chat speech bubbles, ~35 LOC of logic)
- `HexaghostModel.java` (display-only Hexaghost for boss-select backdrop)
- `StartGamePatch.java` (Neow voting + sealed deck construction)

**Not yet read** (~770 lines remaining):
- `BossSelectScreen.java` (345)
- `MonsterNamesPatch.java` (256)
- `NoSkipBossRelicPatch.java` (222)
- `BossChoicePatch.java` (145)
- `BossSelectRoom.java` (44)
- `ShopkeeperNamesPatch.java` (76)
- `MainMenuDisplayPatch.java` (39)

The unread ones either flesh out boss voting (definitely v0.1-relevant) or
implement chat-as-monster polish (post-MVP).

**Headline finding**: see `02-original-mod-feature-inventory.md` for the
full writeup. The crucial insight: most "chat votes on X" features came
from the underlying robojumper Twitch Integration mod, not Slay the Streamer
itself. Slay the Streamer is a *thin layer* (sealed deck + Neow + Act-boss
votes + chat-as-monster polish) on top of a generic chat-voting framework.

For our project: there is no Twitch Integration mod for StS2, so we'll
reimplement both layers ourselves. Strongly favours the monolithic
architecture (option B from session 1).

**Open scoping decisions** (deferred to design phase):
1. Sealed deck in v0.1, or pure-voting v0.1 with sealed deck for v0.2?
2. Act-boss voting in v0.1, or skip it (StS2's boss selection might be different
   enough that the original's BossSelectScreen approach doesn't transfer)?
3. Confirm: monolithic mod (recommended).

---

## 2026-05-07 — Session 1.6: StS2 modding API initial pass

Same day, kept going. Read all 13 files of
`MegaCrit.Sts2.Core.Modding/` plus the top of `Core/Hooks/Hook.cs` (the
file is 27k+ tokens — sampled).

**Headline corrections** (session 1's optimism was partly misplaced):
- `RunHookSubscriptionDelegate` is *not* an event-bus. It's a registration
  delegate that returns `IEnumerable<AbstractModel>` — i.e., it asks the
  mod to register models that participate in the run.
- The actual hook surface is `AbstractModel`'s virtual methods, called by
  the static `Hook` class via `runState.IterateHookListeners(...)`.
- We can also (or instead) use Harmony patches — game ships `0Harmony.dll`
  and auto-runs `PatchAll` on any mod assembly without `[ModInitializer]`.

**New things known**:
- Manifest schema is dead simple (id, name, author, description, version,
  has_pck, has_dll, dependencies, affects_gameplay).
- Mods folder is `<game-install>/mods/`, NOT `%APPDATA%`.
- Two clean entry-point styles: `[ModInitializer]` + manual setup, OR
  pure-Harmony (game auto-PatchAlls).
- For Slay the Streamer 2 we'll use `[ModInitializer]` (need IRC startup +
  manual Harmony lifecycle control).

**Single most valuable next task**: read `AbstractModel.cs` in
`Core/Models/`. Its virtual-method surface determines whether v0.1 is
"~20 AbstractModel overrides + IRC layer" or "~20 Harmony patches + IRC
layer". Both are feasible; the design diverges meaningfully.

Full writeup: `notes/03-sts2-modding-api.md`.

**Stopping for the day.** Substantial commits today: workspace + tooling
(`c86bb35`), original-mod feature inventory (`636c1aa`), StS2 modding API
notes (next commit). Three deep dives in one sitting is enough — fresh
session next time.

---

## 2026-05-07 — Session 1.7: AbstractModel + ModHelper + build pipeline

Same day, kept going (Surfinite had ~2 more hours and we're moving fast).

**Read**:
- `Core/Models/AbstractModel.cs` (1,038 lines, ~200 virtual methods —
  inventoried via grep, then targeted reads).
- `Core/Modding/ModHelper.cs` (full read, 147 lines — closes the
  registration question).
- `references/STS2FirstMod/`: `README.md`, `FirstMod.json`,
  `FirstMod.csproj`, `NewScript.cs`, `project.godot`, `build.sh`.

**Findings written up** in `notes/04-abstract-model-hook-surface.md` and
`notes/05-build-pipeline.md`.

**Headline conclusions**:
1. **AbstractModel can't substitute the player's choice.** Its 200 virtual
   methods cover content modification (cards, prices, damage, rewards) and
   observation (Before/After), but none give us "click X for the player".
   We need Harmony patches for our six v0.1 votes.
2. **Sealed deck is the one v0.1 feature that AbstractModel handles cleanly**
   — `ShouldAddToDeck` and `TryModifyCardBeingAddedToDeck` are made for it.
3. **Mod registration is via `ModHelper`** —
   `SubscribeForRunStateHooks(id, delegate)` and the combat equivalent.
4. **Build pipeline goes through Godot 4.5.1 Mono**, not vanilla `dotnet build`.
   The csproj uses `Godot.NET.Sdk/4.5.1` and Godot's headless mode does the
   compile + asset packaging. Output is `<id>.dll` + optional `<id>.pck` +
   `<id>.json` dropped in `<game-install>/mods/<id>/`.
5. **Godot 4.5.1 Mono needs to be installed** for the build pipeline. New
   prerequisite for next session.
6. **Runtime debugging works** — Godot's remote debug server attaches over
   TCP via `--remote-debug tcp://127.0.0.1:6007` launch flag. Big upgrade
   over StS1 modding.

**Genuinely at a clean wrap point now**:
- All four originally-scoped research items done (original mod, modding API,
  AbstractModel hooks, build pipeline).
- Concrete v0.1 architecture is starting to emerge:
  - Monolithic mod, `Godot.NET.Sdk`, `[ModInitializer("Initialize")]` entry
  - ~6 Harmony patches (one per vote)
  - Optional AbstractModel layer for sealed deck (if v0.1 keeps it)
  - IRC client + vote engine as separate internal services
- The next session is *design phase*, not more research. Brainstorming
  + writing-plans for v0.1.

**Outstanding small research tasks** (worth picking up at design time, not now):
- Identify specific Harmony patch targets for each of the six votes (search
  decompile for Neow class, card reward screen, shop confirm, etc.)
- Confirm `has_pck: false` works for code-only mods.
- Identify exact game log file location on Windows.
