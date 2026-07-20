# Bossy Relics — design spec (2026-07-20)

Pick-1-of-N relic rewards for chests and elites. Streamer-rebalance feature suggested by
FrostPrime, modelled on the StS1 "Bossy Relics" mod. Not chat-voted (possible future
extension). Research basis: `notes/11-bossy-relics-investigation.md` (all file:line
citations live there; this spec states decisions).

## Decisions locked with Surfinite (2026-07-20)

- Approach **A**: reuse vanilla `LinkedRewardSet` for elites; append into the vanilla
  chest relic-picking system for chests. No custom UI.
- One setting for both surfaces: **`RelicChoices`**, int 1–4, **default 1** (= exact
  vanilla, no wrapper objects constructed).
- Extra relics get **independent rarity rolls** (50/33/17) on a mod-owned RNG stream.
- Unclaimed relics are **re-inserted at the back** of their rarity deque, in **both**
  the player grab bag and the shared grab bag.
- **Full refund on skip**: skipping the rewards screen / chest at N>1 returns all N
  pulls to the pool (back of deque). At N=1 vanilla behavior is untouched (including
  vanilla chest-skip's existing one-pull burn).
- Shortfall: none expected with re-insertion; if the pool is truly empty vanilla's own
  Circlet fallback applies (we implement nothing).
- Tutorial/predetermined rewards stay vanilla single offers (first-chest Gorget,
  scripted first-elite relics).
- Out of scope for v1: event relics, boss rewards (StS2 bosses drop no relic; Ancients
  fill that role), chat voting, multiplayer.
- Settings panel: the new dropdown is added **above** the "Open settings folder"
  button — that button stays at the bottom of the panel.

## 1. Player-visible behavior

At `RelicChoices = N > 1`, single-player only:

- **Elite combat rewards**: the relic reward row becomes a chain-linked group of N
  relic rows (vanilla chain-link icon between adjacent rows; vanilla "you can only
  select 1 reward from this set" hover tip). Claiming one removes the whole group and
  returns the other N−1 relics to the back of their rarity deques. Skipping the screen
  returns all N.
- **Treasure chests**: opening the chest shows N relics using the game's existing
  1–4-relic holder layout. Claiming one awards it; the rest go back (back of deque).
  Skipping the chest returns all N.
- Rarity of each extra: independent 50/33/17 roll on the mod's own RNG stream. The
  original (vanilla) relic's roll is untouched and stays on its vanilla stream.
- Distinctness is inherent: pool pulls are destructive, so N pulls are N different
  relics. On-obtain effects (Sozu-likes) fire only for the claimed relic (vanilla
  guarantee).

## 2. Architecture

New folder `src/Game/Rewards/` (feature is not voting; keep DecisionVotes clean).

### 2.1 `EliteRelicChoicePatch`

- Harmony **postfix on `RewardsSet.WithRewardsFromRoom(AbstractRoom)`** (runs before
  `Populate`, and sees the tutorial path).
- Gates (all must hold, else no-op): `RelicChoices > 1`; exactly one **unpopulated**
  `RelicReward` in `Rewards` (populated = predetermined/tutorial → skip); single
  player; not already wrapped.
- Action: replace that `RelicReward` with
  `new LinkedRewardSet([original, extra1..extraN-1], player)`; each extra is a
  fixed-rarity `RelicReward(rarity, player)` with rarity rolled on the mod stream
  (mirror `RelicFactory.RollRarity`). Vanilla `Populate` then pulls all members.
- **Completion bookkeeping mitigation** (dormant-code wart): after a child claim
  completes, invoke the wrapper's own select path (`SelectUnsynchronized`) so
  `SuccessfullySelected` is set and `CompleteRewardsSetIfNecessary` fires — avoids the
  vanilla `Log.Error` when the group is the last reward and the `Offer()` hang on
  non-terminal screens.
- Unclaim/refund wiring: on child claim, return the group's remaining relics via
  `RelicReturnHelper`; on screen skip (`OnSkipped` path), return all members.

### 2.2 `ChestRelicChoicePatch`

- Harmony **postfix on `TreasureRoomRelicSynchronizer.BeginRelicPicking()`**.
- Gates: `RelicChoices > 1`; true single-player (`Players.Count == 1`, singleplayer
  net game type); `_currentRelics.Count == 1` after vanilla ran (0 = empty-chest /
  `ShouldGenerateTreasure` false → leave alone; tutorial Gorget is the first-chest
  pull — detect and leave alone); total capped at **4** (UI hard limit: the chest
  scene has exactly 4 holders).
- Action: append N−1 pulls to the private `_currentRelics`, mirroring the vanilla
  pull shape (`RollRarity` on the mod stream + `_sharedGrabBag.PullFromFront(...)
  ?? RelicFactory.FallbackRelic`).
- Claim-one enforcement, holder layout, award animation: vanilla (single-player vote
  degenerate case — sole voter wins, rest are Skipped).
- Refund wiring: after award resolution, return unclaimed pulls to the back of the
  deques via `RelicReturnHelper` (superseding vanilla's `MoveToFallback` demotion for
  those relics); on full chest skip (`_singleplayerSkipped` path, which bypasses
  award), return all N pulls.

### 2.3 `RelicReturnHelper` (+ `RelicChoicePlanner`)

- `RelicReturnHelper` (game-type-referencing): pushes a relic to the **back** of its
  rarity deque in both the player `RelicGrabBag` and the `SharedRelicGrabBag`
  (reflection into the deque fields; restoration must mirror the pull's removal from
  both). Idempotence guard: never insert a relic already present in the deque or
  already owned by the player.
- `RelicChoicePlanner` (pure, System-only, unit-testable): clamp(1–4), N→extra-count,
  gate predicates (already-wrapped / populated-relic / player-count / current-count →
  offer-count decisions) expressed on plain values.
- Mod RNG: one `Rng` created per run-ish scope via the existing **`SeedCompat`**
  reflective pattern (`src/Game/DecisionVotes/SeedCompat.cs`) — the `Rng` ctor differs
  across the game branches the Workshop build serves. Seed: a fresh `Rng` **per
  offer**, derived from (run `StringSeed` hash, surface salt "bossy-elite"/"bossy-chest",
  current act + floor) — so a save-quit-regenerated offer reproduces the same rarity
  rolls with no stream-position tracking, and no vanilla RNG stream is ever consumed.

## 3. Settings plumbing

`RelicChoices` (int, default 1, clamp [1,4] with warning on load):

1. `src/Game/Bootstrap/ModSettings.cs` — appended at the **END** of the positional
   `ChatSettings` record (mid-record insertion silently misassigns — known trap).
2. `src/Game/Bootstrap/SettingsBootstrap.cs` — template + missing-key migration (old
   settings files keep working, get default 1).
3. `src/Game/Ui/Settings/SettingsWriter.cs` — added to the **Write whitelist**
   (unlisted keys silently don't persist). Same slice: add the missing
   `AllowSameBossTwice` whitelist entry (pre-existing bug — its checkbox never
   persists).
4. `src/Game/Ui/Settings/SettingsPanelBuilder.cs` — 1/2/3/4 dropdown cloned from the
   card-skips dropdown (values via `SetItemMetadata`), with help text ("Relics offered
   per chest / elite reward — pick 1, the rest return to the pool"). Inserted
   **above the "Open settings folder" button**, which stays last.
5. `src/slay_the_streamer_2.json.example`.

Consumers read `ModSettings.Current?.RelicChoices ?? 1` at generation time; never
cache across rooms.

## 4. Error handling & safety rails

- Every postfix body in try/catch → on exception, log (`[bossy-relics]` prefix) and
  leave vanilla behavior intact (same passing-through pattern as the vote patches).
- **Never** inject into `CombatRoom.ExtraRewards` (it is the only serialized reward
  container; a `LinkedRewardSet` there bricks the save on reload).
- Nothing mod-shaped is serialized anywhere: elite rewards regenerate from the
  pre-rewards checkpoint on Continue (postfix re-runs, seed-stable → same offer);
  chest re-entry re-runs the postfix. Mod-absent reload = clean vanilla.
- Skip-gate compatibility is inherent (`CardRewardSkipGatePatch` type-checks
  `is CardReward`; linked groups contain `RelicReward`s only — spec invariant).
- Multiplayer: both patches hard-gate on single-player; the linked-set MP sync bug
  and chest-bot voting are therefore unreachable.
- Do not subscribe to `NLinkedRewardSet.RewardClaimed` (latent zero-arg emit vs
  one-arg signal declaration); use the direct claim path only.

## 5. Testing & validation

**Unit** (tests csproj; the three patch/helper files get `Compile Remove`):
- `RelicChoicePlanner` gate/clamp/count logic.
- Settings round-trip: load default when key missing; clamp 0→1 / 9→4 with warning;
  writer persists `RelicChoices` and `AllowSameBossTwice`.

**Operator validation checklist** (from notes/11 §6 runtime spikes):
1. Elite at N=2/3/4: chain icons land between rows (vanilla never rendered this
   scene); claim removes group; declined relics reachable later (dev console pool
   inspection); no `Log.Error` completion spam; Proceed works on relic-only screen.
2. Chest at N=2/3/4: holders layout sane with one player; claim awards exactly one;
   skip refunds all; first-ever chest still forces Gorget solo.
3. Save-quit mid-rewards-screen and mid-chest → Continue: same offer re-appears;
   claim once, no dupes. Remove mod → reload: clean vanilla.
4. Controller: focus reaches the inner linked rows.
5. N=1 (default): byte-identical vanilla behavior, no wrapper constructed.
6. Log check: no `rewardIndex=-1` loopback throw on linked-child claim in
   single-player.

## 6. Slice conventions

- Commit prefix: `bossy-relics/N.M:`; tag `bossy-relics-complete` after operator
  validation. Add prefix to CLAUDE.md commit conventions in the first commit.
- Release: minor version bump (v0.2.x or v0.1.6 — decide at release time), Workshop
  changeNote highlighting the new setting, README settings-table row.
