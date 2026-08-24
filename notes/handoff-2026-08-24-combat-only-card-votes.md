# HANDOFF — combat-only card-reward votes (from the SabotageTheStreamer scope/ slice)

2026-08-24. For a fresh session in THIS workspace (slay-the-streamer-2).
Feature ask (Surfinite): **chat only gets to vote on card rewards that are the
result of a combat encounter** — relic-obtain rewards (Orrery/Kaleidoscope/
GlassEye/Lost Coffer class), pure event rewards (The Future of Potions, Trial,
Brain Leech, Colorful Philosophers, Crystal Sphere), Dream Catcher rest-site
rewards, and the Draft modifier become free streamer picks. Gated: normal
Monster/Elite/Boss rewards, ? -room fights, event fights (Punch-Off etc.),
and combat-earned extras (The Hunt, Prayer Wheel, White Star).

This EXACT feature was designed, built, reviewed, and rig-proven in
SabotageTheStreamer on 2026-08-24 (the sister tournament mod, slice `scope/`,
commits scope/0..scope/8). The game-side research transfers 1:1 — same game,
same engine types. The mod-side integration differs (this mod gates a CHAT
VOTE at the UI screen, not a co-op proposal round at the model layer, and is
single-client so none of the multiplayer symmetry machinery is needed).

Authoritative sources in the sister repo (read them before implementing;
do NOT redo the decompile sweeps):
- `c:\Users\Surfinite\SabotageTheStreamer\.superpowers\sdd-post\research-encounter-scope.md`
  — ALL 24 `new CardReward(` construction sites classified combat/non-combat,
  event-fight mechanics traced end-to-end, edge cases enumerated.
- `c:\Users\Surfinite\SabotageTheStreamer\docs\superpowers\specs\2026-08-24-scope-encounter-authority-design.md`
  — the design; §2 "Research ground truth" and §3.2 (tagging) transfer verbatim.
- `c:\Users\Surfinite\SabotageTheStreamer\src\Game\CardSwap\CombatOriginTags.cs`
  — the portable tagging class (~60 lines; port with namespace/log changes only).
- `c:\Users\Surfinite\SabotageTheStreamer\notes\57-scope-matrix.md`
  — the rig matrix that proved it live (both modes, zero warns, zero divergence).

## 1. The one sentence that makes this cheap

**A `CardReward` is combat-origin ⟺ its `RewardsSet.Room is CombatRoom`** —
and the single place every combat reward set passes through, after generation
fully completes, is **`Hook.BeforeCombatRewardOffered`** (one call site in the
whole game: `CombatRoom.OfferRoomEndRewards`, CombatRoom.cs:247). Tag the
`CardReward` instances there with a `ConditionalWeakTable` allowlist; at
vote-trigger time, one predicate: config-on AND untagged ⇒ skip the vote,
streamer picks freely.

## 2. Game-side ground truth (verified on game Beta ~0.110.x / build 2026-08;
   re-verify line numbers against THIS repo's decompiled/ tree — greps below)

- `RewardsSet.Room` is written ONLY by `WithRewardsFromRoom`/`EmptyForRoom`
  (RewardsSet.cs:58-81); every bespoke grant goes through
  `RewardsCmd.OfferCustom` → `Room == null` (RewardsCmd.cs:30-33). Vanilla
  itself branches on `Room is CombatRoom` (RewardsSet.cs:76, :126).
- `CombatRoom` is the ONLY deck-fight room class (there is no MonsterRoom
  class; RoomType lives on the encounter). ? -rooms resolve to plain
  CombatRoom (RunManager.cs:758-783).
- **Event fights are free**: every fight-capable event (PunchOff, TheLanternKey,
  FakeMerchant, DenseVegetation) calls `EventModel.EnterCombatWithoutExitingEvent`
  (EventModel.cs:454-475) which pushes a REAL CombatRoom and uses the normal
  room-end reward generation. Their card rewards are combat-origin by
  construction — zero special-casing. BattlewornDummy fights set
  `ShouldGiveRewards=false` and never grant a CardReward.
- `Hook.BeforeCombatRewardOffered(RewardsSet, IRunState, CombatRoom)`
  (Hook.cs:243) is public static async with EXACTLY ONE call site
  (CombatRoom.cs:247), fired once per player's set AFTER `GenerateForRoomEnd`
  completes — so Prayer Wheel/White Star hook-injected extra card rewards,
  The Hunt's `AddExtraReward` reward, and the ctor-B tutorial rewards are all
  ALREADY IN the list when the prefix runs. `RewardsCmd.OfferForRoomEnd` (the
  only bypass route) has ZERO callers. Async ⇒ state machine ⇒ the patch
  cannot be deadened by JIT inlining.
