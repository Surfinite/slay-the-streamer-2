# Bossy Relics — pre-spec landscape investigation (StS2 v0.109.0)

Feature: relic rewards (chest, elite; possibly events) offer N relics (setting 1-4, default 1 = vanilla), streamer claims exactly one, the rest disappear, chain-link UI between choices. Not chat-voted (maybe later). All line numbers below reference the v0.109.0 decompile; game-source paths are given as `Core/...` = `decompiled/sts2/MegaCrit/sts2/Core/...`.

---

## 1. What vanilla already provides

The reuse story is unusually good: **the game ships two complete "offer N relics, claim exactly one" systems**, one dormant and one live.

### 1a. `LinkedRewardSet` — the chain-link mechanic exists, fully built, and NOBODY uses it

`Core/Rewards/LinkedRewardSet.cs` wraps a `List<Reward>` (arbitrary N, no pair assumption anywhere; ctor at L30-38 sets `ParentRewardSet` on every member). Claim flow:

- Claiming a member runs `Reward.SelectUnsynchronized()` (`Core/Rewards/Reward.cs:84-98`), which calls `ParentRewardSet.RemoveReward(this)` — the vanilla "claiming one removes it from the group" hook.
- The UI wrapper `NLinkedRewardSet.GetReward` (`Core/Nodes/Rewards/NLinkedRewardSet.cs:136-143`) then removes the whole group row from the screen, calls `OnSkipped()` on the *remaining* members (records `wasPicked:false` history), and frees itself.
- `NLinkedRewardSet.Reload` (L113-134) renders each member as a standard `NRewardButton` (20px narrower) in a VBox with **one 50×50 chain-link icon per adjacent pair** (`images/ui/reward_screen/reward_chain.png`, 128×128 RGBA). The loop is fully generic — N=4 renders 4 rows and 3 chains.
- `NRewardsScreen._Ready` special-cases it (`Core/Nodes/Screens/NRewardsScreen.cs:309-313`) — the whole group occupies one entry in `_rewardButtons`.
- Hover tip localized in all 14 languages ("Linked Rewards" / "You can only select [blue]1[/blue] reward from this set."); scene + chain texture preloaded for every run via `AssetSets.CommonAssets` (`AssetSets.cs:114`) — warm cache guaranteed.

**Dormancy, verified three ways:** grep across the entire v0.109.0 decomp finds **zero construction sites** — only the ctor declaration and consumer/renderer plumbing (7 files). The `NRewardButton.SetReward` guard even preserves an older internal name ("RewardChainSet", `NRewardButton.cs:156-160`). Built-then-shelved, plausibly for co-op; the multiplayer 4-relic chest does NOT use it.

Known warts of the dormant code (all confirmed, all workable — details in §4):
1. The parent's `SuccessfullySelected` is never set via the UI path, so the set never "completes" through claiming — `Log.Error` when the group is the last button, and a non-terminal screen's `Offer()` await hangs until room exit (`NRewardsScreen.cs:386-417`, `RewardsSet.cs:48`).
2. No serialization: `RewardType => None` (L14), `Reward.FromSerializable` default case throws `NotImplementedException` (`Reward.cs:148-149`).
3. Multiplayer sync broken: `SelectLocalReward` broadcasts `set.Rewards.IndexOf(reward)` = **-1** for a linked child (`RewardsSetSynchronizer.cs:152`); the remote replay is silently dropped (throw inside `TaskHelper.RunSafely`, L191/224-227).
4. Latent signal bug: `GetReward` emits `RewardClaimed` with zero args against a one-arg signal declaration — don't subscribe to it expecting the argument; the direct `RewardCollectedFrom` call is the live path.

### 1b. Multiplayer chest precedent — a different system entirely

The "4-player chest offers 4 relics" belief is TRUE but is `TreasureRoomRelicSynchronizer` (`Core/Multiplayer/Game/TreasureRoomRelicSynchronizer.cs`), not `RewardsSet`/`LinkedRewardSet`. Chests never touch `RewardsSet` at all (`RewardsSet.GenerateRewardsFor` yields an empty list for `TreasureRoom`; `TreasureRoom.EnterInternal` calls `BeginRelicPicking()`, `TreasureRoom.cs:48`).

