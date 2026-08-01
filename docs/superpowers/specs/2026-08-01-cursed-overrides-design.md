# Cursed Overrides — design spec

**Date:** 2026-08-01
**Status:** Approved (Surfinite, 2026-08-01)
**Origin:** FrostPrime feature request — "whenever a streamer uses an override they should get a random curse card added to their deck."
**Commit prefix:** `cursed-overrides/N`
**Game version at design time:** StS2 Beta v0.110.0 (mod v0.2.0)

## 1. Summary

When the streamer spends a vote override, a random curse card is added to their
deck. Toggleable via a new mod setting, **default off**. One curse per override
spend, on every override path. Chat sees the curse named in the existing
override receipt; in-game, the vanilla card-added preview animation is the
visual.

## 2. Setting

- Key: `cursedOverrides`, bool, default `false`, stored in the mod settings
  JSON (`%APPDATA%\SlayTheSpire2\slay_the_streamer_2.json`).
- Plumbed end-to-end through the same layers as `voteOverridesPerAct`
  (the RelicChoices/vote-override settings pattern).
- UI: one `NSettingsTickbox` row, label **"Cursed Overrides"**, placed
  directly under the Vote Overrides dropdown in the mod settings panel.
  Hover text: *"Each vote override also adds a random curse card to your
  deck."*
- Always visible — no conditional hiding when vote overrides are disabled,
  matching the rest of the panel.

## 3. Curse picker — `CursedOverrides` static class

Location: `src/Game/DecisionVotes/CursedOverrides.cs`. **Test-csproj
constraint:** the tests project compiles `src/Game/DecisionVotes/**` via
glob with per-file `Compile Remove` for classes that touch Godot/MegaCrit
types (the Harmony-patch pattern). So the feature splits into two files:

- `CurseRoll.cs` (rides the glob, BCL-only, unit-tested):
  `PickCurse(IReadOnlyList<string> pool, Random rng)` — pure; returns one
  entry uniformly at random.
- `CursedOverrides.cs` (game-side, MegaCrit types; add an explicit
  `Compile Remove` for it in `tests/slay_the_streamer_2.tests.csproj`):
- `CursedOverrides.TryRollCurse(Player player)`:
  1. Returns `null` immediately if `cursedOverrides` is off (default) — zero
     game-state contact.
  2. Builds the pool:
     `ModelDb.CardPool<CurseCardPool>().AllCards.Where(c => c.CanBeGeneratedByModifiers)`.
     As of v0.110.0 this yields 10 curses: Clumsy, Debt, Decay, Doubt,
     Guilty, Injury, Normality, Regret, Shame, Writhe. The game's own flag
     excludes special-purpose curses (Ascender's Bane, Curse of the Bell,
     Enthralled, Spore Mind, Greed) plus Bad Luck, Folly, Poor Sleep. New
     generic curses in future game patches join the pool with zero mod
     changes. (Decision: game-flag filter over a hand-curated list.)
  3. Picks one with a **private `Random` instance** — NOT the run's seeded
     RNG. Mutating `runState.Rng` would shift vanilla's subsequent rolls.
     Determinism is not required here.
  4. Fires `TaskHelper.RunSafely(CardPileCmd.AddCursesToDeck(...))` — the
     game's own fire-and-forget pattern. `AddCursesToDeck` handles card
     creation, deck insert, and the on-screen card-added preview.
  5. Returns the picked curse's `Title` for the receipt.
- Any exception is caught, logged as a `[cursed-override]` Warn, and returns
  `null`. **A curse-roll failure must never break the override itself.**
- Empty-pool paranoia case: Warn, no curse, override proceeds.

## 4. Wiring — three call sites

Each site calls `CursedOverrides.TryRollCurse(player)` immediately after its
existing `VoteOverrideBudget.RecordUse()`:

| Site | Path | Player source |
|---|---|---|
| `CardRewardVotePatch.TryOverrideWithCard` | take-card override | local player from screen context |
| `CardRewardVotePatch` override-skip | both `includeSkip` sub-cases; the `Cancel()` sub-case rolls at the same spot it records the use, not in the resume handler | local player from screen context |
| `AncientVotePatch.TryOverride` | ancient-option override | event `Owner` |

Ordering rule (preserved from the vote-override spec 2026-07-21): budget is
consumed strictly after `TryCloseNow` succeeds; the curse rolls strictly
after `RecordUse()`. Coverage decision: **every** override spend curses,
including override-to-Skip — "overriding chat always costs a curse."

## 5. Chat receipt

`VoteOverrideBudget.FormatOverrideReceipt` and `SendOverrideReceipt` gain an
optional `curseTitle` parameter (string-only — `VoteOverrideBudget` stays
Godot-free per its header contract). Current actual format:

> "{streamer} overrode the vote and took {label}. {N} override(s) remaining this act"

With a curse (clause inserted after the taken-label sentence):

> "{streamer} overrode the vote and took {label}. Cursed Overrides: gained
> {curse}! {N} override(s) remaining this act"

One message, extending the existing receipt — no added Twitch rate-limit
pressure (decision: extend, not a separate message). When the setting is off
or the roll returned null, the receipt is unchanged. The unlimited-budget
variant (limit < 0, count omitted) gets the same clause insertion.

## 6. Error handling and edge cases

- **Setting off (default):** zero behavior change anywhere.
- **Multiplayer:** the curse goes to the overriding (local) player only —
  the same player the override already acts for.
- **Save-quit:** deck adds go through the normal card-pile command like
  vanilla event curses (LostWisp, UnrestSite, Wellspring), so they should
  persist. Explicitly verified in the operator gate (override → save-quit →
  Continue → curse still in deck) because of the known mid-room snapshot
  landmine (CLAUDE.md).
- **Curse-roll failure:** logged Warn; override, receipt (minus curse
  clause), and budget all proceed normally.

## 7. Testing

Unit (no Godot):
- `PickCurse` bounds + uniform coverage with seeded `Random`.
- Setting-off short-circuit returns null.
- Receipt formatting with and without curse title (pure string tests).
- Test classes that touch `VoteSession` tally events get
  `[Collection("TiLog.Sink")]` per the standing rule.

Operator-validation gate (Godot-side, live game):
- All three override paths with setting **on**: curse lands, preview plays,
  receipt names the curse.
- Setting **off**: no curse, receipt unchanged.
- Exhausted-budget click: no curse (no `RecordUse`, so no roll).
- Save-quit → Continue after a cursed override: curse still in deck.

## 8. Out of scope (YAGNI)

- Curse-count scaling or per-curse weighting.
- Chat voting on which curse.
- The shop skip→override trade-in idea (separate feature, separate spec).

## 9. Decisions log

| Decision | Choice | Alternatives considered |
|---|---|---|
| Curse pool | Game-flag filter (`CanBeGeneratedByModifiers`) | Hand-curated list; flag filter + opt-ins (Bad Luck, Folly, Poor Sleep) |
| Coverage | Every override spend, incl. override-to-Skip | Exempting Skip overrides |
| Chat message | Extend existing override receipt | Separate message; none |
| Architecture | Helper called at each spend site (A) | Centralize in `RecordUse` (B); event-driven off `ForcedWinner` (C — misses the Cancel path, ruled out) |
| RNG | Private `Random`, non-deterministic | Run-seeded RNG (would perturb vanilla rolls) |
