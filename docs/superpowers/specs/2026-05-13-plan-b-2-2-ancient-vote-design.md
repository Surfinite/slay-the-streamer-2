# 2026-05-13 — Plan B.2.2: Ancient Vote (design)

**Date**: 2026-05-13
**Status**: Design approved 2026-05-13. Implementation pending.
**Slice**: B.2.2 (start-of-act Ancient-rarity relic vote) — fourth voting slice after B.1 (Neow), B.2.1 (card reward), and v0.2 yt-chat.
**Scope**: Extend the existing `NeowBlessingVotePatch` to handle all 6 Ancients (Pael, Tezcatara, Orobas, Nonupeipe, Tanx, Vakuu) in addition to Neow. Predicate-widening on the same `NEventRoom.OptionButtonClicked` Harmony patch.

## TL;DR

In StS2, Neow and the 6 mid-run Ancients are structurally identical: all are `AncientEventModel` subclasses, rendered via the same `NEventRoom`, and resolved via the same `OptionButtonClicked` method. The B.1 voting slice already covers this code path for Neow. B.2.2 widens the patch's event-type predicate from `is Neow` to `is AncientEventModel and not DeprecatedAncientEvent`, deriving the vote title from `eventModel.Title.GetFormattedText()` instead of hardcoding "Neow's Bonus". Net change: ~30 LOC, one class rename, six new ancients covered. Design surface is small enough that the full multi-LLM meta-review is intentionally skipped.

## Goals