- The save-resume path (`StartPreFinishedCombat` → `OfferRoomEndRewards`,
  CombatRoom.cs:227-229) re-generates and therefore RE-TAGS — resumed combat
  picks stay gated with no extra work. (Relevant here since this mod's runs
  are SP and CAN resume.)
- `CardReward.Reroll()` reuses the SAME CardReward instance
  (CardReward.cs:294-304) — a per-instance tag survives rerolls. You already
  patch Reroll (CardRewardVotePatch.cs:774), so this matters and works.
- **Forbidden discriminators** (each misclassifies — do not use):
  - `CardCreationFlags.IsFromCombat`: MISSING from tutorial ctor-B rewards,
    Prayer Wheel/White Star injections, and The Hunt extras (all combat-origin).
  - `CardCreationSource.Encounter`: Dream Catcher's rest-site reward carries it
    (non-combat), as does the tutorial path.
  - "Recently finished a combat" heuristics: several relic obtains fire
    immediately post-victory (nested elite/boss relic-reward obtains).
- **Draft modifier** (Draft.cs:19-36): drives `CardReward.SelectUnsynchronized`
  directly, NO RewardsSet at all → naturally untagged → streamer-free under the
  toggle. It shows `NCardRewardSelectionScreen` 10× back-to-back (CanSkip=false),
  so your screen-level patches DO fire for it — the tag check handles it.

Re-verification greps (run against this repo's decompiled/ after any game
update; these are also the compat-watchlist entries to add):
```
grep -rn "Room is CombatRoom" decompiled/.../RewardsSet.cs        # discriminator alive
grep -rn "BeforeCombatRewardOffered" decompiled/                  # exactly: Hook.cs def + CombatRoom.cs:247 call + AbstractModel/LastingCandy overrides
grep -rn "OfferForRoomEnd" decompiled/                            # definition only, still ZERO callers
grep -rn "EnterCombatWithoutExitingEvent" decompiled/             # still the sole event-fight door
```

## 3. What does NOT transfer (this mod is simpler)

SabotageTheStreamer is a 2-client co-op mod, so its version needed a
Player-authoritative launch-announce message so both clients gate identically,
plus fail-safe rules for unannounced/rejoin states. **None of that applies
here**: single client, the vote machinery is local. Read the config directly
at gate time. That also means the checkbox can legally flip MID-RUN — with a
direct read, each reward screen evaluates the current value, which is
probably the behavior you want (decide and note it).

