# StS2 modding API — initial pass

Read in session 1.5: `decompiled/sts2/MegaCrit/sts2/Core/Modding/` (whole namespace,
13 files) plus partial reads of `decompiled/sts2/MegaCrit/sts2/Core/Hooks/Hook.cs`.

## Summary in one paragraph

Mods are .NET 9 assemblies (with optional Godot PCK files for assets) dropped
into `<game-install>/mods/`, accompanied by a small JSON manifest. The game
auto-loads them, sorts by manifest dependencies, and either calls a
mod-declared `[ModInitializer("Init")]` static method or auto-runs
`Harmony.PatchAll(assembly)`. Two distinct ways to extend the game are
exposed: **(a) Harmony patches** for direct method interception, and **(b)
`AbstractModel`-based hook participation** for cleaner, registered
"participants" that receive callbacks via an internal hook system. Slay the
Streamer 2's voting features will likely use a mix of both, leaning on
`AbstractModel` where possible.

## Mod loading flow

From `ModManager.Initialize` (the `Core/Modding/ModManager.cs` static class):

1. Game finds `<game-install>/mods/` directory recursively.
2. Game also reads Steam Workshop subscribed mods.
3. For each `.json` file found: deserialize as `ModManifest`. Reject if no
   `id` field. Otherwise create a `Mod` record.
4. Sort all mods by manifest `dependencies` (topological sort, stable on
   user-supplied order). Detect circular deps and mark those mods as failed.
5. For each mod in dependency order, `TryLoadMod`:
   - If user has disabled it in settings, skip.
   - If user hasn't agreed to mod loading (one-time nag), skip all.
   - If duplicate ID already loaded, fail.
   - If `has_dll`: `AssemblyLoadContext.LoadFromAssemblyPath(<modid>.dll)`.
   - If `has_pck`: `ProjectSettings.LoadResourcePack(<modid>.pck)`.
   - For the loaded assembly:
     - Scan all types for `[ModInitializerAttribute]`.
     - **If found**: call the named static method (one per type, must be
       `static`).
     - **If NOT found**: automatically call `Harmony.PatchAll(assembly)` —
       so a "pure-Harmony" mod doesn't need any explicit init code.
6. Game can also detect new Steam Workshop mods at runtime, but they won't
   be hot-loaded — restart required.

**Critical implication**: we have two clean entry-point styles:

```csharp
// Style A: Init method, do anything we want
[ModInitializer("Initialize")]
public static class StreamerMod {
    public static void Initialize() {
        // Set up IRC, register Harmony patches manually,
        // subscribe to ModManager events, etc.
    }
}

// Style B: Just decorate Harmony patch classes; no init code at all
[HarmonyPatch(typeof(NeowEvent), nameof(NeowEvent.SomeMethod))]
public class NeowVotePatch {
    static void Prefix(...) { ... }
}
// game auto-runs PatchAll on our assembly.
```

For Slay the Streamer 2, **Style A** is needed — we have to start the IRC
client and a vote engine, which is non-trivial setup. We'll likely also
register Harmony patches manually to control timing.

## ModManifest schema

From `ModManifest.cs`:

```json
{
  "id":               "string (REQUIRED, unique)",
  "name":             "string",
  "author":           "string",
  "description":      "string",
  "version":          "string",
  "has_pck":          false,
  "has_dll":          false,
  "dependencies":     ["mod_id_1", "mod_id_2"],
  "affects_gameplay": true
}
```

- `id` is the only required field. The game resolves `<id>.dll` and
  `<id>.pck` filenames within the mod folder.
- `affects_gameplay` (defaults to `true`) — mods with this true taint runs
  for leaderboard/competitive purposes. **Slay the Streamer 2 will set this
  to `true`** (we *definitely* affect gameplay).
- `dependencies` is a list of *other mod IDs*. We have no dependencies for
  v0.1 (monolithic).
- The example manifest in `references/STS2FirstMod/FirstMod.json` would
  show concrete values — read that next session.

## The two hook systems

Two distinct things are loosely called "hooks":

### 1. Modding-namespace delegates (`Core/Modding/`)

```csharp
public delegate IEnumerable<AbstractModel> RunHookSubscriptionDelegate(RunState runState);
public delegate IEnumerable<AbstractModel> CombatHookSubscriptionDelegate(CombatState combatState);
```

These are **registration delegates**: a mod gives the game a function that,
when called with the current state, returns a list of `AbstractModel`s the
mod wants to participate in this run/combat. Those models then receive
callbacks through the internal hook system.

We haven't yet read where these are *registered* (probably via `ModManager`
or a runtime helper) — open question for next session.

### 2. The internal hook system (`Core/Hooks/Hook.cs`)

