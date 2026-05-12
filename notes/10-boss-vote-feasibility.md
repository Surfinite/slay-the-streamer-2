# Chat-picks-the-boss vote — feasibility findings

**Date**: 2026-05-12
**Status**: Research / feasibility findings. Not a spec, not a plan. Investigates whether vanilla StS2 lets us implement the original Tempus-mod feature where chat votes on the next act's boss right after the chest room, and what UI surface we'd need to build.
**Motivation**: Surfinite (2026-05-12) asked about porting Tempus's StS1 boss-vote feature: the chest room is always present mid-act regardless of route, so triggering a boss-pick vote on chest exit reaches every run, and the popup overlays the chest scene with the three candidate boss sprites side-by-side (visual reference: [original mod screenshot](https://raw.githubusercontent.com/Tempus/SlayTheStreamer/master/Screenshots/Screen%20Shot%202018-11-20%20at%208.14.03%20PM.png)). Surfinite flagged the underlying feasibility question: "I don't see a console command to change the act boss, so this might be a feasibility study."

## TL;DR

- **Feasibility: YES, cleanly.** Vanilla exposes [`MapCmd.SetBossEncounter(IRunState, EncounterModel)`](../decompiled/sts2/MegaCrit/sts2/Core/Commands/MapCmd.cs) — a public static one-liner that swaps the act's boss, refreshes the top-bar boss icon, and re-renders the map (with `clearDrawings: false` so the streamer's map drawings survive). Internally it calls `runState.Act.SetBossEncounter(boss)` ([`ActModel.cs:356`](../decompiled/sts2/MegaCrit/sts2/Core/Models/ActModel.cs#L356)), which is also public. There is no console command, but there doesn't need to be.
- **Trigger surface**: the chest room is [`TreasureRoom`](../decompiled/sts2/MegaCrit/sts2/Core/Rooms/TreasureRoom.cs) at the model layer + [`NTreasureRoom`](../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Rooms/NTreasureRoom.cs) at the node layer. Best trigger is a Harmony **prefix on `NTreasureRoom.OnProceedButtonPressed`** (matches the screenshot's "vote appears while chest scene is still visible" UX) using our existing B.1 suspend-and-resume pattern: prefix returns `false`, kicks off the vote async, then dispatches a resume click after the winner is set.
- **Boss sprite assets**: every `EncounterModel` exposes `MapNodeAssetPaths` ([EncounterModel.cs:154-168](../decompiled/sts2/MegaCrit/sts2/Core/Models/EncounterModel.cs#L154-L168)) — either a Spine skeleton resource at `res://animations/map/<id>/<id>_node_skel_data.tres`, or fallback to two PNGs (`.tres.png` + `.tres_outline.png`). These are the same icons the streamer already sees on the map, so using them for the vote popup gives free visual consistency. PNG path is simpler if we don't want to pull in MegaSpine bindings.
- **Backdrop/overlay**: vanilla's [`NOverlayStack`](../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/Overlays/NOverlayStack.cs) has a `_backstop` Control + `ShowBackstop`/`HideBackstop` methods that already implement the darken-everything-below pattern. Using it directly is one option. Cleaner alternative: instantiate our own `CanvasLayer` at a higher layer index than the room, add a semi-transparent `ColorRect` child, add our popup Control on top. This pattern is also what we use for `VoteTallyLabel` per [notes/06](06-followups-and-deferred.md) and avoids interacting with vanilla's screen-stack lifecycle.
- **Overall complexity estimate**: ~3-5 days of focused work for v1 (single-boss vote on standard runs). The vote-coordinator and chat-receipt infrastructure is reused verbatim from B.1 / B.2.1. The new code is mostly: one Harmony patch, one popup Control with 3 boss icons, one wiring call to `MapCmd.SetBossEncounter`. Risks are in the small details (A10 DoubleBoss interaction on Act 3, multiplayer sync) more than in the core path.
- **Ascension scope** (corrected 2026-05-12 after Surfinite pushed back): the chest room exists at every ascension — the `replaceTreasureWithElites` parameter is dead code in this build. The only ascension level that affects bosses is `DoubleBoss` (A10+), which adds a second boss to the final act only. See the "Ascension interactions" subsection.
- **Target user**: unlocked-everything streamers per Surfinite 2026-05-12. We don't need to engineer around `BossDiscoveryOrder` progression — the user has already seen every boss.

## The boss-swap surface

[`MapCmd.SetBossEncounter`](../decompiled/sts2/MegaCrit/sts2/Core/Commands/MapCmd.cs) is the entire API:

```csharp
public static void SetBossEncounter(IRunState runState, EncounterModel boss)
{
    runState.Act.SetBossEncounter(boss);              // mutates ActModel._rooms.Boss
    if (TestMode.IsOff)
    {
        NRun.Instance.GlobalUi.TopBar.BossIcon.RefreshBossIcon();
        NMapScreen.Instance?.SetMap(runState.Map, runState.Rng.Seed, clearDrawings: false);
    }
}
```

Three observable side effects:
1. `_rooms.Boss` is replaced (the source of truth for "what fight happens at the boss room").
2. Top-bar boss icon updates immediately ([`NTopBarBossIcon.RefreshBossIcon`](../decompiled/sts2/MegaCrit/sts2/Core/Nodes/TopBar/NTopBarBossIcon.cs) is already wired).
3. Map screen re-renders the boss node with the new icon, preserving streamer drawings.

We call this once after the chat vote closes; everything else is already wired in vanilla.

### What about the second boss?

Act 3 (`Glory`) has a second boss accessed via [`ActModel.SetSecondBossEncounter`](../decompiled/sts2/MegaCrit/sts2/Core/Models/ActModel.cs#L366) (also public). `RunManager.cs:500-502` shows it's assigned at act start by picking a random boss that's not the same as the primary. We have three options for v1:
- **A**: vote on first boss only; let vanilla pick second. Cheapest; the second boss is somewhat fixed by exclusion of the first.
- **B**: vote separately on second boss (would need a second chest in the act? — act 3 may have multiple chests, would need to verify).
- **C**: vote on the pair (top 1 + top 2 from a 4-or-5-option list). Cleanest player flow but bigger UI work.

Recommend A for v1, defer B/C as polish.

### Boss-pool source

[`ActModel.AllBossEncounters`](../decompiled/sts2/MegaCrit/sts2/Core/Models/ActModel.cs#L119) — `IEnumerable<EncounterModel>` derived from each act's `GenerateAllEncounters()`. Filtered to `e.RoomType == RoomType.Boss`. So `runState.Act.AllBossEncounters` gives us the full candidate set; we sample 3 for the vote.

**Important sampling consideration**: include the currently-set boss in the candidate list so chat can vote to keep it. Vanilla's discovery-order logic ([`ApplyDiscoveryOrderModifications`](../decompiled/sts2/MegaCrit/sts2/Core/Models/ActModel.cs#L294)) may have force-picked a specific unseen boss as the current one — keeping that as an option means chat can preserve the streamer's discovery progress if they want.

## Trigger candidates — what fires the vote

The chest is a discrete `TreasureRoom` in the room sequence; player enters, opens chest, picks relic (or skips), clicks Proceed → leaves to map. Trigger options, ordered by ascending lateness:

### Option A: `TreasureRoom.Exit` (postfix on the model)

[`TreasureRoom.Exit`](../decompiled/sts2/MegaCrit/sts2/Core/Rooms/TreasureRoom.cs#L47) fires when the model-layer room is exited. By this point the streamer is already transitioning to the map. Vote would have to either block the transition (awkward — would need to patch the map-load) or fire while map is loading (chat sees an empty screen during the vote).

Reject: too late, no good visual anchor.

### Option B: `NTreasureRoom.OnProceedButtonPressed` (prefix on the node)

[`NTreasureRoom`](../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Rooms/NTreasureRoom.cs) has an `OnProceedButtonPressed` method (named in the `MethodName` class at line 39). Prefix returning `false` blocks the original click handler, lets us kick off the vote with the chest scene still visible (matches the Tempus screenshot), then dispatches a synthetic Proceed click on vote close. This is the same suspend-and-resume pattern we use for B.1's `NeowBlessingVotePatch` and B.2.1's `CardRewardVotePatch` — proven viable.

Recommend: this is the right trigger.

Two sub-considerations:
- **Re-entrancy**: if the streamer clicks Proceed twice quickly, we need to suppress the second click while the vote is open. Same guard as B.2.1's `VoteInProgress` flag.
- **What if the chest hasn't been opened yet?** `_hasChestBeenOpened` is a private field on `NTreasureRoom` (line 78 in the PropertyName class). Vanilla probably disables the Proceed button until the chest is opened, but we should verify and not fire a vote if the streamer's just skipping the chest entirely. Even if Proceed is enabled pre-chest, it doesn't matter — the vote still fires once per chest screen, which is once per act.

### Option C: hook the chest-opened signal earlier

`OnChestButtonReleased` + `_isRelicCollectionOpen` would fire the vote during relic-pick. Probably too early — streamer's still mid-decision on the relic. Reject.

### Trigger conclusion

**Option B** is the recommendation. The vote opens when the streamer clicks Proceed, the chest scene stays visible underneath, the 30-second vote runs, the winner is selected via `MapCmd.SetBossEncounter`, and our synthesized resume click moves the streamer to the map with the new boss already in place.

## Boss sprite assets — what to render

Per [`EncounterModel.cs:138-168`](../decompiled/sts2/MegaCrit/sts2/Core/Models/EncounterModel.cs#L138-L168):

```csharp
public virtual string BossNodePath =>
    $"res://animations/map/{base.Id.Entry.ToLowerInvariant()}/{base.Id.Entry.ToLowerInvariant()}_node_skel_data.tres";

public IEnumerable<string> MapNodeAssetPaths
{
    get
    {
        if (BossNodeSpineResource != null)
            return new ReadOnlySingleElementList<string>(BossNodePath);
        return new ReadOnlyArray<string>(new string[2]
        {
            BossNodePath + ".png",
            BossNodePath + "_outline.png"
        });
    }
}
```

Two render paths:
- **Spine animation** if the boss has a `_node_skel_data.tres` Spine resource. Higher production value (the icon animates on the map). Costs: pulling in `MegaSpine` binding usage; instantiating Spine players in our popup. Worth it for a polished feel, but added implementation surface.
- **PNG fallback**: load `<BossNodePath>.png` (filled icon) and optionally `<BossNodePath>_outline.png` (outline) as `Texture2D`, drop into a `TextureRect`. Trivial to render. Lower visual fidelity but matches what unseen bosses look like on the map (silhouette).

Recommend PNG fallback for v1. Spine animation is polish.

**Title text**: `EncounterModel.Title` ([line 152](../decompiled/sts2/MegaCrit/sts2/Core/Models/EncounterModel.cs#L152)) returns a `LocString` of `encounters.<id>.title`. Display the formatted text under each portrait so chat knows what they're voting for. **Privacy / spoiler concern**: by displaying titles, we leak which bosses exist before the streamer has discovered them. Standard-mode streamers playing for first-discovery may not want this; document a `showBossNames: false` toggle as a future settings knob (silhouettes-only mode).

## Modal/popup architecture — how to draw on top

Three architectural choices, ranked by isolation from vanilla state:

### Option 1: piggyback on `NOverlayStack._backstop` + push an overlay screen

`NOverlayStack` has [`ShowBackstop`/`HideBackstop`](../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/Overlays/NOverlayStack.cs) and a private `_backstop: Control` ([line 57](../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/Overlays/NOverlayStack.cs#L57)) that already darkens-everything-below. We'd:
- Implement an `IOverlayScreen` (or duplicate one of vanilla's existing overlays) as our boss-vote popup.
- Push it onto the stack; vanilla fades in the backstop automatically.

Pros: uses vanilla's pattern; backstop fade is already polished; pause behavior is already correct.
Cons: tightly coupled to vanilla's overlay-stack contract; `IOverlayScreen` interface terms we'd need to satisfy (lifecycle, focus management, dismissal). Risk of interfering with vanilla overlays.

### Option 2: instantiate our own `CanvasLayer` + `ColorRect` backdrop + popup Control

Build the entire visual stack ourselves:
- New `CanvasLayer` added to `SceneTree.Root` at a high layer-index (e.g., 100, above the normal game UI layer).
- Add a `ColorRect` child filling the screen with `Color(0, 0, 0, 0.6)` (semi-transparent black) — this is the darkened backdrop.
- Add our popup Control on top: three boss-portrait columns (TextureRect + title label + vote-tally label per column) + an overall timer label.
- On vote close: free the CanvasLayer.

Pros: zero interaction with vanilla overlay state; clean lifecycle (we own everything we created); same pattern as `VoteTallyLabel` per [notes/06 B.1 architecture-defining outcome](06-followups-and-deferred.md). Easy to verify "doesn't break other vanilla overlays" because we never touch them.
Cons: we don't get vanilla's backstop-fade animation for free; have to tween the `ColorRect.modulate` ourselves if we want it smooth. Trivial.

Recommend Option 2 for v1. Lower risk, easier to ship.

### Option 3: `NModalContainer.Instance.Add(...)` for a true modal popup

`NModalContainer` ([referenced in NSettingsScreen.cs:569](../decompiled/sts2/MegaCrit/sts2/Core/Nodes/CommonUi/NModalContainer.cs)) is the modal-popup container used for error popups etc. We'd build a modal Control matching the container's expected child shape, add it, dismiss on vote close.

Pros: vanilla's modal lifecycle (input-blocking, focus, dismissal).
Cons: similar coupling concerns to Option 1; modals are usually short text + button affairs, not three-column image+vote popups. Probably wrong shape for what we want.

Reject.

## Implementation sketch (surface targets, not code)

For when the v0.2 spec gets drafted, the patch list should look like:

1. **`BossVotePatch.cs`** (new Harmony patch class).
   - `NTreasureRoom_OnProceedButtonPressed_Prefix`: re-entrancy guard, fetch `runState.Act.AllBossEncounters`, sample 3 distinct bosses (include current boss in sample), `coordinator.Start("Act N Boss", labels, 30s)`, dispatch async handler, return false to suspend the original click.
   - `HandleVoteAsync`: await winner via the same TCS-resume pattern as `NeowBlessingVotePatch`. On winner: `MapCmd.SetBossEncounter(runState, winnerBoss)`, then synthesize the Proceed click via `dispatcher.Post`.
2. **`BossVotePopup.cs`** (new Control class).
   - `Show(IReadOnlyList<EncounterModel> options, VoteSession session)` instantiates the CanvasLayer + ColorRect + per-option columns.
   - Subscribes to `session.TallyChanged` to update per-column tally labels live.
   - Free on `session.Closed`.
3. **Settings additions** (per [notes/09](09-settings-and-tunable-knobs.md)): `voteOnBoss: bool` toggle. Optional: `showBossNames: bool` (silhouettes-only mode for spoiler-averse streamers).
4. **Receipt strings** (per `EnglishReceipts.cs`): boss vote open / close. Tally cadence reuses `VoteReceiptPolicy.Default`.
5. **No vanilla-screen-state writes other than `MapCmd.SetBossEncounter`** — keeps the blast radius small.

Patch count: 1 new patch class with 1 Harmony target; 1 new UI class; small settings + receipt additions. Significantly smaller than B.2.1 (which had 5 patch targets and 8 patches).

## Caveats and gotchas

### Achievement / "first defeated" tracking

If the vote happens in Standard Mode and the streamer beats the chat-picked boss, the "first defeat" achievement (e.g., `DefeatOvergrowthEnemies`) should still fire per [`ActModel.DefeatedAllEnemiesAchievement`](../decompiled/sts2/MegaCrit/sts2/Core/Models/ActModel.cs#L123). Need to verify the chain doesn't depend on the boss having been originally rolled (vs reassigned via `SetBossEncounter`). Probably fine since the achievement key is per-act not per-encounter, but worth a smoke test.

### Discovery progression for unseen bosses

**Non-issue per Surfinite 2026-05-12.** `ActModel.ApplyDiscoveryOrderModifications` ([line 294](../decompiled/sts2/MegaCrit/sts2/Core/Models/ActModel.cs#L294)) force-picks unseen bosses, but the target audience is streamers who already have everything unlocked. The "chat always picks the same boss → undiscovered bosses stay undiscovered" concern doesn't apply to the actual user. Document the assumption ("designed for unlocked-everything saves") in the spec when it lands; don't engineer around progression.

### Multiplayer sync

`TreasureChestOpenedMessage` exists in [`MegaCrit.Sts2.Core.Multiplayer.Messages.Game.TreasureChestOpenedMessage`](../decompiled/sts2/MegaCrit/sts2/Core/Multiplayer/Messages/Game/TreasureChestOpenedMessage.cs), so chest events are network-synced. `MapCmd.SetBossEncounter` mutates `runState.Act` on the local instance; it does NOT appear to sync to remote peers. For our singleplayer-only v1 (per `notes/06 v0.2+`: multiplayer is deferred), this doesn't matter. For multiplayer, we'd need a new sync message — flag for whenever multiplayer support comes around.

### Ascension interactions (corrected 2026-05-12)

Re-checked the AscensionLevel enum and live call sites after Surfinite pushed back on the original "chests become elites at high ascension" caveat:

- **`AscensionLevel` enum** ([file](../decompiled/sts2/MegaCrit/sts2/Core/Entities/Ascension/AscensionLevel.cs)) has 11 entries: `None, SwarmingElites, WearyTraveler, Poverty, TightBelt, AscendersBane, Inflation, Scarcity, ToughEnemies, DeadlyEnemies, DoubleBoss`.
- **`DoubleBoss` is the only ascension level that interacts with the boss system** ([`RunManager.cs:499-502`](../decompiled/sts2/MegaCrit/sts2/Core/Runs/RunManager.cs#L499-L502)): `if (i == State.Acts.Count - 1 && AscensionManager.HasLevel(AscensionLevel.DoubleBoss)) { … SetSecondBossEncounter(…) }`. Effect: at A10+ AND only on the FINAL act, a second boss is added (picked at run start from `AllBossEncounters` excluding the primary).
- **`replaceTreasureWithElites` is dead code in this beta**. The only live call site at [`RunManager.cs:549`](../decompiled/sts2/MegaCrit/sts2/Core/Runs/RunManager.cs#L549) hardcodes `false`. The parameter exists in `ActModel.CreateMap` and `StandardActMap.CreateFor` but no path activates it. Either it's reserved for a future ascension level, vestigial from StS1 (where high ascension did replace chests with elites), or only used in test code. **Practical implication: the chest room always exists regardless of ascension level, so our trigger always fires.** The earlier "high-ascension chest replacement" caveat was based on this dead parameter and is withdrawn.

### Second-boss handling under DoubleBoss (A10+)

`Glory` (Act 3) has a `SecondBoss` per [`ActModel.HasSecondBoss`](../decompiled/sts2/MegaCrit/sts2/Core/Models/ActModel.cs#L164) — but ONLY when `AscensionManager.HasLevel(AscensionLevel.DoubleBoss)` is true (A10+). On runs below A10, no act ever has a second boss, and `HasSecondBoss == false`. So:

- **Most runs (A0-A9)**: only one boss per act, single vote per chest, no second-boss complexity.
- **A10+ runs on the final act**: two bosses fought in sequence. Surfinite resolved 2026-05-12: vote on primary boss only, let vanilla pick second from the remainder. Defer separate / paired-vote to v0.2 polish.

### Timing: when does the boss become "locked in"?

`runState.Act._rooms.Boss` is read when the player enters the boss room and combat starts. `SetBossEncounter` mutates this field. As long as we set the boss BEFORE the player walks through any map-screen interaction that loads the boss room scene, we're safe. Chest → vote → set boss → map → boss room is the happy-path timing, no race.

What if the streamer walks back from the map to the chest somehow (similar to the v0.2 polish item in [notes/06](06-followups-and-deferred.md) about the map-screen back-arrow)? Need to verify the back-arrow does NOT exist after a chest exit (it's specifically a rewards-screen feature per notes/06). If it does, we'd have similar concerns about re-firing the vote — but probably out of scope to worry about.

## Open questions

1. **Should the streamer get a "no vote" / "keep current boss" option?** Tempus's original mod (per the screenshot) appears to show all three options as choices, no explicit "keep current" — chat could just vote for whatever boss is currently set. Easier to implement (one of the three sample options is always the current boss).
2. **Should the vote happen in Act 1 too?** Act 1 boss is usually the most weight-bearing for early-run difficulty. Tempus's mod did vote on every act. Worth confirming Surfinite's preference: all acts, or skip Act 1?
3. **Boss-vote receipt wording**: `Act 2 boss vote opened. Vote for #0 Gremlin Nob, #1 Sentries, #2 Lagavulin` — matches existing receipt style from Neow + Card Reward patches. Anything specific to want?
4. **Cancellation**: if the streamer abandons the run mid-vote, same cancellation-receipt path as B.2.1 (verified working there). Should be free.
5. **Vote-window duration for boss vote**: 30s like Neow / card reward? Or longer since the stakes are higher? Per [notes/09 B.3](09-settings-and-tunable-knobs.md), the duration knob is hardcoded — consider exposing a `bossVoteDurationSeconds` override or just reusing the global default.
6. **Visual layout**: three columns horizontally is what the screenshot shows. Vanilla's screen is 1920×1080-aspect normalized; three columns at ~30% width each with 5% gutters works fine. Need design pass on portrait dimensions, font sizes, tally-label placement per column.
7. **What if `AllBossEncounters` has fewer than 3 entries?** Each vanilla act has 3 boss encounters per `BossDiscoveryOrder` patterns (and act3 has more for unlocks). Need to verify there's always ≥ 3. If not, the vote should show however many exist (2-pick still works). Probably a 2-pick is sufficient under any vanilla act.
8. **Boss icons in modded mode where epochs are locked**: if a boss is tied to an epoch the streamer hasn't unlocked, does `AllBossEncounters` filter it out? Per [`GetUnlockedAncients`](../decompiled/sts2/MegaCrit/sts2/Core/Models/ActModel.cs#L197) we know ancients are unlock-filtered. But bosses don't appear to be — they're in the act's encounter pool unconditionally. Worth verifying with a smoke run on a fresh modded profile; if `AllBossEncounters` returns all bosses regardless of progress, chat could pick a boss the streamer's account hasn't "unlocked" — probably fine (modded mode locks epochs anyway) but worth confirming the unlock state doesn't gate the encounter render.

## Cross-references

- [notes/06-followups-and-deferred.md](06-followups-and-deferred.md) — v0.2+ section + B.2.1 architecture-defining outcomes (suspend-and-resume pattern reused here).
- [notes/09-settings-and-tunable-knobs.md](09-settings-and-tunable-knobs.md) — `voteOnBoss` toggle entry (currently 🔵 planned).
- [`MapCmd.SetBossEncounter`](../decompiled/sts2/MegaCrit/sts2/Core/Commands/MapCmd.cs) — the entire boss-swap API.
- [`ActModel.cs`](../decompiled/sts2/MegaCrit/sts2/Core/Models/ActModel.cs) — boss encounter generation, second-boss handling, discovery order.
- [`EncounterModel.cs`](../decompiled/sts2/MegaCrit/sts2/Core/Models/EncounterModel.cs) — `MapNodeAssetPaths`, `BossNodeSpineResource`, `Title`.
- [`TreasureRoom.cs`](../decompiled/sts2/MegaCrit/sts2/Core/Rooms/TreasureRoom.cs) + [`NTreasureRoom.cs`](../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Rooms/NTreasureRoom.cs) — trigger surface.
- [`NOverlayStack.cs`](../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/Overlays/NOverlayStack.cs) — alternative overlay/backdrop path (not recommended for v1).
- Original Tempus mod screenshot: [github.com/Tempus/SlayTheStreamer](https://raw.githubusercontent.com/Tempus/SlayTheStreamer/master/Screenshots/Screen%20Shot%202018-11-20%20at%208.14.03%20PM.png) — visual reference for the three-boss popup layout.
