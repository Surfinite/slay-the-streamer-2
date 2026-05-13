# Meta-Review Round 2 — Plan B.3 Boss Vote v2

**Date**: 2026-05-13
**Source spec**: [`2026-05-13-plan-b-3-boss-vote-design-v2.md`](2026-05-13-plan-b-3-boss-vote-design-v2.md) (committed as `plan-b-3/0.3`)
**Round 1 meta-review**: [`META-REVIEW-2026-05-13-plan-b-3-boss-vote-design.md`](META-REVIEW-2026-05-13-plan-b-3-boss-vote-design.md) (covers 8 reviewers of v1)
**Round 2 reviewers**: 2 (operator-curated subset reviewing v2)
**Code validation pass**: Yes — re-read `VoteSession.cs:267-277` (tie-break / 0-vote semantics), `CardRewardVotePatch.cs:255-282` (HandleVoteAsync fallback shape), confirmed no stable-hash helper exists in `src/Ti/Internal/`.

---

## A.1 — Review Summary

| Reviewer | Sentiment | Headline finding |
|---|---|---|
| R2A | Strongly positive — "Ship it" | Praises SecondBossEncounter exclusion specifically as catching a bug R2A missed in round 1. Four loose items, none blocking. |
| R2B | Mixed-positive — "mostly implementation-ready" | Catches `HashCode.Combine` non-determinism (a real bug breaking Smoke H), plus 4 other blockers + 5 medium items. |

Both reviewers independently identified the same `playerClickIndex` copy-paste artifact (2/2 round-2 consensus).

---

## A.2 — Consensus Points

### C1 — `playerClickIndex` fallback is invalid for boss vote (R2A + R2B, 2/2)

v2 inherited from `CardRewardVotePatch.cs:255-282`:
```csharp
// catch (Exception ex) {
//     winnerIndex = playerClickIndex;
// }
```

For card-reward and Neow, `playerClickIndex` is the streamer's clicked option — the encoded user intent. For B.3, the streamer clicked Proceed, which has no encoded boss choice. Mapping a vote failure to `sample[0]` would be **arbitrarily picking the first boss** because chat had a hiccup. Wrong semantics.

**Code validation**: ✅ Confirmed against `CardRewardVotePatch.cs:263`. The B.2.1 fallback is the streamer's index. B.3 doesn't have one.

**Fix**: Restructure the resume path to handle two cases:
- **Winner resolved successfully** → call `ApplyBossSwap(runState, sample[winnerIndex])`, then synthetic Proceed re-click.
- **Winner failed to resolve** (AwaitWinnerAsync threw / cancelled / liveness check failed) → skip `ApplyBossSwap` (preserve vanilla pre-rolled boss), then synthetic Proceed re-click. The "no lost click" promise still holds — just without the chat-driven mutation.

Implementation: change `HandleVoteAsync` to pass a nullable `int? winnerIndex` to `ResumeOnMainThread`; resume branches on null vs valued.

---

## A.3 — Outlier Points (raised by only one reviewer; high-merit)

### O1 — `HashCode.Combine` is non-deterministic across process launches (R2B only — but correct)

R2B catches that `string.GetHashCode()` in .NET 5+ is per-process randomized for security (the well-known string-hash randomization). `HashCode.Combine` calls `GetHashCode()` on its arguments. Therefore `HashCode.Combine(StringSeed, ActIndex)` produces different ints across process launches **even for the same inputs**.

This directly breaks v2's Smoke H claim ("same run + same act → same candidates on save-reload").

**Code validation**: ✅ Confirmed. This is well-documented .NET behavior since 5.0. No stable-hash helper exists in `src/Ti/Internal/` (verified via Grep). R2B's FNV-1a snippet is a standard fix.

**Fix**: Introduce a stable-hash helper. Two clean options:
- **A**: Add `BossVoteSeed.Stable(string seed, int actIndex) → int` using FNV-1a in `src/Game/DecisionVotes/`. Patch-local, pure, unit-testable.
- **B**: Add a more general `StableStringHash` to `src/Ti/Internal/` for reuse.

Pick **A** for v3 — Rule of Three not fired yet on stable hashing (B.3 is the only consumer). Promote to `Ti/Internal/` if a second consumer shows up later.

**Verdict**: Must-do. v3 adopts FNV-1a in a `BossVoteSeed` pure helper, unit-tested for determinism.