Also not needed: the model-layer `Reward.SelectUnsynchronized` gate (that is
the co-op proposal machinery's choke point). Your gate lives where your vote
trigger lives.

## 4. Implementation sketch (research the donor-side seams, then ~3 small tasks)

### 4a. Port `CombatOriginTags` (near-verbatim)

Copy `src/Game/CardSwap/CombatOriginTags.cs` from the sister repo: a static
class holding `ConditionalWeakTable<CardReward, object>` + a
`[HarmonyPrefix]` on `Hook.BeforeCombatRewardOffered(RewardsSet rewards, ...)`
that tags every `CardReward` in `rewards.Rewards`. Adjustments:
- Namespace + this mod's logger instead of `ModLog`.
- The sister version gates on its `RunGate.IsActive` (tournament-mode latch);
  here, gate on whatever "mod active" predicate this mod's other patches use
  (or tag unconditionally — the tag is inert data, only the vote gate reads it).
- Tag UNCONDITIONALLY of the checkbox — keeps the toggle a pure read-side
  switch and means flipping mid-run works retroactively for already-offered sets.
- Register the new patch target in this mod's patch-verification inventory
  (whatever the donor's PatchVerify equivalent is; the sister repo added it to
  `PatchVerify.Expected` — same discipline applies, this is a load-bearing
  non-resilient target).

### 4b. The gate — RESEARCH ITEM: how does CardRewardVotePatch reach the CardReward?

The vote seam here is `NCardRewardSelectionScreen.SelectCard` /
`OnAlternateRewardSelected` (CardRewardVotePatch.cs:26/:731) — a UI-level
gate, not a model-level one. The tag lives on the `CardReward` INSTANCE, so
the gate needs that instance at screen time. Facts that make this tractable:
- `NCardRewardSelectionScreen.ShowScreen` has EXACTLY ONE caller in the whole
  game: `CardReward.OnSelect` (CardReward.cs:165) — every screen instance
  corresponds 1:1 to a live CardReward.
- You already patch `CardReward.Reroll` (:774) — if that patch (or any other)
  already captures the active CardReward instance, reuse that capture. If not,
  the cheapest new capture is a prefix/postfix on `CardReward.OnSelect` (or on
  `ShowScreen` taking the reward via its caller state) storing a static
  "reward currently on screen" reference, cleared when the screen closes —
  research which fits this codebase's idiom.
- Then the gate is one predicate at vote-trigger time:
  `if (settings.CombatCardVotesOnly && !CombatOriginTags.IsTagged(activeReward))
   → do not open a chat vote; let the streamer click through vanilla.`
- FAIL-SAFE DIRECTION: unknown/untagged ⇒ NO vote ⇒ streamer picks freely.
  An unreachable vote can never strand the run. (This principle drove the
  sister design; keep it.)
- Check the OTHER card-vote-adjacent patches for the same gate:
  `CardRewardSkipGatePatch`, the skip/alternate handling, and anything that
  counts pending votes — every surface that assumes "card reward ⇒ vote" needs
  the same predicate or a shared helper (`ShouldVoteOnReward(CardReward)`).

### 4c. Config: in-game checkbox + settings key

- Key on the ModConfig record (src/Game/Bootstrap/ModSettings.cs — same
  record + parse-block + template-triple pattern as the sister repo; this
  repo has the identical additive-key discipline): suggested
  `bool CombatCardVotesOnly = false`.
  **DEFAULT FALSE = current shipped behavior (chat votes on every card-reward
  screen).** Note this is the OPPOSITE default from SabotageTheStreamer
  (tournament mod defaults to combat-only); Surfinite ruled each mod defaults
  to its own status quo.
- In-game checkbox via `SettingsPanelBuilder`/`SettingsPanelPatch`
  (src/Game/Ui/Settings/) alongside the existing bools (VoteOnActVariant is
  the closest sibling to copy). Label per Surfinite:
  **"Chat only votes on card-rewards from combat"**.
- Additive settings-key rules: ModConfig + template + Load in step, NO
  schemaVersion bump (same rule as the sister repo's CLAUDE.md).

## 5. Behavior table under the checkbox (streamer-facing truth)

| Path | Checkbox OFF (default, today) | Checkbox ON |
|---|---|---|
| Monster/Elite/Boss combat rewards (map, ?, event fights incl. Punch-Off) | chat votes | chat votes |
| Prayer Wheel / White Star extra combat rewards; The Hunt bonus | chat votes | chat votes |
| Relic obtains: Orrery, Kaleidoscope, Glass Eye, Lost Coffer (any source incl. Neow) | chat votes | **streamer free** |
| Pure events: Future of Potions, Trial, Brain Leech, Colorful Philosophers, Crystal Sphere | chat votes | **streamer free** |
| Dream Catcher rest-site reward | chat votes | **streamer free** |
| Draft modifier (10 picks) | chat votes | **streamer free** |
| Hefty Tablet / Dolly's Mirror class (NChooseACardScreen family) | never voted (different screen) | unchanged |

Edge notes (all live-proven in the sister rig):
- Kaleidoscope-class obtain RIGHT AFTER a combat is still streamer-free under
  the toggle (its set has Room==null despite the fight) — the one visible
  divergence from "fought for it ⇒ vote"; accepted by Surfinite.
- The Lantern Key's scripted quest card is a `SpecialCardReward` (different
  class, no choice) — untouched by any of this.
- Tutorial first-run rewards are combat-origin via the Room signal (they LACK
  IsFromCombat — one of the reasons the flag is forbidden).

## 6. Suggested test/verify pass

- Sister-proven log-anchor idiom: log once per tagged set
  (`tagged N combat-origin card reward(s)`) and once per skipped vote
  (`non-combat card reward - vote skipped`); makes live verification
  grep-able instead of eyeball-only.
- Live matrix (adapt notes/57 from the sister repo): combat reward still
  voted (regression); console `relic KALEIDOSCOPE` → no vote, streamer free;
  dev-forced Future of Potions → no vote; The Hunt fatal kill → vote;
  checkbox OFF regression pass (everything votes again); flip the checkbox
  MID-RUN both directions; save → quit → continue mid-combat-reward (SP can
  resume — re-tag on resume is automatic via OfferRoomEndRewards re-running,
  verify the vote still opens).
- Unit-test surface: the settings key parse block (this repo has the same
  Bootstrap test harness pattern).

## 7. One-day porting estimate rationale

Everything hard was already done and live-verified in the sister repo: the
discriminator, the seam, the edge-case sweep (24 construction sites), the
fail-safe direction, and the tag-survives-reroll/resume proofs. What's new
here is purely donor-side: finding the screen→CardReward capture (4b), the
checkbox row, and the vote-skip predicate applied consistently across the
card-vote patch family.
