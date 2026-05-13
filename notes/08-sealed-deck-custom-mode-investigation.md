# Sealed-deck (vanilla Custom Mode) — research findings

**Date**: 2026-05-12
**Status**: Research findings only. Not a spec, not a plan, not a design. Promotes to a real spec under `docs/superpowers/specs/` if/when v0.2's sealed-deck-mode is scoped.
**Motivation**: The v0.2+ "sealed-deck draft start" entry in [notes/06-followups-and-deferred.md](06-followups-and-deferred.md) (search for "Sealed-deck draft start") says FrostPrime's Discord community claims StS2 already has a sealed-deck function in vanilla's Custom Mode. A community member: *"using the sealed deck function doesn't give you a regular pick-1-of-3 Neow bonus"*. If true, our future sealed-deck-mode might piggyback on vanilla's UI rather than building a draft screen from scratch. This file captures what vanilla actually does so design choices on top of it have a concrete base to push against.

## TL;DR

- **Confirmed**: vanilla StS2 has a `SealedDeck` modifier in Custom Mode. Selecting it makes the Neow event present a single option ("Sealed Deck") that opens a 30-card grid; the streamer picks **N=10 from M=30**. There is no separate "sealed deck" toggle outside of modifiers — it's just one of the standard Good Modifiers list.
- **N=10 and M=30 are hardcoded literals** in `SealedDeck.ChooseCards`. They are not fields, not constants, not exposed via any public API. Modifying them requires either a Harmony transpiler or replacing the whole method via a prefix that returns `false`.
- **The "no Neow bonus" behavior is NOT a `GameMode.Custom` side effect.** It comes from `Neow.GenerateInitialOptions` swapping branches on `RunState.Modifiers.Count > 0`. The community member's claim that it's tied to "custom mode" is intuitive but technically slightly wrong — it's the modifier presence that gates Neow, not the game mode.
- **Viability verdict for v1: use vanilla as-is is realistic.** Streamer selects Custom Mode → ticks `SealedDeck` → embarks → drafts 10 of 30 → run continues, our existing B.2.1 patches still apply. **Caveat documented as known limitation**: `Bodyguard` and `Unleash` are `CardRarity.Basic`, and Basic-rarity cards never roll in `RegularEncounter` odds — so Necrobinder's identity cards are absent from the draft pool. A community member's Discord claim ("Necrobinder is unplayable without Bodyguard/Unleash") is one perspective; a 30-card draft with a skilled player (FrostPrime) gives enough flexibility to build a working deck around the rest of the pool. The structural finding is real but the gameplay conclusion is not "unplayable" — it's "Necrobinder feels different / harder in sealed-deck mode". Note it, don't gate v1 on it.
- **Cost of riding vanilla**: achievements + epoch unlocks + ascension progression + win/loss/streak stats are all suppressed for the entire run (and for every Daily/Custom run in general). The streamer's account stats are unaffected by the run. This is intentional vanilla behavior and matches the "show, not record-attempt" intent.
- **Custom Mode is locked until 3 standard wins** (Surfinite's knowledge from playing the game). FrostPrime has it unlocked; a fresh modded save profile would not. Likely needs the same kind of "unlock-everything-on-mod-load" treatment as the existing onboarding gaps for Neow / unmodded save progression.

## Vanilla implementation

### The modifier itself

[`decompiled/sts2/MegaCrit/sts2/Core/Models/Modifiers/SealedDeck.cs`](../decompiled/sts2/MegaCrit/sts2/Core/Models/Modifiers/SealedDeck.cs) — 54 lines, single class.

- `SealedDeck : ModifierModel` ([SealedDeck.cs:19](../decompiled/sts2/MegaCrit/sts2/Core/Models/Modifiers/SealedDeck.cs#L19))
- `ClearsPlayerDeck => true` ([SealedDeck.cs:21](../decompiled/sts2/MegaCrit/sts2/Core/Models/Modifiers/SealedDeck.cs#L21)) — vanilla `ModifierModel.OnRunCreated` then calls `player.Deck.Clear()` per [ModifierModel.cs:60-67](../decompiled/sts2/MegaCrit/sts2/Core/Models/ModifierModel.cs#L60-L67) before the modifier runs anything. So by the time the draft fires, the character has zero cards.
- `GenerateNeowOption(EventModel)` ([SealedDeck.cs:23-26](../decompiled/sts2/MegaCrit/sts2/Core/Models/Modifiers/SealedDeck.cs#L23-L26)) returns `() => ChooseCards(eventModel.Owner)`. This is how it hooks into the Neow event (see "Neow-replacement mechanics" below).
- `ChooseCards(Player player)` ([SealedDeck.cs:28-44](../decompiled/sts2/MegaCrit/sts2/Core/Models/Modifiers/SealedDeck.cs#L28-L44)) is the actual draft method.

### The N and M values

From [SealedDeck.cs:28-44](../decompiled/sts2/MegaCrit/sts2/Core/Models/Modifiers/SealedDeck.cs#L28-L44):

```csharp
CardCreationOptions options = new CardCreationOptions(
    new ReadOnlySingleElementList<CardPoolModel>(player.Character.CardPool),
    CardCreationSource.Other,
    CardRarityOddsType.RegularEncounter
).WithFlags(CardCreationFlags.NoUpgradeRoll | CardCreationFlags.ForceRarityOddsChange);

IEnumerable<CardCreationResult> source =
    CardFactory.CreateForReward(player, 30, options).ToList();  // M = 30

CardSelectorPrefs prefs = new CardSelectorPrefs(
    new LocString("modifiers", "SEALED_DECK.selectionPrompt"),
    10                                                          // N = 10
);
prefs.Cancelable = false;
prefs.RequireManualConfirmation = true;
prefs.Comparison = CompareCards;

List<CardModel> cards = (await CardSelectCmd.FromSimpleGridForRewards(
    new BlockingPlayerChoiceContext(), source.ToList(), player, prefs
)).ToList();

CardCmd.PreviewCardPileAdd(await CardPileCmd.Add(cards, PileType.Deck), 1.2f, CardPreviewStyle.GridLayout);

// Then: scrub PandorasBox from all grab bags (see "Side effects" below)
```

**Both literals are inlined into the method body.** `30` is the second arg to `CardFactory.CreateForReward`; `10` is the second arg to the `CardSelectorPrefs` constructor.

Community memory said "10 of 30" — confirmed exactly.

### The card pool source

The pool is `player.Character.CardPool` — a single `CardPoolModel` from the character's standard pool. `CardCreationSource.Other` + `CardRarityOddsType.RegularEncounter` rarity weights. The two flags:

- `NoUpgradeRoll` — drafted cards are not pre-upgraded.
- `ForceRarityOddsChange` — combined with the single-character card pool, this forces the standard regular-encounter rarity table (common/uncommon/rare distribution as if a normal combat reward).

`CardPool` filtering pipeline ([CardPoolModel.cs:55](../decompiled/sts2/MegaCrit/sts2/Core/Models/CardPoolModel.cs#L55)): `GetUnlockedCards` filters out cards not yet unlocked by the player's progress AND filters by multiplayer constraint (`MultiplayerOnly` etc.). Curses are not in the character's CardPool to begin with (they live in separate pools), so curses won't appear in the draft. Status cards (Wound, Slimed, etc.) likewise live in other pools.

The pool is **unfiltered beyond standard pool/unlock/rarity rules**. There is no special "sealed-deck-only" filter or augmentation; what you see is what the character would normally see in a combat-reward grab.

### The selection screen

`CardSelectCmd.FromSimpleGridForRewards` ([CardSelectCmd.cs:172](../decompiled/sts2/MegaCrit/sts2/Core/Commands/CardSelectCmd.cs#L172)) opens the "simple grid" card selector. **This is a different screen class from `NCardRewardSelectionScreen`** (the one our B.2.1 card-reward-vote patches hook). The grid selector shows N cards laid out in a grid and lets you pick K according to `CardSelectorPrefs.SelectionLimit`. `Cancelable = false` means the streamer cannot escape out of it; `RequireManualConfirmation = true` means a Confirm button appears once the cap is reached.

Cards are sorted by `CompareCards` ([SealedDeck.cs:46-53](../decompiled/sts2/MegaCrit/sts2/Core/Models/Modifiers/SealedDeck.cs#L46-L53)): first by Rarity ascending (Common → Uncommon → Rare), then alphabetically by Title under the current locale.

### How the run-launch wires the modifier in

[`decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/CustomRun/NCustomRunScreen.cs`](../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/CustomRun/NCustomRunScreen.cs):

- The Custom Run screen builds a `StartRunLobby(GameMode.Custom, ...)` ([NCustomRunScreen.cs:244](../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/CustomRun/NCustomRunScreen.cs#L244) for singleplayer).
- `_modifiersList.GetModifiersTickedOn()` ([NCustomRunModifiersList.cs:158](../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/MainMenu/NCustomRunModifiersList.cs#L158)) returns the list of ticked `ModifierModel` instances.
- On Embark, `StartNewSingleplayerRun(seed, acts, modifiers)` ([NCustomRunScreen.cs:401-408](../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/CustomRun/NCustomRunScreen.cs#L401-L408)) calls `NGame.Instance.StartNewSingleplayerRun(...)` with `GameMode.Custom` and the modifier list.
- Modifiers end up on `RunState.Modifiers`. `OnRunCreated` runs for each ([ModifierModel.cs:56-67](../decompiled/sts2/MegaCrit/sts2/Core/Models/ModifierModel.cs#L56-L67)), which clears the player's starter deck because `SealedDeck.ClearsPlayerDeck == true`.

### The modifiers list

[`NCustomRunModifiersList.cs:112-130`](../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/MainMenu/NCustomRunModifiersList.cs#L112-L130) builds the visible tickboxes from `ModelDb.GoodModifiers.Concat(ModelDb.BadModifiers)`. Both are static, hardcoded arrays in [`ModelDb.cs:203-232`](../decompiled/sts2/MegaCrit/sts2/Core/Models/ModelDb.cs#L203-L232):

- **Good modifiers** (9): `Draft, SealedDeck, Hoarder, Specialized, Insanity, AllStar, Flight, Vintage, CharacterCards`
- **Bad modifiers** (7): `DeadlyEvents, CursedRun, BigGameHunter, Midas, Murderous, NightTerrors, Terminal`
- **Mutually exclusive** ([`ModelDb.cs:227-232`](../decompiled/sts2/MegaCrit/sts2/Core/Models/ModelDb.cs#L227-L232)): `SealedDeck`, `Draft`, and `Insanity` are in one exclusive set — ticking one unticks the others. (`CharacterCards` and the other modifiers can stack freely with `SealedDeck` as far as the mutual-exclusion table is concerned.)

## Modifiability surface

What's patchable, what's not.

| Surface | Mutability |
|---|---|
| `SealedDeck.ChooseCards`'s `30` (pool size) and `10` (pick count) | Hardcoded literals inside the method body. No public/private field, no const. **Harmony transpiler** can rewrite the IL operand; **Harmony prefix returning `false`** can fully replace the method; nothing simpler. |
| `SealedDeck` modifier itself | Public class, public methods. Subclassable (it inherits `ModifierModel`) but `ModelDb.GoodModifiers` references it by type, so simply subclassing won't replace it in the modifier list — you'd have to either patch `ModelDb.GoodModifiers` or patch `SealedDeck.ChooseCards` directly. |
| `player.Character.CardPool` filter (e.g., must-include cards) | The card pool source is `player.Character.CardPool` (read-only `CardPoolModel`). Augmenting the pool from outside requires patching `SealedDeck.ChooseCards` to inject extra cards before/after `CardFactory.CreateForReward`. Filtering the pool (e.g., remove specific cards) requires the same. There is no per-modifier configuration interface. |
| `CardCreationOptions` flags (`NoUpgradeRoll`, `ForceRarityOddsChange`) | Inlined into `ChooseCards`. Same constraint as above. |
| Sort order in the picker | `CompareCards` is private static. Replaceable via Harmony prefix returning `false` and a substitute, or via `CardSelectorPrefs.Comparison` if we replace the whole method. |
| `ModelDb.GoodModifiers` / `BadModifiers` lists | Static read-only `IReadOnlyList<ModifierModel>` properties — backed by inline-allocated array expressions. Cannot be appended-to or replaced at runtime without patching the property getter directly. |
| `ModelDb.MutuallyExclusiveModifiers` | Same as above — single hardcoded set. |
| `NCustomRunModifiersList` UI | A Control with `_modifierTickboxes` private list, populated on `_Ready`. To add a custom modifier visibly, we'd have to either inject into the model layer (above) or patch `_Ready` to add an extra tickbox. The signal `ModifiersChanged` plus `GetModifiersTickedOn` is reasonably hook-friendly. |

**Bottom line on modifiability**: keeping vanilla N=10 / M=30 / standard character pool is easy (no code). Anything else requires either method-replacement Harmony patches or transpilers, neither of which we have today in the mod.

## Neow-replacement mechanics

This is the most important section for future polish work.

### The actual gate

The gate is in [`Neow.GenerateInitialOptions`](../decompiled/sts2/MegaCrit/sts2/Core/Models/Events/Neow.cs#L213-L296):

```csharp
protected override IReadOnlyList<EventOption> GenerateInitialOptions()
{
    if (base.Owner.RunState.Modifiers.Count <= 0)
    {
        // ... build normal "1 curse from random + 2 positive options" pool ...
        return new ReadOnlyList<EventOption>(list3);   // 3-element list
    }

    foreach (ModifierModel modifier in base.Owner.RunState.Modifiers)
    {
        Func<Task> neowOption = modifier.GenerateNeowOption(this);
        if (neowOption != null)
        {
            int optionIndex = ModifierOptions.Count;
            ModifierOptions.Add(new EventOption(
                this,
                () => OnModifierOptionSelected(neowOption, optionIndex),
                modifier.NeowOptionTitle,
                modifier.NeowOptionDescription,
                modifier.Id.Entry,
                modifier.HoverTips.ToArray()
            ));
        }
    }
    if (ModifierOptions.Count > 0)
    {
        return new ReadOnlySingleElementList<EventOption>(ModifierOptions[0]);  // 1-element list
    }
    return Array.Empty<EventOption>();
}
```

Three branches:

1. **No modifiers** → normal "pick 3 of (1 curse + 2 positive)" options.
2. **Modifiers exist AND at least one overrides `GenerateNeowOption`** → return a single-element list `[ModifierOptions[0]]`. The streamer sees one option, clicks it, it runs the modifier's draft Task.
3. **Modifiers exist but none override `GenerateNeowOption`** → `Array.Empty<EventOption>()`. The Neow event has no options at all. This is the case for modifiers like `Hoarder` or `CursedRun` that don't override `GenerateNeowOption`.

After the first modifier option is selected, `OnModifierOptionSelected` ([`Neow.cs:298-309`](../decompiled/sts2/MegaCrit/sts2/Core/Models/Events/Neow.cs#L298-L309)) advances to `ModifierOptions[index + 1]` if there is one — so multiple modifiers each contribute their option in sequence. With only `SealedDeck`, it's one option, then the event ends.

### Which modifiers override `GenerateNeowOption`

From grepping the decompile, modifiers that override `GenerateNeowOption` (non-null return):

- `SealedDeck` — opens the 10-of-30 grid.
- `Draft` — offers 10 sequential `pick-1-of-3` reward screens (a separate "draft" UX from sealed-deck). `Draft` ([Draft.cs:11](../decompiled/sts2/MegaCrit/sts2/Core/Models/Modifiers/Draft.cs#L11)) is mutually exclusive with `SealedDeck` per `MutuallyExclusiveModifiers`.
- `Specialized`, `Insanity`, `AllStar` — also override (per the grep), each with their own pre-run setup. Not investigated in detail; relevant only because they'd compete for the Neow slot if ever stacked.

### The intervention point for "chat picks Neow first, then sealed-deck draft"

The user note in [notes/06](06-followups-and-deferred.md) says: *"Chat picks the Neow bonus FIRST, then streamer drafts the sealed deck — order matters so chat can't sandbag a deck commitment with a 'remove 2 cards' Neow pick."*

To make this work on top of vanilla, the cleanest patch target is `Neow.GenerateInitialOptions`. The patch shape:

1. **Prefix or postfix** on `GenerateInitialOptions`. If modifiers are present AND any have `GenerateNeowOption != null`, build BOTH the normal-Neow pick-3 list AND the modifier list, and return a composite event sequence: first the pick-3 (which is what our existing `NeowBlessingVotePatch` already chat-votes on), then chain into the modifier's option.
2. Vanilla's `ModifierOptions` chaining via `OnModifierOptionSelected` already handles "one selection completes, advance to next". The patch is essentially: prepend a normal-Neow pick-3 to the `ModifierOptions` queue and let vanilla's sequencing carry through.

This is consistent with the B.2.2 readiness note in [notes/06](06-followups-and-deferred.md): *"B.2.2 likely collapses to predicate-widening on NeowBlessingVotePatch.IsNeowEvent"*. Sealed-deck "polish" (chat Neow first, then draft) is in the same family — predicate-widening + small option-list intervention on the Neow event.

Surfinite tagged this as *"polish that may never happen"*; no spec required from this investigation.

### What the community member's "doesn't give you a Neow bonus" claim really means

The community claim is imprecise but practically correct: in vanilla, if you tick `SealedDeck` in Custom Mode, the Neow event becomes a single non-bonus "Sealed Deck" option (which IS the modifier kickoff), not the usual pick-3 bonus. So yes — the streamer doesn't see the relic-grant bonus. But the gate is on `RunState.Modifiers.Count > 0`, not on `GameMode == Custom`. The two coincide only because the Custom Mode UI is the (currently) only path to populate `RunState.Modifiers`.

## Other custom-mode side effects

These come from `GameMode.Custom` (and most apply to `GameMode.Daily` too, since the predicate is `GameMode != Standard`).

| Effect | Source | Notes |
|---|---|---|
| **Achievements + epochs locked** | [`GameModeExtension.cs`](../decompiled/sts2/MegaCrit/sts2/Core/Runs/GameModeExtension.cs) defines `AreAchievementsAndEpochsLocked() => gameMode != GameMode.Standard`. Used by `NTopBar.cs:193`, `NTopBarPortraitTip.cs:48`, `ProgressSaveManager.cs:558,572`, `NGameOverScreen.cs:455`, `NRunHistoryPlayerIcon.cs:97`. | Visible "lock" icon shows on the top-bar UI during the run. No epoch unlock progress saved. |
| **No ascension progression on win** | [`ProgressSaveManager.cs:222-238`](../decompiled/sts2/MegaCrit/sts2/Core/Saves/Managers/ProgressSaveManager.cs#L222-L238) — `if (!flag2 && !flag3)` gates `IncrementSingleplayerAscension` / `IncrementMultiplayerAscension` / `TotalWins++` / `CurrentWinStreak++` / `FastestWinTime`. `flag3 == (GameMode == Custom)`. | Winning a Custom run doesn't bump anything on the character's record. |
| **No loss tracking** | Same file, [lines 243-246](../decompiled/sts2/MegaCrit/sts2/Core/Saves/Managers/ProgressSaveManager.cs#L243-L246). | Losing a Custom run doesn't reset win streak either. |
| **Run history flagged as "custom"** | [`NRunHistory.cs:470`](../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/RunHistoryScreen/NRunHistory.cs#L470) — `case GameMode.Custom: locString.Add("GameMode", ...)`. | Appears in the run-history screen with a "custom" label and the same achievement-lock icon. |
| **No daily-mod challenge save** | [`RunManager.cs:521`](../decompiled/sts2/MegaCrit/sts2/Core/Runs/RunManager.cs#L521) — `if (State.GameMode != GameMode.Standard) return false;`. | Not applicable for sealed-deck runs; flagged for completeness. |
| **Metrics scrubbing** | [`MetricUtilities.cs:98`](../decompiled/sts2/MegaCrit/sts2/Core/Runs/Metrics/MetricUtilities.cs#L98) — non-Standard runs return early before reporting. | Telemetry-only; not user-facing. |
| **Multiplayer lobby variants exist** | `NMultiplayerHostSubmenu.OnCustomPressed`, `NJoinFriendScreen` handling `GameMode.Custom`. | Out of scope for our singleplayer mod focus. |
| **Pandora's Box removed from grab bags** | [`SealedDeck.cs:39-43`](../decompiled/sts2/MegaCrit/sts2/Core/Models/Modifiers/SealedDeck.cs#L39-L43) explicitly `Remove<PandorasBox>()` on every player's `RelicGrabBag` and the shared bag after the draft. | This is a `SealedDeck`-specific effect, not a Custom Mode effect. Pandora's Box converts your starter deck → strikes/defends, which makes no sense when there is no starter deck. |
| **Darv event won't offer Pandora's Box** | [`Darv.cs:185`](../decompiled/sts2/MegaCrit/sts2/Core/Models/Events/Darv.cs#L185) — `ValidRelicSet((owner) => !owner.RunState.Modifiers.Any(m => m.ClearsPlayerDeck), [PandorasBox])`. | Same rationale, defensive filter at the event level too. |

Effects NOT observed in the search:

- **Daily-mod flags**: irrelevant — Daily mode is its own thing, Custom Mode isn't a "daily" anything.
- **Ascension forced off**: the Custom Mode UI does expose the ascension panel (`_ascensionPanel.Initialize(MultiplayerUiMode.Singleplayer)` per [NCustomRunScreen.cs:246](../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/CustomRun/NCustomRunScreen.cs#L246)). The streamer CAN run Custom + Ascension N, it just doesn't progress the ascension track on win.
- **Unlock progression skipped**: not observed as an explicit branch; the `MarkActAsSeen` / `MarkEventAsSeen` / `MarkRelicAsSeen` calls in `ProgressSaveManager.UpdateWithRunData` don't gate on GameMode, so playing in Custom still marks things as "seen" for the gallery / discovery progress. Only the win/loss/ascension/streak counters are gated.
- **Save data flagged separately**: the `SerializableRun` carries `GameMode` as a field, but it lives in the same save folder as standard runs. Not a separate profile.

## Viability assessment for "use vanilla as-is for v1"

**Hypothesis to test**: *"Streamer can run a sealed-deck mod-stream by just selecting custom mode + sealed deck from vanilla's existing UI, with no mod-side code for the draft itself."*

**Verdict: YES — viable as-is for v1, with documented limitations.** The procedural shape of the run works on every character (Custom Mode → SealedDeck modifier → 10-of-30 draft → run continues). Necrobinder's `Bodyguard`/`Unleash` and Defect's `Zap`/`Dualcast` are `CardRarity.Basic` and won't appear in the draft pool — characters dependent on those Basics will play differently (and likely harder) in sealed-deck mode, but a skilled streamer drafting 10 out of 30 has the flexibility to build around it. Treat as a documented known limitation, not a blocker. Per-character must-include support stays in the "polish, may never happen" bucket alongside the chat-Neow-vote-before-draft polish.

The basic mod-stream loop works straightforwardly:

1. Streamer launches the game, goes to Main Menu → Singleplayer → Custom.
2. Streamer ticks `SealedDeck` in the modifiers list (and any other compatible modifiers they want; `Draft` and `Insanity` are mutually exclusive with `SealedDeck`).
3. Streamer picks character + ascension + optional seed, hits Embark.
4. Run starts. Player deck is cleared by `ClearsPlayerDeck`. The first encounter is the Neow event, which now shows one option: "Sealed Deck" (or whatever the loc-string resolves to — see Open question Q2).
5. Streamer clicks it. The 30-card grid opens. Streamer picks 10. Confirm.
6. The 10 cards land in the deck. Run continues normally. Our existing B.2.1 patches still apply to all card-reward screens / Ancients events / etc. that follow.

No mod code needed for the draft. Chat doesn't vote on the draft (the streamer drafts solo, which matches the original Tempus mod per the [v0.2+ note](06-followups-and-deferred.md)).

### Caveats the streamer / FrostPrime should be aware of

1. **N and M are 10 and 30, fixed.** If the desired feel is closer to "Necrobinder needs a 12-of-40 pool for it to be playable", vanilla can't do that without code.
2. **Necrobinder identity cards are absent from the SealedDeck pool (structural finding; not a gameplay showstopper).** Confirmed 2026-05-12: `Bodyguard` ([Bodyguard.cs:18](../decompiled/sts2/MegaCrit/sts2/Core/Models/Cards/Bodyguard.cs#L18)) and `Unleash` ([Unleash.cs:32](../decompiled/sts2/MegaCrit/sts2/Core/Models/Cards/Unleash.cs#L32)) are both `CardRarity.Basic`. The `RegularEncounter` rarity odds used by SealedDeck only roll Common/Uncommon/Rare; `RollForRarity` ([CardFactory.cs:199-220](../decompiled/sts2/MegaCrit/sts2/Core/Factories/CardFactory.cs#L199-L220)) will never select Basic, so Necrobinder's starter Basics will not appear in the 30-card pool. On Discord (2026-05-12), a community member framed this as "Necrobinder is unplayable without Bodyguard/Unleash" — one perspective, not a structural truth. With 30 cards to choose 10 from, a skilled streamer like FrostPrime can almost certainly assemble a working deck from Common/Uncommon/Rare picks even without the Osty-centric Basics — the character just plays differently. **Treat as documented known limitation, not a v1 blocker.** Future polish if it bites: inject Basic must-includes into `source` ([SealedDeck.cs:31](../decompiled/sts2/MegaCrit/sts2/Core/Models/Modifiers/SealedDeck.cs#L31)) before the grid opens, OR add them to `player.Deck` directly after the draft completes. Defect's `Zap`/`Dualcast` (also Basic) likely behave the same way; same treatment applies.
3. **No chat Neow vote before the draft.** Vanilla replaces the Neow pick-3 with the single-option draft. Adding a chat Neow pick before the draft requires the `Neow.GenerateInitialOptions` patch described above.
4. **No ascension progression, no achievements, no win/loss tracking for the run.** Streamer's account stats stay where they are. Probably desired for stream context but worth telling the streamer up front.
5. **Pandora's Box is removed from all grab bags for the run.** Streamer can't see it as a relic option. Defensive/intentional, not a bug; flag to chat if they ever notice.
6. **The 30-card draft pool reflects what the character has unlocked.** A fresh modded profile with no unlocks would draft from a much smaller card pool. This is the same `forceFirstRunNeow`-ish onboarding concern flagged in [notes/06 B.1 follow-ups](06-followups-and-deferred.md) — modded saves don't have the streamer's unmodded progress unless we explicitly copy or override. Likely needs the same fix as the Neow onboarding flag (out of scope for this investigation but worth flagging together).
7. **The draft cannot be aborted.** `Cancelable = false` on the `CardSelectorPrefs`. Once the streamer enters the grid, they have to confirm 10 picks. If they Alt-F4, the run is abandoned and they restart from scratch.
8. **Co-op multiplayer flows through `BlockingPlayerChoiceContext`.** In a multi-player Custom run, only the lobby host drafts; remote players block on `BlockingPlayerChoiceContext`. We're singleplayer-only anyway, but flagged.

None of these are showstoppers for the "show a sealed-deck Slay the Streamer stream" v1 use case. They're all things to document in a streamer-onboarding note when the time comes.

## Open questions

Items this investigation cannot answer from the decompile alone. **Q3, Q5, Q7 resolved 2026-05-12 by Surfinite + follow-up decompile check; kept here for the historical trail.**

1. **What does the SealedDeck Neow option look like in-game?** The loc-string keys are `modifiers.SEALED_DECK.NeowOptionTitle` and `modifiers.SEALED_DECK.NeowOptionDescription` (via `ModifierModel` base properties). The actual text lives in the game's `.pck` localization tables, not the decompile. Worth confirming visually whether the wording is friendly enough or needs supplementing with a chat receipt explaining what's about to happen.
2. **What does the 30-card grid screen look like?** Specifically: does it scroll, is it 30-in-one-view, what's the confirm button look like, is there any feedback on rarity counts (e.g., "you picked 7 commons / 3 rares")? Affects whether stream viewers can read the screen at all. Requires in-game inspection.
3. **[RESOLVED 2026-05-12] Does `CharacterCards` modifier stack with `SealedDeck`?** **Yes — stacks, and adds the other character's cards into the 30-card draft pool**. Confirmed by Surfinite. This is fun behavior worth keeping; no code action needed. Per-character pool composition becomes "this character + the CharacterCards target character's cards" when both modifiers are ticked. Streamer-facing implication: a "Necrobinder + CharacterCards = Ironclad" combo could solve the Necrobinder-identity-cards problem in a different direction (drafting from a different character's Common/Uncommon/Rare pool instead), but it changes the run identity entirely.
4. **Behavior on Ascension 20 + SealedDeck**: ascension scaling stacks normally with modifiers? Likely yes, but worth a sanity check, especially because the upgraded-cards-on-A19+ behavior interacts with the `NoUpgradeRoll` flag in `SealedDeck`'s pool generation.
5. **[RESOLVED 2026-05-12] Custom Mode is unlock-gated by 3 standard wins.** Per Surfinite: vanilla requires 3 wins to unlock the Custom Mode main-menu button. **Not derived from the decompile** — Surfinite's knowledge from playing the game. FrostPrime has it unlocked already so this is not an immediate blocker for v1. **Possible mod-side mitigations** (to design later, not now): (a) "unlock everything" side-effect when the mod loads, applied to the modded save profile; (b) a configurable settings flag `unlockAllForMod: true` (default true?) that performs the unlocks at boot. Same concern family as the existing v0.1 onboarding gap (`forceFirstRunNeow` / `copySaveFromUnmodded`) flagged in [notes/06 B.1 follow-ups](06-followups-and-deferred.md) — modded profiles are progress-empty unless we explicitly handle it. If any of those settings flags get implemented, "unlock Custom Mode" should ride along with them.
6. **Does `BlockingPlayerChoiceContext` interact safely with our mod's Harmony patches?** None of our current patches fire during the draft (we don't patch `CardSelectCmd.FromSimpleGridForRewards`), but if a vote happens to be live from a prior screen when the draft starts, we should verify the chat thread doesn't try to mutate state on a screen that's no longer there. Likely fine; flag for the smoke when sealed-deck v1 lands.
7. **[RESOLVED 2026-05-12] Does MegaCrit filter the Necrobinder pool so the streamer can't end up unplayable?** **No — but the gameplay impact is less severe than the original Discord claim suggested.** Verified via decompile 2026-05-12: the "needed" cards (`Bodyguard`, `Unleash` for Necrobinder; same shape for `Zap`/`Dualcast` on Defect, likely others) are `CardRarity.Basic`. The SealedDeck pool uses `CardCreationOptions(..., CardRarityOddsType.RegularEncounter)`. `CardFactory.RollForRarity` ([CardFactory.cs:199-220](../decompiled/sts2/MegaCrit/sts2/Core/Factories/CardFactory.cs#L199-L220)) only rolls Common/Uncommon/Rare under RegularEncounter odds — Basic is never selected. So Basic-rarity character-identity cards do not appear in vanilla SealedDeck drafts regardless of seed. **However**: the community framing of "Necrobinder is unplayable without these" is one perspective, not a structural truth. A skilled streamer drafting 10 of 30 has substantial flexibility; the character plays differently without the Osty-centric Basics but is not categorically broken. Surfinite (2026-05-12) overrode this concern: keep per-character must-include support in the "polish, may never happen" bucket. Future polish patch targets if needed: inject Basic must-includes into `source` before the grid opens ([SealedDeck.cs:31](../decompiled/sts2/MegaCrit/sts2/Core/Models/Modifiers/SealedDeck.cs#L31)), or append directly to `player.Deck` after the draft completes.

## Draft modifier — parallel research (2026-05-12)

Surfinite asked 2026-05-12 for the same shape of research on the sibling `Draft` modifier, since it's mutually exclusive with `SealedDeck` (and `Insanity`) and represents the alternative "deck-clearing pre-run choice" path. Quick findings — mostly the same shape as SealedDeck with one major implication for our existing patches.

### Vanilla implementation

[`decompiled/sts2/MegaCrit/sts2/Core/Models/Modifiers/Draft.cs`](../decompiled/sts2/MegaCrit/sts2/Core/Models/Modifiers/Draft.cs) — 41 lines, single class. Same architecture as `SealedDeck`: `Draft : ModifierModel`, `ClearsPlayerDeck => true`, `GenerateNeowOption` returns the draft Task.

The core method is `OfferRewards(Player player)` ([Draft.cs:20-40](../decompiled/sts2/MegaCrit/sts2/Core/Models/Modifiers/Draft.cs#L20-L40)):

```csharp
CardCreationOptions creationOptions = new CardCreationOptions(
    new ReadOnlySingleElementList<CardPoolModel>(player.Character.CardPool),
    CardCreationSource.Other,
    CardRarityOddsType.RegularEncounter
).WithFlags(CardCreationFlags.NoUpgradeRoll);

for (int i = 0; i < 10; i++)                                     // N = 10 picks
{
    CardReward reward = new CardReward(creationOptions, 3, player)  // 3-card pick each
    {
        CanSkip = false
    };
    await reward.Populate();
    if (LocalContext.IsMe(player))
    {
        await reward.OnSelectWrapper();
    }
}
// Same PandorasBox scrub as SealedDeck (lines 35-39)
```

### Differences vs SealedDeck

| Dimension | SealedDeck | Draft |
|---|---|---|
| UX shape | One grid of 30 cards, streamer picks 10 | 10 sequential `pick-1-of-3` screens |
| Effective N total | 10 from 30 | 10 from up to 30 (3×10 = 30 cards, but each pick fresh-rolls; in practice fewer unique cards seen because of resampling) |
| Card-creation flags | `NoUpgradeRoll \| ForceRarityOddsChange` | `NoUpgradeRoll` only |
| Skippable? | No (`Cancelable = false` on `CardSelectorPrefs`) | No (`CanSkip = false` on each `CardReward`) |
| Rarity weighting | Forced rarity-odds-change variant (`Roll`) | Player's normal modified odds (`RollWithBaseOdds`) — relics like the rarity-shifting ones would influence the Draft pool but not the SealedDeck pool |
| Screen opened | `CardSelectCmd.FromSimpleGridForRewards` → simple-grid card selector | `CardReward.OnSelectWrapper` → `NCardRewardSelectionScreen.ShowScreen` ([CardReward.cs:149](../decompiled/sts2/MegaCrit/sts2/Core/Rewards/CardReward.cs#L149)) — **the exact same screen our B.2.1 `CardRewardVotePatch` hooks** |

### The big implication: Draft routes through our patched surface

Our B.2.1 card-reward voting patches hook `NCardRewardSelectionScreen.SelectCard`. When the `Draft` modifier is active, the streamer's draft flow opens that screen 10 times in a row, inside the Neow event. **With our mod loaded, chat would automatically vote on each of those 10 picks.** The streamer doesn't drive the draft — they kick it off and then watch chat pick the deck card-by-card.

This is significantly different from `SealedDeck` (where the streamer alone drafts via a grid screen we don't patch). The two vanilla modifiers map onto two completely different chat-vs-streamer experiences:

- **`SealedDeck` + our mod** ≈ Tempus's original StS1 mod: streamer drafts the deck alone, chat antagonizes via subsequent in-run voting.
- **`Draft` + our mod** ≈ a fully chat-controlled deck construction: chat builds the entire deck by voting on 10 sequential card rewards before the run even starts.

Neither is implemented as a designed feature today — it's an emergent interaction between vanilla and our existing patches. Worth deliberately deciding on for v0.2 sealed-deck-mode rather than letting whichever happens by accident be the default.

### Caveats specific to Draft

1. **All the `SealedDeck` Basic-rarity caveats apply identically.** Same `CardCreationOptions(..., CardRarityOddsType.RegularEncounter)`, same `RollForRarity`, same exclusion of Basic. So `Bodyguard`/`Unleash`/`Zap`/`Dualcast` etc. won't show up in any of the 10 picks either. Documented as a known limitation; not a blocker.
2. **B.2.1 patch behaviour during Draft is unverified.** The vote patch will fire, but the per-act skip budget tracking (which lives on `NRewardsScreen`, the *parent* rewards screen — not involved here because Draft opens `NCardRewardSelectionScreen` directly) won't be exercised. `CanSkip = false` means skip-related vote paths can't be triggered anyway. Likely fine; would benefit from a smoke playthrough before relying on it.
3. **Reset-receipt timing edge case.** Our `SetRewards_Postfix` budget-reset detection fires on `NRewardsScreen`, not `NCardRewardSelectionScreen`. Draft opens 10 sub-screens before the first real `NRewardsScreen` of Act 1 ever appears, so reset receipts should still fire at the expected "first combat of Act 1" moment, not during the draft. Still worth a smoke to confirm.
4. **Card pool exposure is sequential, not gridwise.** A skilled streamer (or chat) might see common cards repeated across the 10 picks because each pick re-rolls the pool. Less "draft a balanced deck" feel, more "react to whatever shows up". For a chat-driven build, this is probably good (high variance, more drama); for a streamer-only build, SealedDeck's grid view is the better fit.
5. **`Draft` and `SealedDeck` and `Insanity` are mutually exclusive** ([`ModelDb.cs:227-232`](../decompiled/sts2/MegaCrit/sts2/Core/Models/ModelDb.cs#L227-L232)) — only one can be ticked at a time. Streamer commits to a single pre-run experience.

### Implication for the v0.2 sealed-deck-mode spec

The original spec note in [notes/06](06-followups-and-deferred.md) assumes Sealed Deck shape (streamer drafts alone). With `Draft` available as a vanilla alternative that naturally produces a "chat picks the entire deck" experience via our existing patches with zero new code, the v0.2 design decision now has at least three candidate shapes to evaluate, all reachable via vanilla modifiers:

- **A**: Vanilla `SealedDeck` modifier — streamer drafts grid solo, no chat input on the draft. Closest to Tempus's StS1 mod.
- **B**: Vanilla `Draft` modifier — chat votes on all 10 picks via our existing card-reward vote patches. Heavier chat-control. Free with no extra code.
- **C**: Hybrid — use `SealedDeck` then add a Neow-vote-before-draft step (the polish item from the main investigation). Streamer drafts the deck, but chat gets the standard Neow bonus before the draft starts.

Picking which to ship — or whether to ship multiple as configurable streamer-side options — is a future spec/design decision, not a research call.

## Cross-references

- [notes/06-followups-and-deferred.md](06-followups-and-deferred.md) — the v0.2+ section has the original sealed-deck-mode notes (corrected 2026-05-12 to "streamer drafts, not chat drafts").
- [notes/07-youtube-chat-feasibility.md](07-youtube-chat-feasibility.md) — structure model for this writeup.
- `SealedDeck.cs`, `Draft.cs`, `ModifierModel.cs`, `Neow.cs`, `NCustomRunScreen.cs`, `NCustomRunModifiersList.cs`, `GameModeExtension.cs`, `ModelDb.cs` — the key implementation surfaces enumerated throughout.
- Memory entry `sts2_ancients.md` — example of an EventModel-based decision point we already chat-vote on; the SealedDeck Neow-replacement uses the same `EventOption` mechanic at the model layer.