### O2 — Test seam `ApplyBossSwap` is unreachable from tests (R2B only — but correct)

v2 says:
> `BossVotePatch.cs` is excluded from `Compile` ... `ApplyBossSwap` delegate seam lets tests verify the index→encounter mapping

These are contradictory. If `BossVotePatch.cs` is excluded from `Compile`, the test project cannot reference `BossVotePatch.ApplyBossSwap`. The seam exists at runtime but is invisible to xUnit.

**Code validation**: ✅ Confirmed. The B.2.1 pattern uses pure-logic helpers (`SkipBudgetTracker`) for unit-testable parts; the patch itself is operator-validated, not unit-tested. v2 confuses the two layers.

**Fix**: Decouple the testable seam from the runtime hook:
- **Testable**: `BossVoteResolver.ResolveWinner<T>(IReadOnlyList<T> options, int winnerIndex) → T` — pure, game-free, unit-tested for bounds + index→option mapping.
- **Runtime hook**: `BossVotePatch.ApplyBossSwap` delegate stays for operator-debug override (e.g., logging swaps without actually applying), but is NOT a test seam. Document it as "intentionally not unit-tested; covered by Smoke A-H."

**Verdict**: Must-do. Restructure the test seam claim.

### O3 — Popup must take DTOs, not `EncounterModel` (R2B only — but correct)

v2 says `BossVotePopup.cs` is NOT excluded from test compile. But v2 also has the popup rendering `EncounterModel.Title.GetFormattedText()` and loading from `BossNodePath`. Both reference `MegaCrit.Sts2.*` types, so the popup CAN'T compile into tests as written.

**Code validation**: ✅ Confirmed. Looking at existing UI: `VoteTallyLabel.cs` consumes `VoteSession` (TI-side type only — game-free). The popup should follow the same hygiene.

**Fix**: Introduce `BossVotePopupOption(int Index, string Title, string? PortraitPath)` record in `src/Game/Ui/` (or `src/Game/DecisionVotes/`). The patch maps `EncounterModel` → `BossVotePopupOption` before constructing the popup. Popup itself is game-free (only references TI + Godot + the DTO).

**Verdict**: Must-do. v3 introduces the DTO. Popup stays in `Game/Ui/` since it has Godot deps and acceptance tests live in the game tree, but it becomes MegaCrit-free.

### O4 — `QueueFree` thread-safety claim is too confident (R2B only — but correct)

v2 says `SafeQueueFree()` "handles off-main-thread cleanup safely since Godot's `QueueFree` is deferred-and-thread-safe."

**Code validation**: ⚠️ The `VoteTallyLabel.SafeQueueFree` pattern works in practice across 4 prior slices, but R2B is correct that "Godot `QueueFree` is thread-safe" is not formally guaranteed across all node operations. The defensive approach: marshal through `IMainThreadDispatcher` to guarantee main-thread context, matching the project's broader pattern.

**Fix**: `BossVotePopup` accepts `IMainThreadDispatcher` in its constructor; `session.Closed` / `session.Cancelled` handlers do `_dispatcher.Post(SafeQueueFree)` rather than calling `SafeQueueFree` directly. Belt-and-braces; near-zero overhead.

**Verdict**: Should-do. v3 marshals popup cleanup through the dispatcher.

### O5 — Keyboard `ui_accept` may leak through backdrop (R2B only)

v2 specifies `MouseFilter = Stop` on the backdrop `ColorRect`, but only blocks mouse. Keyboard/controller `ui_accept` could still activate a focused button under the overlay (the Proceed button itself).

**Fix**: Override `_UnhandledInput` on the popup root to swallow `ui_accept`:
```csharp
public override void _UnhandledInput(InputEvent @event) {
    if (@event.IsActionPressed("ui_accept") || @event.IsActionPressed("ui_cancel")) {
        GetViewport().SetInputAsHandled();
    }
}
```

**Verdict**: Should-do. Cheap UX fix. Doesn't address full controller-navigation (that stays v0.2 polish), but prevents accidental Proceed during the vote.

### O6 — Tie-break and 0-vote semantics not documented (R2A only)

v2 says "reused verbatim from `VoteSession`" without naming the behavior.

