# AbstractModel hook surface vs v0.1 votes

Source: `decompiled/sts2/MegaCrit/sts2/Core/Models/AbstractModel.cs` (1,038
lines, ~200 virtual methods).

> **Stable-branch drift (verified 2026-05-08).** `AbstractModel` had real
> signature changes between beta and stable. The note's hook *names* are
> still valid (we never documented signatures), but if you reach for these
> hooks, use the stable signatures, not the beta ones:
>
> | Method | Beta signature | Stable signature |
> |---|---|---|
> | `AfterAttack` | `(PlayerChoiceContext, AttackCommand)` | `(AttackCommand)` |
> | `AfterCardGeneratedForCombat` | `(CardModel, Player?)` | `(CardModel, bool addedByPlayer)` |
> | `AfterPowerAmountChanged` | `(PlayerChoiceContext, PowerModel, decimal, Creature?, CardModel?)` | `(PowerModel, decimal, Creature?, CardModel?)` |
> | combat-state params | `ICombatState` | `CombatState` (interface deleted) |
>
> **Removed entirely**: `AfterAutoPostPlayPhaseEntered`,
> `AfterAutoPrePlayPhaseEntered{,Early,Late}` (the four "auto-play-phase"
> callbacks). **Added**: `BeforePlayPhaseStart{,Late}(PlayerChoiceContext,
> Player)`. The `ICombatState` / `NullCombatState` / `PlayerTurnPhase`
> types were deleted from `Combat/`; use the concrete `CombatState`
> directly. This affects any v0.2+ combat-side hooks but doesn't change the
> v0.1 Harmony-heavy plan in this doc.

## What AbstractModel actually is

```csharp
public abstract class AbstractModel : IComparable<AbstractModel>
{
    public ModelId Id { get; }
    public bool IsMutable { get; private set; }
    public bool IsCanonical => !IsMutable;
    public abstract bool ShouldReceiveCombatHooks { get; }
    public event Action<AbstractModel>? ExecutionFinished;
    // ~200 virtual hook methods follow
}
```

Subclasses are the game's content (cards, relics, powers, monsters, etc.).
There's a canonical/instance distinction: `IsMutable=false` is the blueprint,
`IsMutable=true` is a runtime instance. Every runtime instance gets a chance
to react to game events via the virtual methods, called by `Core/Hooks/Hook`.

## The four method shapes

The virtual methods fall into four categories:

1. **Observation**: `BeforeX(...)` / `AfterX(...) -> Task` — fired for awareness.
   Cannot change game state directly, but can issue commands during them.
2. **Transformation**: `ModifyX(...) -> T` — receives a value, returns a
   modified value. Multiple models stack (the result of one feeds the next).
3. **Optional transformation**: `TryModifyX(..., out T) -> bool` — returns
   true to apply, false to pass through. Useful when most models don't care.
4. **Predicate**: `ShouldX(...) -> bool` — vetos/allows. Often "all models must
   return true" or "any model returning false blocks".

**None of these "click for the player".** The hook surface is for *content
mods that participate in the game's state machine*, not for *intercepting
the user's input handling*. That's Harmony territory.

## The verdict for v0.1: Harmony-heavy with selective AbstractModel use

| v0.1 vote | Substitutable via AbstractModel? | Best AbstractModel hooks (if any) | Plan |
|---|---|---|---|
| **Neow blessing** | No | none specific to Neow | **Harmony-patch** the Neow event class |
| **Card reward pick** | Partial | `BeforeRewardsOffered`, `TryModifyRewards`, `TryModifyCardRewardOptions` | Harmony to substitute the click; AbstractModel to pre-mod the offered cards if we ever want chat to influence the *pool* |
| **Event choice** | No | `ModifyNextEvent` modifies which event runs, not options-within-event | **Harmony** the event dialog/option-dispatch |
| **Boss reward pick** | Partial | same as card reward | **Harmony** for the click; AbstractModel auxiliary |
| **Shop purchase** | No | many Modify* methods exist for shop *contents* and prices, but not for which item the player picks | **Harmony** the shop confirm/purchase path |
| **Map path** | No | `ModifyGeneratedMap` modifies the map shape, not the click on a node | **Harmony** the map-node selection |
| **Sealed deck** (if v0.1) | YES | `ShouldAddToDeck`, `TryModifyCardBeingAddedToDeck`, `BeforeCardRemoved` | AbstractModel handles the deck construction cleanly; Harmony only for the custom selection UI |

So our v0.1 architecture splits cleanly into two layers:

1. **Harmony patch layer** — intercepts player input at each choice point and
   substitutes chat's vote. ~6 patches for the six votes, each fairly small.
2. **AbstractModel layer (optional for v0.1)** — for sealed deck if we
   include it, plus any auxiliary effects we want chat to have on the run.

The original StS1 mod used the same pattern (`SpirePatch` is the Harmony-
equivalent for ModTheSpire). So the high-level architecture transfers; only
the API names change.

## Notable hooks in case useful later

These don't map to v0.1 votes but are useful context for stretch goals:

**Sealed-deck-relevant** (most promising AbstractModel hooks):
- `ShouldAddToDeck(CardModel) -> bool` — block automatic deck additions.
- `TryModifyCardBeingAddedToDeck(CardModel, out CardModel?) -> bool` —
  substitute one card for another at deck-add time.
- `TryModifyCardBeingAddedToDeckLate(...)` — same, runs after the early one.
- `AfterAddToDeckPrevented(CardModel) -> Task` — observation when blocked.

