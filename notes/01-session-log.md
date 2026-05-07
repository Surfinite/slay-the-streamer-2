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
- Reading any of the original mod's source — that's session 2's job.
- Mapping hook points in the decompiled DLL — needs the decompile to finish first.
- Writing any code at all — we're in research mode.