**Code validation**: ✅ Confirmed from `VoteSession.cs:267-277`:
```csharp
private (int Winner, int? TieAmong, bool NoVotes) ComputeWinner() {
    var voted = _tallies.Where(kv => kv.Value > 0).ToList();
    if (voted.Count == 0) {
        var idx = _random.Next(Options.Count);   // 0-vote → random pick across all options
        return (idx, null, true);
    }
    var maxCount = voted.Max(kv => kv.Value);
    var tied = voted.Where(kv => kv.Value == maxCount).Select(kv => kv.Key).ToList();
    if (tied.Count == 1) return (tied[0], null, false);
    return (tied[_random.Next(tied.Count)], tied.Count, false);   // tie → random among tied
}
```

So:
- **0 votes**: random pick from all candidates. `noVotes=true` flag set in snapshot but the winner index is valid.
- **Tie**: random pick from tied candidates.

R2A's specific question — "should `ApplyBossSwap` fire when zero humans expressed an opinion?" — is an interesting design question. Two options:
- **A**: Inherit the behavior. Random pick fires; chat had the opportunity, even if no one took it.
- **B**: Override for B.3. On `noVotes=true`, skip `ApplyBossSwap` (preserve vanilla boss). More conservative.

I lean **A** — consistency with Neow / Card Reward where the random pick fires regardless. Document the inherited behavior so future-you doesn't re-litigate it.

**Verdict**: Should-do — document the inherited tie-break + 0-vote behavior. Pick option A (inherit; random pick fires). One sentence.

### O7 — `_isRelicCollectionOpen` at resume time (R2A only)

What if the streamer opened the relic collection overlay during the 30s vote? Does `OnProceedButtonPressed()` no-op (or throw) when `_isRelicCollectionOpen == true`?

**Verdict**: Should-do — add to spike items. Cheap to verify.

### O8 — `HasSecondBoss` timing (R2A only)

When does `HasSecondBoss` flip to true? Run start? Act 3 entry?

**Code validation**: From `notes/10-boss-vote-feasibility.md` and ActModel decompile: `HasSecondBoss` is true only when `AscensionManager.HasLevel(AscensionLevel.DoubleBoss)` is satisfied AND we're on the final act. The second boss is rolled at run start.

**Verdict**: Should-do — clarify in spike item 3 that `HasSecondBoss` is set at run start; the chest exit is well after that. Confirm at runtime via a log line during Smoke C.

### O9 — `SecondBossEncounter` null-defensive handling (R2B only)

Even if `HasSecondBoss` is true, defensive code shouldn't trust `SecondBossEncounter` is non-null at the call site.

**Fix**:
```csharp
if (runState.Act.HasSecondBoss) {
    string? secondId = runState.Act.SecondBossEncounter?.Id;
    if (!string.IsNullOrEmpty(secondId)) {
        pool.RemoveAll(e => e.Id == secondId);
    } else {
        TiLog.Warn("[boss-vote] HasSecondBoss true but SecondBossEncounter missing");
    }
}
```

**Verdict**: Should-do. Trivial, consistent with project failure philosophy.

### O10 — `Prepare` shouldn't overfit irrelevant fields (R2B only)

Spike item 5 mentions `NTreasureRoom._proceedButton` as a candidate `Prepare` reflection. R2B correctly notes: if the prefix doesn't actually USE `_proceedButton`, failing `Prepare` on its rename is brittle.

**Code validation**: ✅ Looking at `CardRewardVotePatch.Prepare:103-156`, only fields/methods the patch actually USES are reflected (`_options`, `_cardRow`, `_selectCardMethod`). The pattern is "verify what we depend on, not arbitrary class shape."

**Fix**: `BossVotePatch.Prepare` only checks `original` method signature (parameterless `OnProceedButtonPressed`). No private-field reflection unless the patch body actually reads one.

**Verdict**: Should-do. Spike item 5 reframed: identify only the fields/methods the patch body uses.

### O11 — Double-receipts (close + ignored) on cancellation path (R2B only)

If the resume liveness check fails, the chat sees BOTH:
1. Normal close receipt: `"Chat picked #1 X"` (fired by `VoteSession.CloseNowInternal` at line 231 before `ResumeOnMainThread` runs).
2. Then `SendCancellationReceipt()`: `"Vote result ignored — run abandoned during boss vote"`.

This is existing B.2.1 behavior, but the v2 spec's wording calls it "cancellation" which is slightly misleading.