- Chat votes on the relic option presented by each Ancient event (3 options typical, gated dynamically by the game's `GenerateInitialOptions` logic).
- Identical vote flow to Neow: 30s vote, mandatory chat receipt, run-id guard, multiplayer bail, single-option skip.
- Single Harmony patch covers Neow + all current ancients + any future `AncientEventModel` subclasses MegaCrit ships (StS2 is in early access — new ancients are plausible with future characters/acts).
- Zero new architectural surface; reuse B.1's machinery verbatim.

## Non-goals

- **NutritiousSoup heads-up** — Tezcatara relic that enchants Basic Strikes on pickup; inert if the deck has no Strike-tagged cards (only possible in Sealed/Draft Custom-Mode runs). Vanilla quirk; the game offers the option unconditionally and `AfterObtained` runs over zero matches. Ignored for v1.
- Per-ancient dialogue or flavor customization in chat receipts.
- Sealed-deck pre-Neow drafting order (relevant only once sealed-deck mode is wired; see [`notes/08`](../../../notes/08-sealed-deck-custom-mode-investigation.md)).
- Unit tests for the predicate — see Testing.
- Full multi-LLM meta-review crowd-sourcing pass — intentionally skipped given the change surface; normal PR self-review at integration time.

## Architecture

The B.1 patch (`NeowBlessingVotePatch`) attaches Harmony to `NEventRoom.OptionButtonClicked` and bails to vanilla unless the active event model is `Neow`. Inheritance research (verified against [`decompiled/sts2/MegaCrit/sts2/Core/Models/Events/*.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/Models/Events/) on 2026-05-13):

- `Neow`, `Pael`, `Tezcatara`, `Orobas`, `Nonupeipe`, `Tanx`, `Vakuu`, `Darv` all inherit `AncientEventModel`.
- `DeprecatedAncientEvent` is the only sibling we must exclude (sealed class, intentionally unused).
- All 7 mid-run ancients are stored in the `_event` field on `NEventRoom` exactly like Neow, accessed via the same `_eventField` `FieldInfo` cache.
- All 7 ancients resolve options via `OptionButtonClicked(EventOption option, int index)` with the identical signature B.1 patches.

Act mapping (informational — the patch doesn't care which act):
- Act 1 (`Underdocks` / `Overgrowth`): Neow only (gated on `NeowEpoch`).
- Act 2 (`Hive`): Orobas (gated on `OrobasEpoch`), Pael, Tezcatara.
- Act 3 (`Glory`): Nonupeipe, Tanx, Vakuu.
- Cross-act (`AllSharedAncients` at [`ModelDb.cs:121`](../../../decompiled/sts2/MegaCrit/sts2/Core/Models/ModelDb.cs#L121)): Darv (gated on `DarvEpoch`). Darv was missed during initial brainstorming research — found during T3 code review on 2026-05-13. Inheritance-based predicate handles it correctly without code changes.

Epoch-based unlock gating is handled at the act level (`GetUnlockedAncients(UnlockState)`) or cross-act (`AllSharedAncients`). By the time `OptionButtonClicked` fires, the chosen ancient has already passed all unlock + character-pool filters — the patch sees only valid, currently-offered ancients.

## Code changes

**Renames:**
- File: `src/Game/DecisionVotes/NeowBlessingVotePatch.cs` → `AncientVotePatch.cs`
- Class: `NeowBlessingVotePatch` → `AncientVotePatch`
- Log tag prefix: `[SlayTheStreamer2][neow-vote]` → `[SlayTheStreamer2][ancient-vote]` (every log site)
- Comment in [`src/ModEntry.cs:177`](../../../src/ModEntry.cs#L177) updated to reference `AncientVotePatch`
- [`tests/slay_the_streamer_2.tests.csproj:23`](../../../tests/slay_the_streamer_2.tests.csproj#L23) `Compile Remove` path updated to the new filename

**Using directives** (correction applied during T3 implementation, 2026-05-13): the original spec proposed adding `using MegaCrit.Sts2.Core.Entities.Ancients;`, but `AncientEventModel` actually lives in `MegaCrit.Sts2.Core.Models` (verified at [`decompiled/sts2/MegaCrit/sts2/Core/Models/AncientEventModel.cs:22`](../../../decompiled/sts2/MegaCrit/sts2/Core/Models/AncientEventModel.cs#L22)) — that namespace is already imported. `DeprecatedAncientEvent` is in `MegaCrit.Sts2.Core.Models.Events`, also already imported. No new using directive needed.

**Predicate (the substantive change)** — replaces `IsNeowEvent`:
```csharp
private static bool IsAncientEvent(NEventRoom room) {
    var eventModel = _eventField.Value?.GetValue(room);
    return eventModel is AncientEventModel and not DeprecatedAncientEvent;
}
```
Called at its two existing sites: the `Prefix` gating bail and the `ResumeOnMainThread` liveness check ("is this still the same kind of event?").

**Vote title** — new helper, replaces the hardcoded `"Neow's Bonus"`:
```csharp
private static string GetVoteTitle(NEventRoom room) {
    var eventModel = _eventField.Value?.GetValue(room) as EventModel;
    var name = eventModel?.Title.GetFormattedText() ?? "Ancient";
    return $"{name}'s Offering";
}
```
Yields "Neow's Offering", "Pael's Offering", "Tezcatara's Offering", etc. The fallback `"Ancient"` covers the (essentially impossible) case where the reflection cast succeeds at `object` but fails at `EventModel`. Cheap and defensive.

Call site change in `Prefix`:
```csharp
// was: session = coordinator.Start("Neow's Bonus", labels, TimeSpan.FromSeconds(30));
session = coordinator.Start(GetVoteTitle(__instance), labels, TimeSpan.FromSeconds(30));
```

**Reused verbatim (no change):**
- `RunIdGuardEnabled` static property + the soft-check in `Prepare`
- Field-info caching for `_event`
- Multiplayer bail (`Players.Count > 1` → vanilla)
- Single-option skip (degenerate vote falls through to vanilla)
- Chat-not-readable bail (`ConnectedReadOnly` or `ConnectedReadWrite` required)
- `_voteInProgress` / `_resumeInProgress` flags
- `HandleVoteAsync` + suspend-and-resume Harmony pattern
- Run-state liveness checks during resume (`IsAbandoned`, `IsGameOver`, run-id drift)
- Vote duration (30s — unchanged)
- Harmony attribute target (`typeof(NEventRoom), nameof(NEventRoom.OptionButtonClicked)`)

**Estimated total LOC change:** ~30 net (mostly renames + the new title helper + the swapped predicate body).

## Edge cases & risks

- **`DeprecatedAncientEvent`** — sealed class, intentionally excluded by the predicate. If MegaCrit ever instantiates one (current code suggests they don't), the patch bails to vanilla cleanly.
- **NutritiousSoup in Strike-less Sealed/Draft deck** — vanilla offers the option unconditionally; `AfterObtained` iterates zero matching cards and the relic is silently inert. Same outcome whether the streamer or chat picks it. Out of scope per Non-goals; document in PR description so it's discoverable.
- **ArchaicTooth in Sealed/Draft deck without transcendence cards** — game-side filtered by `SetupForPlayer` returning false (see Orobas.OptionPool3 in the decompile). The patch never sees it as an option. No mod-side action needed.
- **Future ancients added by MegaCrit** — inheritance-based predicate covers them automatically. Worst case for a non-standard flow is either a degenerate vote (caught by single-option skip → vanilla) or a runtime exception (caught by `HandleVoteAsync`'s try/catch → fall back to player click). The existing safety nets are sufficient.
- **Vote-title localization** — `eventModel.Title.GetFormattedText()` returns the localized in-game name. If localization data is missing, returns the key string. The `"'s Offering"` suffix is English-only; we could localize this later if we ship a non-English build (currently English-only).
- **Run-id drift during a Pael vote that triggers an Act transition** — covered by the existing `runIdAtStart != currentRunId` check in `ResumeOnMainThread`.
- **Cosmetic regression: "Neow's Bonus" → "Neow's Offering"** — accepted trade for zero special-casing per the design discussion. Mention in the PR description.

## Testing strategy

**Unit tests:** None possible for the predicate. The test csproj explicitly excludes Harmony-patch files from compilation (they reference `MegaCrit.Sts2.*` types the test project doesn't link); same constraint that applied to B.1's predicate and was accepted there. The underlying voting machinery (`VoteSession`, `VoteCoordinator`, receipts, dispatcher) is covered by existing tests and is unchanged.

**Mechanical regression:** `dotnet test` after the rename — expected green. No test references `NeowBlessingVotePatch` by name; only `tests/slay_the_streamer_2.tests.csproj` has the `Compile Remove` line that's updated as part of the rename.

**Operator-validation gate** (required before tagging `plan-b-2-2-complete`):

1. **Neow regression** — start one standard run; vote opens with title "Neow's Offering" (was "Neow's Bonus"); chat votes apply; winner triggers correctly. Confirms the rename + title change didn't regress B.1.
2. **Each Act 2 ancient** (Pael, Tezcatara, Orobas) — one run each, savescum-OK; chest-room ancient triggers vote with title `{Name}'s Offering`; chat votes apply; winning relic granted.
3. **Each Act 3 ancient** (Nonupeipe, Tanx, Vakuu) — same as Act 2.
4. **Trolling override** — any one ancient: streamer clicks option A, chat votes option B, winner is option B. Confirms suspend-and-resume still works for the new event types.

Total: ~7 short runs, ~30 minutes with savescumming at the ancient room.

Results recorded in [`notes/06-followups-and-deferred.md`](../../../notes/06-followups-and-deferred.md) (matches the B.1 / B.2.1 / yt-chat pattern).

## Acceptance gate

- All operator-validation steps above pass on a fresh `./build.ps1` + `./install.ps1`.
- `dotnet test` is green.
- Log inspection: `[SlayTheStreamer2][ancient-vote]` lines appear for at least Neow + one of each act's ancients; no `[neow-vote]` lines remain anywhere.
- Runtime startup hash matches `git log -1 --format=%H` post-merge (per CLAUDE.md's "stale dist" gotcha).
- Tag `plan-b-2-2-complete` once green.