`Hook` is a static class with methods like:

```csharp
public static async Task AfterActEntered(IRunState runState);
public static async Task BeforeAttack(ICombatState, AttackCommand);
public static async Task AfterAttack(ICombatState, PlayerChoiceContext, AttackCommand);
public static async Task AfterBlockBroken(ICombatState, Creature);
public static async Task AfterBlockGained(ICombatState, Creature, decimal, ValueProp, CardModel?);
public static async Task AfterCardChangedPiles(IRunState, ICombatState?, CardModel, PileType, AbstractModel?);
public static async Task AfterCardDiscarded(ICombatState, PlayerChoiceContext, CardModel);
// ... many more (the file is 27k+ tokens)
```

Each method iterates `runState.IterateHookListeners(combatState)` and calls
the matching method on each `AbstractModel`. So our `AbstractModel`
subclasses override the relevant virtual methods to receive callbacks.

This is the same machinery the game's *built-in* relics, powers, and cards
use. It's not a mod-only API — it's the canonical extension mechanism.

**Open questions** (must answer before v0.1 design):

1. **What virtual methods does `AbstractModel` expose?** Specifically: are
   there hooks for `Neow blessing chosen`, `card reward presented`, `map
   path chosen`, `event option chosen`, `shop item bought`, `boss reward
   chosen`? Need to read `AbstractModel.cs` (probably in `Core/Models/`) and
   match its surface against our v0.1 votes.
2. **Can `AbstractModel` *override* the player's choice, or only observe?**
   The voting use case requires us to *substitute* chat's vote for the
   player's input — does the hook system allow that, or are hooks observation-only?
3. **What's `IterateHookListeners`'s contract?** Does it call the hook methods
   in registration order? Synchronously? Can a hook short-circuit later ones?

## Other findings from `ModManager.cs`

- **Mod folder**: `Path.Combine(Path.GetDirectoryName(OS.GetExecutablePath()), "mods")` —
  i.e., `<game-install>/mods/`, NOT `%APPDATA%`. (Updates open question 4 in
  session 1's log.)
- **`ModManager.OnModDetected`** event — fires when a mod is detected
  (loaded or not). We could use it to display load errors, etc.
- **`ModManager.OnMetricsUpload`** event — fires when game metrics are
  uploaded. Mods can subscribe; not relevant for v0.1.
- **`HasHarmonyPatches()`** — exposed publicly, might be used by the game
  to decide whether a run is "modded" beyond just `affects_gameplay` flag.
- **`HandleAssemblyResolveFailure`** specifically handles `sts2,*` and
  `0Harmony,*` resolve failures, redirecting them to the executing
  assembly. Good — means we can `using HarmonyLib;` from our mod without
  shipping a copy of Harmony with us.

## Implications for our v0.1

Old mental model (session 1):
> "RunHookSubscriptionDelegate gives us run-level events for free, no
> Harmony needed."

Corrected mental model (now):
> "`RunHookSubscriptionDelegate` is the mechanism to *register* models per
> run. The actual extension surface is `AbstractModel`'s virtual methods.
> If AbstractModel exposes the choice points we need, we use models. If
> not, we fall back to Harmony patches. Either way, both are first-class."

Either path is feasible. **The single most valuable next research task** is:

- Read `AbstractModel.cs` (find it under `decompiled/sts2/MegaCrit/sts2/Core/Models/`).
- Inventory its virtual methods.
- Match them against our six v0.1 votes (Neow, card, event, shop, map, boss).
- Determine whether they allow choice-substitution or only observation.

That answers whether v0.1 is "20 AbstractModel overrides + IRC" or
"20 Harmony patches + IRC", and the design diverges significantly between
the two paths.

## Updates to session 1's open questions

| # | Question | Status |
|---|---|---|
| 1 | Why are mods Godot projects? | **Now partially answered** — `has_pck` lets mods ship Godot resource packs (assets/scenes). C#-only mods don't need the Godot project structure. STS2FirstMod uses it because it ships custom assets. We may not need it for v0.1 if we go UI-light. |
| 2 | What does the official `Modding` API expose? | **Answered** — manifest loading, DLL/PCK loading, `[ModInitializer]` lifecycle, two hook-subscription delegates that return `AbstractModel`s. |
| 3 | What's the `[ModInitializer]` lifecycle? | **Answered** — game calls the named static method exactly once during mod load. If absent, game runs `Harmony.PatchAll`. |
| 4 | Mods folder location? | **Answered** — `<game-install>/mods/` (not AppData). |
| 5 | How do other behaviour mods wire in? | Still open — would still be informative to look at UndoAndRedo or BetterSpire2 source if they're public. |