**Verdict**: Should-do — rephrase to "ignored-result receipt sent after the normal close receipt." Matches what actually happens.

### O12 — 2-option dynamic plumbing verification (R2B only)

v2 commits to 2-option votes if pool is exactly 2. R2B asks for explicit verification every downstream path handles N options dynamically (no hardcoded `#2`).

**Verdict**: Should-do — add an explicit task to the implementation plan (when we write it next) to grep the patch + popup + receipt strings for hardcoded `#2` after coding.

### O13 — VoteTallyLabel + popup redundancy falsifiable threshold (R2A micro-nit)

v2 leaves "re-evaluate during operator validation if it's actually a viewer-side problem" — no concrete bar.

**Fix**: Add a Smoke A criterion: "Stream feed visual check — if both surfaces showing the same tally feels redundant on a typical viewer-aspect-ratio capture, raise as polish."

**Verdict**: Consider (lean yes; trivial).

---

## A.4 — Conflicts & Contradictions

### Conflict 1: Test seam claim (R2B) vs v2's existing description

R2B's catch overrides v2 inline. No reviewer disagrees; v3 fixes this.

### Conflict 2: 0-vote behavior (option A inherit vs option B override)

Neither reviewer explicitly conflicts — R2A asks the question, R2B doesn't address it. My judgment: inherit (option A). Consistent with other slices. Documented.

---

## A.5 — Recommended Changes (prioritized)

### Must-do (auto-apply)

1. **Fix `playerClickIndex` fallback** — replace with explicit "no winner → skip ApplyBossSwap, synthetic Proceed only" path (R2A + R2B, 2/2 consensus).
2. **Replace `HashCode.Combine` with stable FNV-1a** — introduce `BossVoteSeed.Stable(string, int) → int` helper, unit-tested for determinism (R2B; code-validated; breaks Smoke H if not fixed).
3. **Decouple testable seam from runtime hook** — `BossVoteResolver.ResolveWinner` for tests; `ApplyBossSwap` stays as runtime hook (R2B; code-validated).
4. **Introduce `BossVotePopupOption` DTO** — popup is MegaCrit-free; patch does the mapping (R2B; code-validated).
5. **Marshal popup cleanup through dispatcher** — `BossVotePopup` accepts `IMainThreadDispatcher`; handlers `_dispatcher.Post(SafeQueueFree)` (R2B).

### Should-do (auto-apply)

6. **Keyboard `ui_accept` / `ui_cancel` swallowing** via `_UnhandledInput` (R2B).
7. **Document tie-break + 0-vote inherited behavior** (R2A; code-validated against `VoteSession.cs:267-277`).
8. **`_isRelicCollectionOpen` spike item** (R2A).
9. **`HasSecondBoss` timing clarification** (R2A).
10. **`SecondBossEncounter` null-defensive handling** (R2B).
11. **`Prepare` reframe** — only reflect fields/methods the patch uses (R2B; code-validated).
12. **Double-receipt wording** (close + ignored) (R2B).
13. **2-option dynamic plumbing verification task** in the upcoming implementation plan (R2B).
14. **VoteTallyLabel + popup redundancy criterion** in Smoke A (R2A micro-nit).

### Reject (with reason)

None this round. Both reviewers raised valid concerns; the spec absorbs all of them.

---

## A.6 — What Stays

- All Must-do + Should-do changes from round 1 (v2 baseline).
- SecondBossEncounter pool exclusion (both reviewers explicitly endorse).
- Three-way prefix branch (`_resumeInProgress` → return true; `_voteInProgress` → return false; else acquire).
- `_Process` polling instead of `TallyChanged` subscription (R2A explicit praise).
- Tween fade-in drop (R2A explicit praise).
- Named `BossCandidateSampler` extraction (R2A explicit praise).
- Deferred suspend-and-resume base class extraction (R2A explicit praise).
- Smoke H save-reload test (R2A explicit praise — though it will only pass once `HashCode.Combine` is replaced).
- Optional Enhancements table at end (R2A explicit praise as "meta-documentation").
- All 9 spike items (R2A explicit praise on the expansion).
- Delegate-over-interface direction (R2A explicit praise).

---

## Part B — v3 spec produced separately as `2026-05-13-plan-b-3-boss-vote-design-v3.md`.
