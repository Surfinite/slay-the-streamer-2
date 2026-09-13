# Streamer-vs-Streamer tournament mod — consolidated feasibility research

Pre-spec landscape research for the FrostPrime (Tristan) commission, consolidated from four
decompile research passes + two design calls, 2026-08-05/06. This document is the basis for
the design/spec work in the new repo; it is written to be self-contained and portable.

Decompile referenced: v0.109/v0.110-era `decompiled/sts2/` (namespace-flattened). File paths
below are relative to that root unless they start with `src/` (our mod repo).

---

## 1. The commission

- **Format** (Tristan's vision, "the next Devour the Tower"): teams of 2 — one **Player**
  (streamer, plays the run), one **Builder** (the *opposing* team's saboteur, joins the
  Player's game and controls what enters their deck). Each match = two independent games
  running in parallel; team A's builder sits in team B's game and vice versa. Winner decided
  out-of-band by run progress.
- **Builder powers** (agreed on calls): picks the Player's card rewards, picks Ancient/Neow
  blessings, picks the act boss, gets sabotage potions from chests and throws them at
  opportune mid-combat moments. Builder has **no say** in route (map), relics, or combat.
- **Player powers**: plays combat, picks route, picks chest relics (1-of-3), can **override**
  the Builder's card pick **once per act** (needs full visibility of the option list while an
  override remains).
- **Run end**: if the Player dies, the whole run ends (kill the Builder too → vanilla
  game-over fires naturally).
- **Tiebreak**: if both teams' Players reach the same point (or both win), a
  damage-race-vs-target-dummy in X turns, using the exact build the Player finished with.
  Must trigger **only when a tie is declared**, not at every run end (Tristan: players won't
  try otherwise). Simultaneous start handled by commentator countdown (deliberately not
  mechanized in v1).
- **Timeline**: proof-of-concept ~1 month (enough for Tristan to start promoting), tournament
  ~1 month after that.
- **Scoring**: how far through the run; out-of-band (spreadsheet/admin), not the mod's job.

### Route A fallback (still on the table if Route B stalls)
Builder as an *external* picker riding the existing Slay-the-Streamer suspend-and-resume vote
patches — structurally a single voter with 100% weight, no co-op netcode at all. Weaker
builder experience (no in-engine view). Everything below is the chosen **Route B**: builder
as a real co-op player with decision authority swapped.

---

## 2. Load-bearing engine facts (research pass 1: lobby + netcode)

### Lobby
- `MegaCrit.Sts2.Core.Multiplayer.Game.Lobby/StartRunLobby.cs` (pre-run),
  `RunLobby.cs` (in-run rejoin/disconnect), `LoadRunLobby.cs` (resume saved MP run),
  `MegaCrit.Sts2.Core.Multiplayer.Game/JoinFlow.cs`.
- `LobbyPlayer` (struct, `IPacketSerializable`): `ulong id` (Steam64), `int slotId`
  (**serialized as 2 bits → hard max 4 players**), `CharacterModel character`,
  `SerializableUnlockState`, `maxMultiplayerAscensionUnlocked`, `isReady`.
  **No role field** — role assignment must be mod-carried (custom `INetMessage`).
- Join handshake (`JoinFlow.Begin`, 10s timeout) rejects on: version mismatch, **mod-list
  set-difference in either direction** (`ModManager.GetGameplayRelevantModNameList()`,
  `id + "-" + version`, opt-out via `manifest.affectsGameplay = false` — our mod MUST NOT
  opt out), and `idDatabaseHash != ModelIdSerializationCache.Hash`.
  → both clients always run the identical mod build; "only one side modded" cannot connect.
- Transport: custom abstraction, two backends — Steamworks.NET P2P
  (`Transport.Steam/SteamHost.cs`, friends-only lobby, flips private at run start) and ENet
  direct-IP (debug harness `NMultiplayerTest.cs`). Pure P2P star topology; host relays;
  clients cannot message each other directly (`NetClientGameService.SendMessage` to non-host
  throws).

### Simulation model — the single most important fact
**Deterministic mirrored simulation.** Every client runs the full game for ALL players from
the shared seed. Consequences used throughout this design:
- The Builder's machine **already generates the Player's card rewards, event options,
  relic offers, etc. locally**. Displaying another player's choices costs UI only, zero
  netcode.
- Command flow: client `RequestEnqueue(action)` → host orders → broadcast
  (`GameActions.Multiplayer/ActionQueueSynchronizer.cs`). `Cmd` façades
  (`Core.Commands/*Cmd.cs`) are **local**; the wire units are `INetAction` / `INetMessage`
  (hand-rolled bit-packing, `Multiplayer.Serialization/PacketWriter`).
- **`ChecksumTracker`** (`Multiplayer.Game/ChecksumTracker.cs`) XxHash-checksums state per
  action; divergence → run abandoned (`RunManager.cs:1193`) + Sentry report to MegaCrit.
  Iron rule: every mod mutation must run identically on both clients, inside the
  synchronized execution path (GameAction/Hook), never touching shared RNG streams
  out-of-band.
- **Mod wire types are first-class**: `MessageTypes.cs`, `ActionTypes.cs`, and `ModelDb.cs`
  all reflect over mod assemblies (`ReflectionHelper.GetSubtypesInMods<T>()`) — mods can
  define `INetMessage`/`INetAction`/models with stable type ids. (Verify `NetTypeCache`
  ordering is deterministic across clients — assumed same-mod-set ⇒ same order.)
- Sender identity on messages is **client-authored and unverified** end-to-end
  (`NetMessageBus.SerializeMessage` writes the sender's own ulong; host trusts/relays it).
  Spoofing works today but is a vanilla trust hole MegaCrit may close — **prefer explicit
  mod messages carrying a target player id.** `PlayerChoiceSynchronizer.ReceiveReplayChoice`
  is public and does exactly the right thing for injecting a choice for an arbitrary player.

### What a "player" is — the one hard engine constraint
- `Player` has non-nullable `Character` + `Creature`. **Zero spectator/observer support**
  (repo-wide grep: 0 hits). A connected peer without a `Player` is affirmatively
  disconnected (`RunLobby` → `NetError.RunInProgress`) and would NRE in three synchronizers.
- **Therefore the Builder must join as a real `LobbyPlayer` with a real character/creature.**
  The mod makes them *inert* (empty deck, undamageable, reward-suppressed), never *absent*.

### Choice protocol (card rewards) — the authority-swap seam
`Core.Rewards/CardReward.cs::OnSelect` runs on both clients for both players and forks on
**`LocalContext.IsMe(base.Player)`** (backed by settable static `LocalContext.NetId`):
- mine → show `NCardRewardSelectionScreen`, await pick, `SyncLocalChoice(player, choiceId, result)`
- not mine → `await WaitForRemoteChoice(player, choiceId)`

Wire payload: `PlayerChoiceMessage { uint choiceId; NetPlayerChoiceResult }` — **an index
into the locally-regenerated option list**. Target player derived from packet sender.
`ReserveChoiceId`/`ValidateChoiceId` keep per-slot counters — **both clients must flip
authority consistently or one hangs on `WaitForRemoteChoice` / throws on id mismatch.**
This bookkeeping is the #1 PoC risk to burn down first.

Vanilla shared-decision UI precedents worth stealing from: `MapVote` +
`MapSelectionSynchronizer`, `NetScreenType.SharedRelicPicking`,
`NMultiplayerPlayerExpandedState` (existing "inspect remote player's full state" screen),
`NRewardsScreen._waitingForOtherPlayersOverlay`, `NMultiplayerVoteContainer` (vote chips).

### Spectating / builder viewpoint
Netcode syncs full teammate combat state + input intent to every client; combat-only;
`LocalContext.NetId` is the identity seam. Mid-combat partner-screen spectating is proven
feasible (see memory note `sts2_coop_replication_facts`; community mod **TeammateSpectate**
is prior art — find its source on GitHub before decompiling). Non-combat screens come free
from deterministic mirroring.

---

## 3. Combat facts (research pass 2)

### Solo-feel scaling — near-single seam
- HP: all monster HP scaling routes through `Creature.ScaleHpForMultiplayer`
  (`Entities.Creatures/Creature.cs:639`, wrapper at :335; called from
  `CombatState.CreateCreature`; monster-specific respawn/spawn re-scales in
  `TestSubject.cs:338`, `ToughEgg.cs:172`, `DecimillipedeSegment.cs:137`,
  `BattlewornDummy.cs:41-43` also route through it).
- Block: `Models.Singleton/MultiplayerScalingModel.cs` — `GetMultiplayerScaling` (act-based
  1.1/1.2/1.3) + `ModifyBlockMultiplicative` (early-returns 1m when count==1).
- Power stacks: gate at `Commands/PowerCmd.cs:84` (`Players.Count > 1 &&
  power.ShouldScaleInMultiplayer`); ~13 powers opt in (Artifact, CurlUp, Plating, Rampart,
  Regen, Skittish, Slippery, Shriek, Plow, Flutter, HardenedShell, Reattach); bespoke
  formulas in `BufferPower.cs:32`, `ArtifactPower.cs:45`, `SkittishPower.cs:82`,
  `SlipperyPower.cs:43`, `PlatingPower.cs:33,81`.
- Hardcoded one-offs: `KnowledgeDemon.cs:213` (heal 30 × count), `WaterfallGiant.cs:267`.
- **No** player-count spawn scaling found; monster damage numbers are not count-scaled.
- Patch plan: force count-1 semantics at `ScaleHpForMultiplayer` +
  `ModifyBlockMultiplicative` + the `PowerCmd` gate + the enumerated one-offs.

### Targeting — monsters hit ALL players by default
- `Models/MonsterModel.cs:385-397 PerformMove()`: `targets = combatState.PlayerCreatures`
  (everyone). `Commands.Builders/AttackCommand.cs:98-117` same;
  `FromMonster()` auto-`TargetingAllOpponents`. Debuffs applied to full target list.
- So an invulnerable Builder does NOT absorb hits away from the Player — Player takes
  normal damage. Residual: `TargetingRandomOpponents` (rolls `Rng.CombatTargets`,
  AttackCommand.cs:210/418) can waste a single-target hit on the Builder.
- Chokepoint: filter the Builder's creature out of the target list at
  `MonsterModel.PerformMove` (highest-leverage single edit; deterministic since both
  clients run the mod). NOTE `Creature.IsHittable` is NOT enough — `AttackCommand.Execute`
  filters on `IsAlive`, so also zero damage to the Builder in `CreatureCmd.Damage`
  (godmode) as belt-and-braces.
- Unaudited: ~100 monster models not all read for bespoke per-player behavior; sampled
  GremlinMerc, KnowledgeDemon, WaterfallGiant, TestSubject, ToughEgg, DecimillipedeSegment.

### Turn structure — simultaneous, no patch needed for mid-turn potions
- `Core.Combat/CombatManager.cs`: one shared `PlayerTurnPhase` for all players
  (`SetPhaseForAllPlayers`); turn ends when ALL ready (`AllPlayersReadyToEndTurn`, :562).
  Builder can freely act during the Player's turn.
- **Constraint to communicate to Tristan**: `CombatPlayPhaseOnly` actions (incl. potions)
  requested during the enemy turn are **deferred to the start of the next player turn**
  (`ActionQueueSynchronizer.RequestEnqueue`, :104-111); during `EndTurnPhaseOne` they are
  cancelled. So "chug as the boss winds up" resolves before the next player turn, not
  mid-animation.
- Dead players are auto-readied (:378-389) — a dead Builder never stalls the turn.

### Death / run end
- Run lost only when **all** players' creatures dead (`CreatureCmd.cs:319-323`,
  `RunState.IsGameOver` :111-121). Player death → `DeactivateHooks()` (removes their
  cards/relics/potions from hook iteration) + `CombatManager.HandlePlayerDeath` (cards
  removed, energy zeroed). `Player.ReviveBeforeCombatEnd()` (:630) revives all to 1 HP at
  the top of `EndCombatInternal` (before rewards) on victory.
- **"Run ends when Player dies"**: hook the player-death path, `CreatureCmd.Kill` the
  Builder's creature from inside the same synchronized execution → both dead → vanilla
  game-over. Cheap; the invulnerable Builder can never die otherwise, so this is the sole
  Builder-death path.

### Empty deck — safe
- `CombatManager.SetupPlayerTurn` → `CardPileCmd.Draw` (:704-759) guards empty draw+discard
  ("no draw" thought bubble, no crash). Turn-end paths iterate empty piles fine.
- Residual: `Hook.AfterHandEmptied` fires every turn for a card-less player — harmless if
  the Builder holds no relics (we strip them anyway); listeners unaudited.

### Potions — the strongest vanilla assist
- `GameActions/UsePotionAction.cs` — fully routed through the synchronized action queue;
  `CombatPlayPhaseOnly` in combat. `PotionModel.EnqueueManualUse`.
- **Cross-player targeting is first-class**: `TargetType.AnyPlayer`
  (`PotionModel.IsValidTarget` :222-257), `CanThrowAtAlly()` (:337). A custom `OnUse` can
  reach `target.Player` and mutate anything (deck/energy/hand) — sabotage potions are
  near-free.
- Modded potions register via `ModelDb` reflection like all models; enter random pools only
  via `ModHelper.AddModelToPool<TPool, TModel>()` (we'll hand-offer instead — see chests).

### Deck mutation seams (cross-player add/remove)
- Add: `player.RunState.CreateCard(ModelDb.Card<T>(), player)` then
  `CardPileCmd.Add(card, PileType.Deck)` (`Add` throws if card not registered to that
  player's RunState). Canonical worked example: `CardPileCmd.AddCursesToDeck` (:922-936).
- Remove: `CardPileCmd.RemoveFromDeck(card, showPreview)` (:37/42).
- Both are deterministic **only when invoked from inside the synchronized execution path**
  (a GameAction's `ExecuteAction` or a Hook). Never consume shared `RunState.Rng` streams
  from UI code.

---

## 4. Treasure chests (research pass 3)

### Vanilla co-op chest flow
- `Rooms/TreasureRoom.cs` → `TreasureRoomRelicSynchronizer.BeginRelicPicking()`
  unconditionally (solo = the 1-player case of the same path).
- `Multiplayer.Game/TreasureRoomRelicSynchronizer.cs`: **one relic per player** pulled from
  `SharedRelicGrabBag` via shared `Rng.TreasureRoomRelics` → **one shared option list**,
  identical on both clients. Realtime voting (`PickRelicAction` through the action queue;
  vote chips via `NMultiplayerVoteContainer`; re-clickable until all commit).
- Resolution (`AwardRelics`, :188): sole voter wins outright; contested → **rock-paper-
  scissors fight whose moves are RNG-rolled, not player-chosen** (pure theater over a coin
  flip — `RelicPickingResult.GenerateRelicFight`, `RelicPickingFightMove`); leftovers →
  consolation prizes **only to players who got nothing AND didn't skip**; rest `Skipped`
  (owner null). Winners' relics are demoted in other players' grab bags (`MoveToFallback`).
- Skip = `PickRelicAction` with null index; counts as a received vote (no stall).

### Our design mapped onto it
- **Player gets 1-of-3**: our shipped `src/Game/Rewards/ChestRelicChoicePatch.cs` already
  postfixes `BeginRelicPicking` to append pulls — drop its `players.Count != 1` gate.
  UI renders up to 4 holders (`treasure_room.tscn`: `MultiplayerRelicHolder1..4`).
  Invisible-4th-holder controller-focus risk: **deprioritized** (decision 2026-08-06).
- **Builder never gets a relic**: mod auto-submits the Builder's skip vote at
  `BeginRelicPicking` time → Player is the only real voter → always sole-voter win, RPS
  never fires, no consolation to the Builder.
- Refund bookkeeping (`ChestRelicRefundPatch`, `ChestRefundDemotionGuardPatch`) assumes
  solo; needs co-op-aware rework around the consolation/`Skipped`/`MoveToFallback` branches.
- RPS-as-mechanic (Builder contests a relic): available later, but it's a *random* steal,
  not skill expression — flavor decision for Tristan, don't sell it as gameplay.

### Builder's sabotage-potion choice from the same chest
- The relic synchronizer's offer is a single shared `List<RelicModel>` — cannot hold potions
  or per-player lists. **Use the other channel**: `TreasureRoom.DoExtraRewardsIfNeeded`
  (:66-88) builds a **per-player `RewardsSet`** that is normally EMPTY for treasure rooms
  (`RewardsSet.GenerateRewardsFor` :164-204 adds nothing for `TreasureRoom`).
- Postfix `RewardsSet.WithRewardsFromRoom` filtered to `room is TreasureRoom` + Builder's
  `Player`: add N `PotionReward`s wrapped in vanilla's `LinkedRewardSet` for pick-1-of-N —
  **reuses Bossy Relics machinery near-verbatim** (`LinkedSetSignalRewirePatch` etc.;
  only the relic-specific refund patch doesn't transfer, and potions need no refund).
- Potion generation: `PotionFactory.CreateRandomPotionsOutOfCombat(player, count, rng,
  blacklist)` or hand-constructed sabotage `PotionReward`s.
- `RewardsSet.Offer` shows the screen only when `LocalContext.IsMe(Player)` → the potion
  screen naturally appears only on the Builder's machine.
- Note: the elite-path `EliteRelicChoicePatch` is solo-gated and inert in co-op (chest
  relics never pass through `RewardsSet`/`RelicReward` anyway).

---

## 5. Events, ancients, map, cards, boss (research pass 4)

### Events / Ancient blessings
- `Multiplayer.Game/EventSynchronizer.cs`; two modes on `EventModel.IsShared` (default
  false).
- **Non-shared (ALL ancients + Neow)**: one clone **per player** (`canonicalEvent.
  ToMutable()` per player, indexed by slot on every client), **per-player RNG**
  (`EventModel.cs:195`: seeded `Seed + Owner.NetId` → different options per player). Each
  player picks their own blessing. Choice = `ChooseLocalOption(index)` →
  `OptionIndexChosenMessage`, applied to the *sender's* copy on receipt. Single UI funnel:
  `NEventRoom.OptionButtonClicked` → `EventSynchronizer.ChooseLocalOption`.
- **Builder-picks-blessing design**: every client holds all players' event instances, so
  the Builder's machine already has the Player's exact options to render. Builder's pick →
  our own `INetMessage` → the Player's client calls `ChooseLocalOption(index)` as itself.
  Builder's own parallel event copy auto-resolves to its leave/harmless option (never
  blocks). Same effort class as the card-reward swap; different seam (no `IsMe` fork here).
- **Shared events (8)**: `BattlewornDummy, DenseVegetation, FakeMerchant,
  JungleMazeAdventure, MorphicGrove, PunchOff, TheLanternKey, WarHistorianRepy` — vote per
  page, host resolves by **picking one player's vote uniformly at random**
  (`_multiplayerOptionSelectionRng.NextItem(_playerVotes)`), applied to all copies.
  Mirror-vote (Builder auto-copies Player's vote) makes the random pick a no-op — same
  trick as the map (below). Event handling overall still needs a design discussion with
  Tristan (deferred).

### Map route selection
- Click: `NMapPoint.cs:209` → `NMapScreen.OnMapPointSelectedLocally` (:643) →
  `VoteForMapCoordAction` through the action queue. Re-clicking your voted node = map ping,
  not a vote clear.
- Resolution (`MapSelectionSynchronizer.MoveToMapCoord`): once ALL players voted, host
  picks **one player's vote uniformly at random** (seeded `new Rng(_runState.Rng.Seed)`).
  **Not majority, not host-decides.** No player-facing abstain; **a never-voting player
  stalls the run forever** (no timeout).
- **Design — Player owns the route**: (a) block the Builder's map clicks locally
  (`NMapPoint.State`/travelable assignment block `NMapScreen.cs:588-627`, or
  `SetTravelEnabled(false)`, or prefix `OnMapPointSelectedLocally` — all local-only, no
  desync surface); (b) **mirror-vote**: Builder's client auto-submits a vote identical to
  the Player's the moment it lands → unanimous → random pick is a no-op, nothing stalls.
  This also retires the "passive player stalls the run" risk — map voting was where it
  lived.

### Multiplayer-only content removal (solo feel)
- Tag: `CardModel.MultiplayerConstraint` (`CardModel.cs:276`), enum
  `{None, MultiplayerOnly, SingleplayerOnly}`. **21 MP-only cards** (BeaconOfHope,
  BelieveInYou, Coordinate, DemonicShield, EnergySurge, Flanking, GangUp, GlimpseBeyond,
  HammerTime, HuddleUp, Ignition, Intercept, Knockdown, Largesse, LegionOfBone, Lift,
  Mimic, Rally, Sneaky, TagTeam, Tank); **1 SP-only** (WellLaidPlans — gets re-enabled,
  which is correct for solo feel).
- **TWO seams, patch both**: `CardFactory.FilterForPlayerCount` (`CardFactory.cs:25`,
  rewards) AND `IRunState.CardMultiplayerConstraint` (`IRunState.cs:73`, **default
  interface property** — returns SP-only constraint when count ≤ 1; consumed by
  `CardPoolModel.GetUnlockedCards` → merchant, Discovery-style generators, several
  potions, powers). Patching only the factory leaks MP cards via shops/generators.
  Verify whether concrete `RunState` overrides the default interface prop (Harmony target
  differs).
- **MassiveScroll must be suppressed** (`Relics/MassiveScroll.cs:21`, MP-only, Ancient
  rarity): its effect builds a pool of MP-only cards — empty pool ⇒ likely crash.
- SP-only relics re-enabled via their `IsAllowed` (`SilverCrucible.cs:83`,
  `WingedBoots.cs:49`). Grab bags are built at run start and serialized — flip semantics
  before run start, not mid-run.
- Rest site: co-op-only `MendRestSiteOption` (`RestSiteOption.cs:49`) removable via vanilla
  **`Hook.ModifyRestSiteOptions`** — no Harmony needed.
- Potions: no player-count filtering exists. Events: only `DenseVegetation` is count-gated;
  several events *branch* on count (FakeMerchant, TheArchitect, WarHistorianRepy) — review
  during the events design pass. `NEventRoom.SetDescription` injects an `IsMultiplayer`
  loc variable (event text will read multiplayer-flavored; cosmetic).
- Untraced: `ActModel.GenerateRooms(rng, unlockState, isMultiplayer)` bool's internal
  effects; `RelicGrabBag._mpFallbackDequeue` population.

### Boss swap in co-op — the one real netcode build
- Boss rolled once at run setup from shared `State.Rng.UpFront`
  (`ActModel.GenerateRooms` :277, `RunManager.GenerateRooms` :507-536) — deterministic,
  no sync message exists. Read late via `RoomSet.NextBossEncounter` (SecondBoss handling
  :70). A swap applied before entering the boss room takes effect, **survives map
  regeneration and act transitions** (rooms are never re-rolled; `ActChangeSynchronizer`
  only gates readiness and bumps the act).
- **`MapCmd.SetBossEncounter` is 100% local** (only caller of `ActModel.SetBossEncounter`;
  mutates per-client `RunState.Act` + local UI refresh). One-sided application ⇒ divergent
  per-client saves.
- Build: custom synchronized `GameAction` + `INetAction` carrying the boss `ModelId`
  (mirror the `VoteForMapCoordAction`/`NetVoteForMapCoordAction` pattern), host-
  authoritative (vanilla convention: host resolves, broadcasts), enqueued via
  `ActionQueueSynchronizer.RequestEnqueue`; both clients apply `MapCmd.SetBossEncounter`
  in `ExecuteAction`. Mod net-action registration is confirmed available
  (`ActionTypes.cs` reflects over mods). Existing `BossVotePatch` UI/swap/refresh logic
  transplants; only the application step changes.

---

## 6. Player override of Builder's card pick (once per act)

- Insert a stage into the mod's own pick protocol, before the single synchronized
  submission (no vanilla protocol change, no desync surface):
  1. Builder picks → pick arrives at the Player's client, **not yet submitted**.
  2. While the Player has an override left, the reward screen renders on BOTH machines —
     interactive for the Builder, **read-only for the Player** with the Builder's pick
     highlighted, plus "Override (N left)". Short auto-accept timeout (~10s) so zero-
     override runs cost nothing. (Full-visibility requirement from Tristan, call 2 —
     Player can't judge an override without seeing all options. Data is already mirrored;
     cost is UI only.)
  3. Accept/timeout → Builder's index submitted. Override → screen unlocks for the Player,
     their index submitted, budget decremented.
- Once spent: collapse the Player's side to a "Builder picked X" toast (cosmetic choice).
- Budget: straight reuse of `VoteOverrideBudget` (`src/Game/DecisionVotes/`,
  spend-only-on-success semantics); reset hooked to act transition (`EnterNextAct`).
- Broadcast bonus: the Player's stream shows "will he override?" drama in real time.
- Open question for Tristan: does once-per-act cover **all** Builder decisions (blessings,
  boss) or card rewards only? The budget machinery is shared either way.

---

## 7. Tiebreak: damage race vs target dummy

### Vanilla already ships the fight
- `Models.Events/BattlewornDummy.cs` — "punch the dummy" event: pick 1 of 3 difficulty
  settings (`BattleFriendV1/V2/V3` monsters), `EnterCombatWithoutExitingEvent` into
  `BattlewornDummyEventEncounter`, resume after with reward/defeat text via
  `RanOutOfTime`.
- `Models.Powers/BattlewornDummyTimeLimitPower.cs` — **existing turn-limit power**:
  counter decrements each turn-end; at zero sets `encounter.RanOutOfTime = true` and the
  dummy escapes (`CreatureCmd.Escape`). This is the X-turn clock, done.
- Delta for "damage dealt in X turns": clone the encounter with an effectively-unkillable
  high-HP dummy; count damage (dummy HP lost, or a tracking power). Deterministic; display
  the total big at the end for broadcast.

### Trigger model (decided call 2 — Tristan)
- **NOT at every run end** (players won't try unless it matters). Fires only when a tie is
  declared, after both runs conclude.
- **Capture (invisible, every run)**: at run end (win or loss), silently snapshot the final
  build — copy/serialize the run state before the game discards it (the save system fully
  serializes runs; `SerializableRunState`/run-history data). Players never know if it'll
  be used → no incentive distortion.
- **Fight (on demand)**: a menu-launched "Tiebreak" mode that rehydrates the snapshot and
  enters the dummy arena directly. **Prior art: Landmaster/Hindsight**
  (github.com/Landmaster/Hindsight — C#, BaseLib-StS2 dependency; replays runs from
  retained run-history data, re-entering the fight you died at; retains last 2 runs by
  default; replays don't save). Read its source before building the rehydration.
- **No netcode**: player-only damage, so it runs as a LOCAL SOLO fight — no lobby, no
  synchronizers, no checksum exposure. Identical deterministic dummy (fixed seed, fixed
  turns) ⇒ fair by construction.
- **Simultaneous start**: commentator countdown on the broadcast — deliberately not
  mechanized ("the countdown is a feature, not a compromise"). A ready-sync is buildable
  later if wanted.

### Rules decisions (provisional)
- **Potions: INCLUDED** (Surfinite, call 2, pending Tristan's confirmation). Reasoning: an
  unspent potion is either a mistake or a strategic hold — both are information about the
  player; the both-cleared-the-final-fight case makes hoarding unambiguous discipline.
  Excluding would be extra work (stripping at rehydration); including is default.
- **Stateful relic charges**: snapshot-as-is by the same logic; get Tristan to bless "the
  snapshot is literally your final state — potions, charges and all" as the written rule
  to close the whole category.

---

## 8. Consolidated build inventory

### Vanilla handles it / near-free
- Mirrored display of the Player's rewards/events/relics on the Builder's machine.
- Cross-player (ally-targeted) potions; custom potion models.
- Simultaneous turns (Builder acts during Player's turn).
- Downed-player handling; run ends when all dead.
- Empty-deck safety.
- 4-player lobby cap fits 2×(player+builder).
- Dummy fight + turn-limit power for the tiebreak.
- Mod-defined `INetMessage`/`INetAction`/models with stable wire ids.
- Mod-list handshake = tournament version enforcement for free.

### Harmony surgery on mapped seams
- Card-reward authority swap (`CardReward.OnSelect` `IsMe` fork + choice-id bookkeeping) —
  **PoC risk #1**.
- Ancient/Neow builder-pick (`ChooseLocalOption` funnel + one mod message; auto-resolve
  Builder's copy).
- Solo scaling (HP/block seams + PowerCmd gate + enumerated one-offs).
- Random-target filtering (`MonsterModel.PerformMove`) + Builder godmode
  (`CreatureCmd.Damage`).
- Kill-Builder-on-Player-death.
- Chest: de-solo-gate `ChestRelicChoicePatch`, auto-skip Builder, rework refund
  bookkeeping; Builder potion offer via `WithRewardsFromRoom` + `LinkedRewardSet`.
- MP-only card removal (2 seams) + MassiveScroll suppression + SP-relic re-enable +
  rest-site hook.
- Map lockout (local click gating) + mirror-vote (map + shared events).
- Override window (mod-side protocol stage + `VoteOverrideBudget` reuse).
- Builder reward suppression / relic-strip / no combat cards.

### Must build
- Boss-swap synchronized action (host-authoritative, `VoteForMapCoordAction` pattern).
- Role assignment metadata (custom lobby-adjacent `INetMessage`).
- Builder viewpoint UI ("showing Player's rewards" screens; spectate view — TeammateSpectate
  prior art).
- Tiebreak snapshot-at-run-end + rehydrate-into-dummy-arena (Hindsight prior art) + damage
  counter + results display.
- Sabotage potion content (the fun part — design with Tristan).

---

## 9. Risks

1. **Choice-ID desync** when swapping reward authority (`ValidateChoiceId` throw /
   `WaitForRemoteChoice` hang). Burn down first in PoC.
2. **ChecksumTracker is unforgiving** — divergence abandons the run and Sentry-reports to
   MegaCrit. All mutations inside synchronized paths; never touch shared RNG out-of-band;
   keep mod logic in the presentation/authority layer.
3. **Beta churn**: the game updates frequently and the handshake pins exact versions — someone
   must own "everyone is on build X" for tournament day; every game update needs a compat
   pass (see CLAUDE.md decompile-diff workflow). This is an ongoing cost to price in.
4. Sender-spoofing hole may be patched by MegaCrit — use explicit mod messages, never rely
   on spoofing.
5. `AfterRewardTaken` async-resolution assumption (Bossy Relics landmine) applies to the
   reused `LinkedRewardSet` machinery — recheck each game update.

## 10. Verify-live checklist (before/at PoC)

- [ ] Choice-id counters stay in lockstep across an authority-swapped reward (2 machines).
- [ ] `TreasureRoom.DoExtraRewardsIfNeeded` / chest-open: per-client single creation of the
      injected potion `RewardsSet` (watch `TreasureChestOpenedMessage`).
- [ ] Whether concrete `RunState` overrides the `CardMultiplayerConstraint` default
      interface property (changes the Harmony target).
- [ ] `NetTypeCache`/mod type-id ordering identical across clients.
- [ ] `Hook.AfterHandEmptied` listeners' behavior with a card-less Builder.
- [ ] Event/relic synchronizer stalls with an auto-resolved Builder (beyond map, which is
      solved by mirror-vote).
- [ ] `ActModel.GenerateRooms` `isMultiplayer` bool: what it changes.
- [ ] `RelicGrabBag._mpFallbackDequeue` population.
- [ ] Hindsight source read (GitHub first, per project convention) before building
      rehydration.
- [ ] TeammateSpectate source located (GitHub first) for spectate-view prior art.

## 11. Open questions for Tristan

- Override scope: cards only, or all Builder decisions? Budget shared either way.
- Tiebreak rules blessing: "snapshot is literally your final state — potions, charges and
  all" (provisional yes on potions).
- Event handling design (Builder role in events; the 8 shared events).
- Sabotage potion roster + how mean is too mean (content design session).
- Tournament ops: who owns version pinning; scoring admin; match scheduling.
- Win condition details (furthest floor? boss kills? timing?) — out-of-band but shapes the
  snapshot trigger.

## 12. PoC scope (~1 month target, promotable)

Two real machines in a co-op lobby:
1. Role assignment (who is Builder) — even hardcoded/config-file is fine for PoC.
2. Builder inert: empty deck, godmode, reward-suppressed.
3. **Card-reward authority swap end-to-end** (the architectural risk).
4. One sabotage potion thrown mid-combat at the Player.
5. Solo HP scaling (the one-seam version) so the demo *feels* right.

Everything else (chests, boss, override, map lockout, MP-card removal, tiebreak, events) is
additive after the PoC proves the swap. Tournament at ~2 months.