- `BeginRelicPicking` (L68-111) pulls **one relic per eligible player** (gated by `Hook.ShouldGenerateTreasure`) from the run-shared grab bag, rarity rolled on the dedicated `RunRngType.TreasureRoomRelics` stream (constructed at `RunManager.cs:334`). The pull line is `TryGetRelicForTutorial(player) ?? _sharedGrabBag.PullFromFront(rarity, runState) ?? RelicFactory.FallbackRelic` — first-ever chest forces Gorget; Circlet backstops exhaustion.
- Claiming is a **vote**: sole voter wins; contested → rock-paper-scissors relic fight; unvoted relics → consolation prizes for vote-losers; leftovers → `Skipped`. Each player receives at most one relic. In true single-player the lone vote wins outright and the rest are Skipped — **exactly the feature's semantics, already enforced**.
- Unclaimed relics are `MoveToFallback`'d into players' grab bags — demoted to last-resort stock, not destroyed (`NTreasureRoomRelicCollection.cs:477-483`; runs in the UI node's `RelicsAwarded` handler, not the synchronizer).
- The chest UI already renders 1-4 relics natively: `treasure_room.tscn` ships `SingleplayerRelicHolder` + `MultiplayerRelicHolder1..4` (L221-285); `InitializeRelics` branches purely on `CurrentRelics.Count` (count 0 → empty-chest VFX; 1 → SP holder; 2-4 → MP holders with a count==2 reposition). **Hard cap 4** — relics at index ≥ 4 are silently never rendered or claimable.

### 1c. Relic rolling APIs

- `RelicReward` (`Core/Rewards/RelicReward.cs:45-62`) has three ctors: random (`Player`), predetermined (`RelicModel, Player`), fixed-rarity (`RelicRarity, Player` — consumes **no RNG**). `Populate()` (L64-85) pulls via `RelicFactory.PullNextRelicFromFront`, honoring a `SetRng(Rng)` override (`Reward.cs:110-114`) — **but only in the rarity-less branch**; the fixed-rarity ctor ignores `SetRng`.
- Pools are **pre-shuffled deques, not weighted rolls** (`Core/Runs/RelicGrabBag.cs`): shuffled once at run start with `State.Rng.UpFront`; a pull is deque removal and consumes zero RNG — only the rarity roll (`RelicFactory.RollRarity`, 50/33/17, L54-63) does.
- **Pulls are destructive and distinctness is automatic**: each pull removes the relic from the player's bag AND `SharedRelicGrabBag` (`RelicFactory.cs:30-35`); N sequential `Populate()` calls yield N distinct relics.
- Depletion degrades gracefully: rarity cascade Shop→Common→Uncommon→Rare → fallback deque → Circlet (stackable); the shared bag additionally self-refreshes (`refreshAllowed: true`, `RunState.cs:149`). Rolling extras can never crash.
- On-obtain effects (`relic.AfterObtained()`) fire inside `RelicCmd.Obtain` at claim time, **only for the claimed relic** (`Core/Commands/RelicCmd.cs:21-41`). Unclaimed relics trigger nothing — Sozu/Blood-Vial-alike timing is a non-issue.

---

## 2. Recommended implementation approach

### Elite relics: swap the lone `RelicReward` for a `LinkedRewardSet`

**Patch point:** Harmony postfix on **`RewardsSet.WithRewardsFromRoom(AbstractRoom)`** (public, returns `this`, `RewardsSet.cs:65-81`). It runs before `GenerateWithoutOffering`/`Populate`, so injected rewards get populated by the vanilla loop; and unlike a `GenerateRewardsFor` postfix it also sees the elite-tutorial path (`TryGenerateTutorialRewards`, `RewardsSet.cs:238-269`, which adds Vajra/OrnamentalFan bypassing `GenerateRewardsFor`). Scan the final `Rewards` list for an **unpopulated** `RelicReward` (predetermined tutorial relics are pre-populated → correctly skipped) and replace it with `new LinkedRewardSet(new List<Reward>{ original, extra1, ... }, player)`.

**Rolling the N-1 extras:** `new RelicReward(rarity, player)` with the rarity rolled by the mod on its own stream (mirror `RelicFactory.RollRarity(modRng)`; `Rng` is constructible: `new Rng(ulong seed, string name)`), or `new RelicReward(player).SetRng(modRng)` (cast — `SetRng` returns base `Reward`). **Do not let extras roll on `PlayerRng.Rewards`** — that stream is heavily shared (gold, potions, `CardFactory`, many events), and N-1 extra `NextFloat()`s would shift everything downstream. With a mod-owned stream, vanilla RNG streams stay byte-identical; the *bag state* still diverges (N pulled instead of 1), which is inherent to the feature. Distinctness is free (destructive pulls).

**Completion wart mitigation:** after a child claim, call `SelectUnsynchronized()` on the wrapper (its `OnSelect` trivially returns true, so this sets `SuccessfullySelected` and lets `CompleteRewardsSetIfNecessary` fire). This exact path is validated by vanilla's own TestMode flow (`RewardsSet.Offer`, L146-149). Cost: one extra `Hook.AfterRewardTaken` for the wrapper. Cheapest wiring: small postfix, or accept the benign `Log.Error` on terminal screens and only fix it for non-terminal ones.

**Determinism/save story is free:** the run save is written at combat victory, *before* rewards are generated (`CombatManager.cs:852-853`); the rewards set is never serialized; on Continue, `StartPreFinishedCombat` regenerates via `RewardsCmd.GenerateForRoomEnd`, the postfix re-runs, and because the save predates every pull the same relics are re-offered. Mod-absent reload silently falls back to vanilla — nothing mod-shaped in the save.

### Chest relics: append pulls into the existing vote system

**Patch point:** postfix on **`TreasureRoomRelicSynchronizer.BeginRelicPicking()`**, appending N-1 pulls to the private `_currentRelics` (AccessTools; `_sharedGrabBag`/`_rng` are also private fields). Mirror the vanilla per-pull shape: `RelicFactory.RollRarity(_rng)` + `_sharedGrabBag.PullFromFront(rarity, runState) ?? RelicFactory.FallbackRelic` (skip the tutorial override for extras). Guard `Players.Count == 1` (true single-player only), cap total at **4** (UI hard limit), and respect the session-already-active throw at L70-73. UI (holders, vote pips), claim-one enforcement, award animation, and `MoveToFallback` demotion of the unclaimed relics all come for free. Using the `TreasureRoomRelics` stream means extra rolls only diverge future *chest* rarity rolls — low collateral.

### Settings plumbing — four places, not three

`RelicsOffered` (int, default 1, clamp [1,4]):
1. `src/Game/Bootstrap/ModSettings.cs` — append at the **END** of the positional `ChatSettings` record (Load constructs fully positionally at L231; mid-record insertion silently misassigns). Clone the `voteDurationSeconds` parse shape (L186-199: default-if-missing, clamp-with-warning).
2. `src/Game/Bootstrap/SettingsBootstrap.cs` — `BuildTemplate()` (L46-59) + `AddMissingKeys` migration handles old installs.
3. `src/Game/Ui/Settings/SettingsWriter.cs` — **the Write whitelist (L29-34)**; miss this and the UI change silently doesn't persist. (The trace found a pre-existing bug proving the gotcha: `AllowSameBossTwice` has a panel checkbox and a Load path but is absent from the whitelist — UI toggles never persist. One-line fix, do it in this slice.)
4. `src/slay_the_streamer_2.json.example`.

Panel: clone `AddCardSkipsDropdown` (`SettingsPanelBuilder.cs:271-324` — values in `SetItemMetadata`, never item ids) with a 1/2/3/4 dropdown + `AddHelpText`. Consumers read `ModSettings.Current?.RelicsOffered ?? 1` at generation time, never cache. **N=1 → true no-op**: don't construct a single-member `LinkedRewardSet`; leave the vanilla `RelicReward` untouched.

### Where the code lives

- `src/Game/DecisionVotes/RelicChoicePatch.cs` (or new `src/Game/Rewards/`) — game-type-referencing, so it needs its own `<Compile Remove>` in `tests/slay_the_streamer_2.tests.csproj` (the DecisionVotes glob at L16 would otherwise sweep it into the Godot-free test build).
- Pure-logic extraction for unit tests: `RelicOfferPlanner` (clamp, context → count, N=1 degenerate case), plus settings round-trip tests in the three already-compiled test files (`ModSettingsTests`, `SettingsBootstrapTests`, `SettingsWriterTests`).
- Commit prefix: declare `bossy-relics/N:` in CLAUDE.md; tag `bossy-relics-complete` after operator validation.

---

## 3. Alternatives considered

- **`Hook.ModifyRewards` / `TryModifyRewardsLate` pipeline** (`RewardsSet.cs:106`, `Hook.cs:1584-1602`) — vanilla's own extension point (how BlackStar/LavaRock add rewards), but it requires an `AbstractModel` registered as a run hook listener, not a free callback. A Harmony postfix on `WithRewardsFromRoom` is simpler and equivalent. Keep in back pocket for ordering-after-other-modifiers needs.
- **Custom mod-built choice popup** — unnecessary; vanilla `LinkedRewardSet` UI + chest vote UI cover both surfaces with localized tips and preloaded assets.
- **Inject via `CombatRoom.AddExtraReward`** — forbidden. `ExtraRewards` is the only serialized reward container; a `LinkedRewardSet` there **bricks the save** (write succeeds silently, Continue crashes in `Reward.FromSerializable` before any mod hook runs). Plain `RelicReward`s would serialize but a mod-absent reload would then offer the extras *without* the claim-one gate.
- **Event relics in v1** — no choke point exists (~15 direct `RelicReward` construction sites: WarHistorianRepy, PunchOff, CrystalSphere, NeowsBones take-both with `WithSkippingDisallowed`, hooks, tutorials), and per-event semantics (predetermined relics, take-all designs) argue against blanket transformation. Defer; if added later, a `GenerateWithoutOffering`/`WithCustomRewards` postfix with a per-event allowlist is the shape — noting event screens are non-terminal, where the completion wart can hang `Offer()`, so the wrapper-select mitigation becomes mandatory there.
- **Reuse `AncientRelicExclusionPatches` machinery** — wrong subsystem (event option pools, not reward rolling). Only its lessons transfer: narrowest patch surface, first-fire-only logging.

---

## 4. Risks & edge cases

- **Pool depletion (the most load-bearing nuance).** Pulls are destructive to both bags; offering N and granting 1 consumes N-1 unclaimed relics per elite offer with no recovery. Chest unclaimed relics are demoted to `_mpFallbackDequeue` — but that deque is last-resort-only (reached after ALL rarities empty) and **is not serialized** (`SerializableRelicGrabBag` omits it) — lost on save-quit-Continue. Worse: a single-player chest **skip** bypasses `AwardRelics` entirely (`_singleplayerSkipped`, L160-163) — no `MoveToFallback`, N shared-bag entries just gone. Degradation is graceful (cascade → Circlet, never a crash), but at N=4 with 2-3 relic offers per act, late-run rare/uncommon exhaustion is plausible. Consider re-inserting unclaimed elite relics into the bags (see §5).
- **Save-quit mid-screen.** Elite: safe by construction — rewards regenerated deterministically from a pre-rewards save; claims are rolled back and re-offered identically. Chest: save-quit mid-chest reverts to an unopened chest; the postfix simply re-runs. Mod-absent reload: clean vanilla in both cases, *provided* injection stays generation-time-only and `LinkedRewardSet` never reaches `ExtraRewards`.
- **Skip-gate interaction (`CardRewardSkipGatePatch`) — already safe.** Its checks require `Reward is CardReward` exactly; `RelicReward` and `LinkedRewardSet` fail both, so no skip-budget double-charge, no mandatory-look gating, and a relic-only screen passes Proceed (`pending.Total == 0`). Its `GetRewardButtons` doc already anticipates `NLinkedRewardSet` in `_rewardButtons`. One real hazard: linked-set child buttons live in the set's internal container, invisible to `CountPendingCardRewards` — **spec must state linked sets contain `RelicReward`s only**. And do not "improve" the gate to cover relic rows (relics instant-claim like `SpecialCardReward` — the exact `stream-polish/3` regression class).
- **On-obtain effect timing.** `AfterObtained` fires only for the claimed relic at claim time; unclaimed relics never trigger anything. No Sozu/Blood-Vial-like double-fire risk.
- **Backend completion wart.** Without mitigation: `Log.Error("All rewards have been taken, but the rewards set is not complete...")` when the group is last claimed; on non-terminal screens the `Offer()` await hangs until room exit. Terminal elite screens are benign either way; apply the wrapper-select mitigation (§2).
- **Multiplayer.** Linked-child claims desync remote peers (silent dropped replay); fake-multiplayer bots auto-vote randomly on chest relics. **Gate both patches on true single-player** (`Players.Count == 1`, `NetGameType.Singleplayer`).
- **Chest UI cap of 4** — enforced by the scene's four holders; keep `_currentRelics.Count ≤ 4`. Also note both `.tscn` citations come from the repo's asset extraction, which may predate v0.109 — re-extract before relying on exact geometry if MegaCrit reships scenes.
- **Tutorial overrides.** First-ever chest forces Gorget; first two Ironclad elites route through `TryGenerateTutorialRewards`. The `WithRewardsFromRoom` postfix + unpopulated-`RelicReward` filter handles the elite case correctly (predetermined → already populated → skipped); consider skipping the multi-offer entirely on tutorial rewards.
- **RNG hygiene.** Extras must not roll on `PlayerRng.Rewards`; use a mod stream or fixed-rarity ctor (and remember `SetRng` is ignored on the fixed-rarity ctor path).
- **Layout.** No row cap on `NRewardsScreen`; a 4-relic linked group plus gold/potion/cards (~700px) scrolls fine (`CanScroll` at ≥400px). Row ordering unchanged: `LinkedRewardSet.RewardsSetIndex` = max of members = 3, sorting exactly where the single relic sorts today.
- **Signal bug.** Rely on `NLinkedRewardSet`'s direct `RewardCollectedFrom` call; never subscribe a second handler to its `RewardClaimed` (zero-arg emit vs one-arg declaration).
- **Settings whitelist.** `SettingsWriter.Write` silently drops unlisted keys; also fix the `AllowSameBossTwice` persistence bug (one line) in this slice.

---

## 5. Open questions for Surfinite

1. **Scope: chest + elite only for v1?** Events have no choke point and take-all/predetermined designs (NeowsBones, WarHistorianRepy) that a blanket transform would break — research recommends deferring events, but it's a design call.
2. **Boss rewards?** StS2 boss fights ship NO relic reward at all (no StS1-style boss-relic choice — `RewardsSet.cs:196-200`). Should Bossy Relics *add* a relic choice to boss rewards (new content, not a multiplier), or stay strictly "multiply what vanilla offers"?
3. **Unclaimed-relic economy:** accept permanent pool consumption (N-1 relics burned per offer, graceful Circlet degradation late-run), or re-insert unclaimed relics into the grab bags after each choice? Re-insertion keeps the relic economy vanilla-shaped but means the same relics can be re-offered later — is that desirable ("saw it, skipped it, it comes back") or annoying?
4. **When the pool can't fill N** (or the pull returns Circlet fallbacks): offer fewer relics than N, pad with Circlets, or fall back to vanilla single-relic for that offer?
5. **One setting or two?** Same `relicsOffered` value for chests and elites, or independent knobs (chest capped at 4 by UI; elite has no hard cap but 4 is the sane max)?
6. **Chest skip semantics at N>1:** vanilla SP skip currently burns the pulled relics from the shared bag. Acceptable at N=4 (skip costs 4 pool entries), or should the mod restore skipped chest relics?

---

## 6. Runtime-spike checklist (before or during implementation)

Residual unknowns the code-read couldn't settle — each is a quick in-game check once a prototype exists:

1. **Chain-icon placement at N=3/4** — `NLinkedRewardSet` captures `GlobalPosition` during `_Ready`, before container layout settles. Vanilla never renders this scene; MegaCrit may never have seen it on screen. Verify the 2-3 chain links land between rows, not drifted.
2. **Controller focus into linked children** — the screen's focus wiring treats the whole set as one Control; confirm gamepad navigation reaches the inner `NRewardButton`s (modding menu just gained controller support, so streamers may use pads).
3. **Singleplayer `INetGameService.SendMessage` loopback** — a linked-child claim broadcasts `rewardIndex = -1`; confirm the singleplayer service discards it (assumed no-op) rather than looping it back into the local handler.
4. **Save-quit mid-chest with N>1** — `_currentRelics` is in-memory; on Continue the room re-enters and re-rolls from `RunRngType.TreasureRoomRelics`. Confirm the re-roll offers the same relics (stream position is save-checkpointed pre-chest) and that the extra pulls don't hit the CLAUDE.md pre-mutation-snapshot landmine.
5. **Double-`OnSkipped` on linked children** (screen removal + proceed-skip both call it) — check whether duplicate `wasPicked:false` run-history entries bother anything (stats screen), else dedupe.
6. **Single-player chest with 2-4 relics** takes the multiplayer-holder path (`VoteContainer.RefreshPlayerVotes`, hands animation with 1 player) — visual smoke test for layout/vote-pip weirdness.

Cross-branch note: any mod-owned `Rng` construction must go through the existing `SeedCompat` reflective pattern (`src/Game/DecisionVotes/SeedCompat.cs`) — the ctor is `(ulong[, string])` on v0.109+ but `(uint, ...)` on the v0.107/v0.108 branches the Workshop build also serves.