**Combat hooks** (for chat-as-monster-style features in v0.2+):
- `BeforeCombatStart`, `BeforeCombatStartLate`, `AfterCombatEnd`
- `BeforeAttack`, `AfterAttack`, `BeforeDamageReceived`, etc.
- `AfterCardPlayed`, `BeforeCardPlayed`, `AfterCardDrawn`
- `BeforePotionUsed`, `AfterPotionUsed`
- `AfterPowerAmountChanged`, `BeforePowerAmountChanged`

**Map / room navigation** (might be useful for future map-vote refinement):
- `BeforeRoomEntered(AbstractRoom)`, `AfterRoomEntered(AbstractRoom)`
- `ModifyGeneratedMap(IRunState, ActMap, int)`,
  `ModifyGeneratedMapLate(IRunState, ActMap, int)`
- `AfterMapGenerated(ActMap, int)`
- `ShouldProceedToNextMapPoint() -> bool`
- `ModifyUnknownMapPointRoomTypes(IReadOnlySet<RoomType>)` — manipulate the
  question-mark-room outcomes

**Shop** (most are content-modify, no click-substitution):
- `ModifyMerchantPrice(Player, MerchantEntry, decimal)`
- `ModifyMerchantCardPool(Player, IEnumerable<CardModel>)`
- `ModifyMerchantCardCreationResults(Player, List<CardCreationResult>)`
- `ModifyMerchantCardRarity(Player, CardRarity)`
- `ShouldRefillMerchantEntry(MerchantEntry, Player)`
- `AfterItemPurchased(Player, MerchantEntry, int)`

**Rewards generally**:
- `BeforeRewardsOffered(Player, IReadOnlyList<Reward>)` — fires for any
  reward room (combat, boss, etc.), might be a unified chat-vote trigger.
- `TryModifyRewards(Player, List<Reward>, AbstractRoom?)` — late-stage modify.
- `AfterRewardTaken(Player, Reward)` — observation only.
- `ModifyCardRewardCreationOptions(Player, CardCreationOptions)`

## Where to find each Harmony patch target

For each v0.1 vote, we need to identify the specific game class+method to
patch. Educated guesses based on namespace inspection:

| Vote | Likely classes to patch |
|---|---|
| Neow blessing | something in `Core.Events` or `Core.Rooms` matching "Neow" — search the decompile next session |
| Card reward | `Core.Rewards` — `CardReward`, `RewardItem`, etc. or the screen class in UI |
| Event choice | `Core.Events` — event base class, maybe `EventDialog`'s option-pick path |
| Boss reward pick | `Core.Rewards` — boss-relic-specific class |
| Shop purchase | `Core.Entities.Merchant` — `MerchantEntry` purchase confirm |
| Map path | `Core.Map` — map node click handler |

These are starting points. Confirming each is a small task (grep for the
class names in the decompile) but better done at design time than now.

## Implications for v0.1 design

1. **Confirmed**: monolithic mod is the right architecture. We need control
   over both Harmony patch placement and the IRC/voting infrastructure.
2. **Initial mod skeleton** (when we get to it):
   ```csharp
   [ModInitializer("Initialize")]
   public static class StreamerMod {
       public static void Initialize() {
           // 1. Start IRC client
           // 2. Initialize vote engine
           // 3. Register Harmony patches manually (or rely on auto-PatchAll)
           // 4. Register AbstractModel(s) via RunHookSubscriptionDelegate
           //    if we use the model layer
       }
   }
   ```
3. **Sealed deck question**: if we keep it in v0.1, AbstractModel makes that
   part *cleaner*, but the custom card-selection UI is still substantial
   work. Defer the decision to design phase.
4. **Per-vote test plan**: each vote is a Harmony patch + IRC consumer. We
   can test each independently with a fake IRC source feeding canned
   messages. Great for rapid iteration.

## Registration: closed (it's `ModHelper`)

Found in `Core/Modding/ModHelper.cs`. Three public static APIs:

```csharp
// Add new content (cards/relics/events) to a canonical pool.
// Must be called BEFORE the game first reads that pool — the pool freezes.
public static void AddModelToPool<TPoolType, TModelType>();
public static void AddModelToPool(Type poolType, Type modelType);

// Register a delegate that returns models per run / per combat.
// IDs are unique per subscriber (duplicate ID = ignored with error log).
// Subscribers are sorted by id for deterministic call order.
public static void SubscribeForRunStateHooks(string id, RunHookSubscriptionDelegate del);
public static void SubscribeForCombatStateHooks(string id, CombatHookSubscriptionDelegate del);
```

Internally: when `Hook.AfterX(runState, ...)` fires, it iterates
`runState.IterateHookListeners(...)` which (presumably) chains the game's
built-in listeners with `ModHelper.IterateAllRunStateSubscribers(runState)`.
Modded models get all the same callbacks as game content. Confirmed: this
is a first-class, peer-of-the-game mechanism.

**Concrete usage pattern** for our mod:

```csharp
[ModInitializer("Initialize")]
public static class StreamerMod {
    public static void Initialize() {
        ModHelper.SubscribeForRunStateHooks("slay_the_streamer_2", runState =>
            new AbstractModel[] { new ChatVotingRunModel(/* deps */) });
        // Harmony patches in this assembly auto-applied via PatchAll fallback,
        // OR call new Harmony("...").PatchAll(...) here for explicit control.
    }
}
```

## Still unread but lower priority

- `STS2FirstMod`'s README and project files (option 2 from earlier — would
  show concrete `[ModInitializer]` usage and manifest example).
- The remaining 7 files in original StS1 mod (boss-vote subsystem mainly).
- The specific game classes we'll Harmony-patch (Neow, card reward screen,
  shop confirm, etc.). Need a focused decompile-search session.
