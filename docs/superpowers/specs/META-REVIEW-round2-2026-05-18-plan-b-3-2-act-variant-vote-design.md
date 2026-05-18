# Meta-Review Round 2 — B.3.2 Act-Variant Vote Design

**Date**: 2026-05-18
**Input**: 3 external reviews of [v2 spec](2026-05-18-plan-b-3-2-act-variant-vote-design-v2.md)
**Output**: This meta-review + [v3 spec](2026-05-18-plan-b-3-2-act-variant-vote-design-v3.md)

---

## A.1 — Review Summary Table

| Reviewer | Sentiment | Key Focus Areas | Unique Insight |
|---|---|---|---|
| **R2-1** | Mixed; 5 crit, 11 medium | No-votes suppression unspecified; TI/Game seam violation in popup; L1/L3 mode propagation gap; cancellation-vs-no-votes ordering; fallback re-invoke unsafe | The custom no-votes receipt **doesn't actually suppress** the generic close receipt — design ships double-messages |
| **R2-2** | Mostly positive; v2 "near-shippable" | `session.Snapshot` API unverified; `_pending` static dead code; Spike #5 wrong example; Gate 14 thin | v1→v2 audit table; praises codebase finds (`IsInstanceValid`, 4:3 lock) as "worth more than the rest of the meta-review combined" |
| **R2-3** | Mostly positive | No-votes receipt races `FormatClose`; `ShouldBail` semantically diverged from `Prefix`; ESC + probe double-fire; gate count typo | `ShouldBail`'s `VoteInProgress`/`ResumeInProgress` branches are dead in production (atomic check is outside `ShouldBail`) — coverage illusion |

## A.2 — Consensus Points

| # | Issue | Reviewers | Severity |
|---|---|---|---|
| 1 | **No-votes receipt suppression mechanism is unspecified / appears broken** | R2-1, R2-2, R2-3 (**3/3**) | 🔴 Critical |
| 2 | **`session.Snapshot` access pattern doesn't exist** — must be spike-validated or pivoted | R2-1, R2-2 (**2/3**) | 🔴 Critical |
| 3 | **Gate count typo: text says 13, table lists 15** | R2-1, R2-3 (**2/3**) | 🟢 Trivial |
| 4 | **`ShouldBail` divergence from `Prefix` — coverage illusion** | R2-1, R2-3 (**2/3**) | 🟡 Medium |

## A.3 — Outlier Points Worth Keeping

- **R2-1 #2 (TI/Game seam violation in popup)** — codebase-verifiable: v2's `IsAbandonmentDetected()` lives in `ActVariantVotePopup` and references `NCharacterSelectScreen` (MegaCrit type). This contradicts the "popup public interface MegaCrit-free" rule from B.3.1 memo. Trivial fix: pass `Func<bool> shouldCancel` from patch.
- **R2-1 #3 (L1/L3 mode propagation gap)** — codebase-verifiable: spec computes `mode` in `PreWarmAssets` but never returns it; popup has no way to know which render mode to use. Critical for the `ForceL3PopupFallback` setting to work.
- **R2-1 #4 (cancellation can send no-votes receipt)** — real ordering bug: if `session.Cancel()` produces a snapshot with `NoVotesReceived = true`, `HandleVoteAsync` sends the no-votes receipt THEN `ResumeOnMainThread` sends the cancellation receipt. Two contradictory messages.
- **R2-1 #5 (fallback re-invoke unsafe)** — if `BeginRunLocally` partially mutated state before throwing, retry with `Act1 = "random"` could double-create state. Should be gated behind spike #4 outcome.
- **R2-2 M1 (`_pending` static is dead code)** — set in `PrefixContinue`, cleared in `finally`, but never **read** by anyone (everything uses the local `pending` capture). Confirmed: scan of v2 shows zero read sites.
- **R2-2 M2 (Spike #5 example wrong)** — `instance.GetParent()` doesn't apply to `StartRunLobby` (not a Node). Misleading.

---

## A.4 — Category Breakdown with Reality-Check

### 🏗️ Architecture & Design

| Item | Reviewers | Reality-check | Verdict |
|---|---|---|---|
| **No-votes suppression via custom `formatReceipt` callback** | R2-1, R2-2, R2-3 | **Codebase-verified**: [`VoteSession.cs:67`](../../../src/Ti/Voting/VoteSession.cs#L67) takes `Func<VoteSnapshot, ReceiptKind, string>? formatReceipt`. The Ti layer ALREADY supports per-session receipt substitution. v2's "send custom + suppress generic" mechanism was a non-existent feature; the clean fix is to pass a formatter that returns the custom text when `snapshot.NoVotesReceived` is true on `ReceiptKind.Close`, and delegates to `EnglishReceipts` for all other cases. **No Ti edits needed** — v2's "no Ti edits" claim stands once the mechanism is corrected. | ✅ Must-fix via formatReceipt |
| **`session.Snapshot` public access** | R2-1, R2-2 | **Codebase-verified**: `VoteSession` does NOT expose a public `Snapshot` property. The snapshot is constructed internally and passed to the `formatReceipt` callback. v2's pseudocode (`var snapshot = session.Snapshot`) would not compile. The fix is to inspect `NoVotesReceived` via the formatter callback, not via a property | ✅ Must-fix |
| **TI/Game seam violation in popup** | R2-1 | **Codebase-verified pattern**: B.3's `BossVotePopup` takes `Func<bool> isOccludingOverlayVisible` and `Func<bool> isRunDying` as constructor params, with the MegaCrit probes living in `BossVotePatch`. v2's `ActVariantVotePopup.IsAbandonmentDetected` violates this pattern by referencing `NCharacterSelectScreen` inside the popup | ✅ Must-fix |
| **`_pending` static is dead code** | R2-2 | **Codebase-verified by inspection**: only write sites in v2, no read sites. The local `pending` capture is what flows through closures | ✅ Remove |
| **L1/L3 mode not propagated to popup** | R2-1 | **Codebase-verified by reading v2 spec**: pre-warm computes mode for logging only; popup constructor doesn't receive it. `ForceL3PopupFallback` is therefore broken as written — the popup would see non-null asset paths and render L1 anyway | ✅ Must-fix |

### ⚠️ Risks & Concerns

| Item | Reviewers | Reality-check | Verdict |
|---|---|---|---|
| Cancellation-vs-no-votes ordering | R2-1 | Cancellation must dominate. If `session.Cancel()` sets `NoVotesReceived = true` (which is plausible — cancellation often happens with zero votes), both receipts would fire | ✅ Must-fix |
| Fallback re-invoke partial-state risk | R2-1 | Real concern. Gate behind Spike #4 outcome — if idempotent, keep; otherwise remove or guard | ✅ Should-fix |
| `_UnhandledInput` may be too late | R2-1 #12 | Godot's input order: `_Input` → focus owners' `_GuiInput` → `_UnhandledInput`. If a parent control consumes ESC first, popup never sees it. Use `_Input` and `AcceptEvent()` instead | ⚠️ Should-fix |
| `session.Cancel()` idempotency | R2-3 | Need codebase check or guard. With the `_userAbandoned` guard in popup, double-call is prevented; but the `Cancel()` itself should still be idempotent for defense | ⚠️ Add note to spike or guard locally |
| Fallback re-invoke + finally restoration mislead | R2-3 3.4 | If fallback fires with `winnerKey != "random"`, finally tries to restore previousAct1. The restoration is benign (run already in progress) but log is confusing. Fix: set `winnerKey = "random"` in fallback path | ✅ Should-fix |
| "Frozen behind" language | R2-1 #13 | Mouse-blocked != frozen. Soften to "mouse interaction blocked; ESC cancels vote" | ✅ Apply |

### 🔧 Implementation Details & Nits

| Item | Reviewers | Verdict |
|---|---|---|
| Gate count "13" → "15" | R2-1, R2-3 | ✅ Apply (trivial) |
| Asset count "8 total" → "4 total" | R2-1 #9 | ✅ Apply (trivial) — there are 2 backgrounds + 2 banners = 4 |
| Spike #5 example fix (`instance.GetParent()`) | R2-2 M2 | ✅ Apply — replace with scene-tree/screen-state probe candidates |
| Gate 14 sub-checks (4 explicit verifications) | R2-2 M3 | ✅ Apply — given user's 4:3 emphasis, hardening is justified |
| `_multiplayerWarnFired` reset or annotate | R2-2 L1 | ✅ Annotate as intentional process-lifetime |
| `previousAct1` always-random note | R2-2 L2 | ✅ Annotate; don't simplify (defensive symmetry) |
| Forced-L3 log normalize | R2-2 L3 | ✅ Apply |
| Stale "Act 1 variant vote" in receipts section | R2-1 nit | ✅ Apply |
| Hex format spec for `FallbackColorHex` | R2-1 nit | ✅ Add (RRGGBB only) |
| `ForceL3PopupFallback` log on vote-open | R2-1 nit | ✅ Add (cheap diagnostic) |

### 🔄 Alternative Approaches

| Alternative | Verdict |
|---|---|
| **Ti-layer `NoVotesBehavior` enum** (R2-1 Alt 1) | ❌ Reject — `formatReceipt` callback already supports this. No Ti edits needed |
| **Accept vote-random on no votes** (R2-1 Alt 2) | ❌ Reject — violates explicit Goal "vanilla random pick stands" |
| **Postfix `GetRandomList`** (R2-1 Alt 3 as Plan B) | ⚠️ Keep as documented Plan B if Spike #6 (Act1 read-site validation) fails. Already in v2's "Alternative" section |
| **`readonly record struct PendingActVariantVote`** (R2-3 5.1) | ❌ Reject — needs reference semantics for cross-callback flag write |

### ✅ Confirmed Good (universal approval continues)

All previously-confirmed elements remain solid:
- `Act1` write-then-reinvoke override seam
- Suspend-and-resume pattern
- TI/Game seam (with the popup fix from this round)
- L3 fallback strategy (binary all-or-nothing)
- Forward-compatibility framing
- Hardcoded 2-element pool
- Pre-warm BOTH variants
- Direct path construction
- Gate 11 save-quit preservation
- Gates 12, 13, 14, 15 added this round
- 4:3 gameplay-area anchoring (highlighted as critical by R2-2)

---

## A.5 — Conflicts & Contradictions

**Conflict 1**: `ShouldBail` scope.
- R2-1 #10 and R2-3 3.2 both flag the divergence (resolver's `VoteInProgress`/`ResumeInProgress` branches are dead in production).
- R2-3 8.2 suggests removing those enum values entirely.
- **Recommendation**: prune `VoteInProgress` and `ResumeInProgress` from `BailReason`. Document that those bail paths are handled inline in `Prefix` (atomic semantics require it) and verified by Gates 7, 12 instead of unit tests.

**Conflict 2**: Fallback re-invoke retention.
- R2-1 #5: remove unless spike validates idempotency.
- R2-3 3.4: keep but align winnerKey to "random" so finally doesn't mislead.
- **Recommendation**: keep the fallback (it's the only defense against player softlock at character-select) but (a) align `winnerKey = "random"` in the fallback path so finally semantics are clean, AND (b) Spike #4 outcome may downgrade to remove. The fallback is conditional on spike acceptance.

## A.6 — Recommended Plan Changes

### Must-do (8)

| # | Change | Reviewers |
|---|---|---|
| M1 | **Switch from `session.Snapshot` access to custom `formatReceipt` callback for no-votes detection.** Pass a callback to `coordinator.Start(...)` that returns the custom no-votes close text when `snapshot.NoVotesReceived` is true; delegates to `EnglishReceipts` for all other cases. No Ti edits needed | R2-1, R2-2, R2-3 |
| M2 | **Read `noVotes` outcome via shared state set by the formatter callback** instead of `session.Snapshot`. Use a captured `VoteOutcome` local that the formatter writes to side-channel-style, or read `WinnerIndex` + a flag captured from inside the callback | R2-1, R2-2 |
| M3 | **Move abandonment probe from popup to patch.** Popup constructor takes `Func<bool> shouldCancel`; `IsAbandonmentDetected` is a private patch method that touches `NCharacterSelectScreen` | R2-1 |
| M4 | **Propagate L1/L3 mode to popup.** Add `internal enum ActVariantPopupMode { L1Textures, L3Fallback }`. `PreWarmAssets` returns `ActVariantPrewarmResult(Mode, Succeeded, Total, ElapsedMs)`. Popup constructor takes `mode`; ignores asset paths in `L3Fallback` | R2-1 |
| M5 | **Cancellation dominates no-votes in receipt-send order.** In `HandleVoteAsync`, check `Volatile.Read(ref pending.Cancelled)` BEFORE sending the no-votes receipt. If cancelled, skip no-votes entirely; `ResumeOnMainThread` sends cancellation receipt | R2-1 |
| M6 | **Remove `_pending` static field.** Use only the local `pending` captured through closures and method parameters | R2-2 |
| M7 | **Fix gate count typo: "13 gates total" → "15 gates total"** | R2-1, R2-3 |
| M8 | **Resolve `ShouldBail` divergence.** Prune `BailReason.VoteInProgress` and `BailReason.ResumeInProgress` from the resolver (they require atomic-acquire semantics that pure functions can't express); document that those bail paths are verified by Gates 7, 12, not unit tests | R2-1, R2-3 |

### Should-do (10)

| # | Change | Reviewers |
|---|---|---|
| S1 | Spike #5 example fix — replace `instance.GetParent()` with scene-tree/screen-state probe candidates | R2-2 |
| S2 | Strengthen Gate 14 with 4 explicit sub-checks (no squish, no banner bleed, font scaling, CanvasLayer parent verification) | R2-2 |
| S3 | Asset count fix — "4 total assets (2 backgrounds + 2 banners)" instead of "8 total" | R2-1 |
| S4 | Fallback re-invoke aligns `winnerKey = "random"` in fallback path so finally restoration is consistent | R2-3 |
| S5 | "Frozen behind" wording softened to "mouse interaction blocked; ESC cancels vote" | R2-1 |
| S6 | Annotate `_multiplayerWarnFired` as intentional process-lifetime suppression | R2-2 |
| S7 | Annotate `previousAct1` as defensive-symmetry capture (always "random" in current bail order) | R2-2 |
| S8 | Normalize forced-L3 log line format to match standard pre-warm telemetry | R2-2 |
| S9 | Fix stale "Act 1 variant vote" reference in Receipts section header text | R2-1 |
| S10 | Specify `FallbackColorHex` accepted format: 6-digit `RRGGBB` only, no `#`, no alpha | R2-1 |

### Consider (deferred)

C1. Add Spike→Gate dependency table (R2-2 ADD1)
C2. Log `seed` parameter on resume for debugging (R2-3 6.2)
C3. Multiplayer regression gate (R2-2 ADD2)
C4. Rename `ForceL3PopupFallback` to `ForceTextOnlyVariantPopup` (R2-2 L7)
C5. `_Input` instead of `_UnhandledInput` for ESC (R2-1 #12)
C6. Read-only chat receipt-gate scoping (R2-1 #14)

### Reject

- **Ti-layer `NoVotesBehavior` enum** — `formatReceipt` callback supports this without Ti edits.
- **Accept vote-random on no votes** — violates explicit Goal.
- **`readonly record struct PendingActVariantVote`** — needs reference semantics.

## A.7 — What Stays

Universal approval from all 3 reviewers (plus the unanimous round-1 baseline):

1. `Act1` write-then-reinvoke override seam
2. Suspend-and-resume Harmony pattern
3. TI/Game seam discipline (with the popup probe fix from this round)
4. L3 fallback strategy (binary all-or-nothing)
5. Forward-compatibility framing
6. Hardcoded 2-element pool, no unlock gating
7. Pre-warm BOTH variants on main thread before popup opens
8. Direct path construction over `AssetPaths` enumeration
9. Gate 11 save-quit preservation
10. New Gates 12, 13, 14, 15 from round 1 + this round
11. 4:3 gameplay-area anchoring (R2-2 specifically praised this as load-bearing)
12. Captured-modifiers copy at PrefixContinue
13. `<!-- CHANGED -->` annotation style (auditable, doesn't pollute structural reading per R2-2)

---

## Summary

Round 2 surfaced one **load-bearing codebase truth** that the v2 spec missed: `VoteSession` already supports per-session receipt substitution via the `formatReceipt` callback parameter. The "send custom + suppress generic" mechanism v2 described doesn't exist — but the **right** mechanism (substitute via callback) does, and v2's "no Ti edits" claim is preserved once we pivot. Three reviewers independently triangulated this gap.

The other must-fix items are smaller refinements (popup seam, mode propagation, cancellation ordering, dead `_pending` field, gate count typo, `ShouldBail` divergence). All 8 must-do changes are surgical edits, not architectural shifts.

The v3 plan applies 8 must-do + 10 should-do changes inline. The 6 Consider-tier items are surfaced for explicit user selection at the end of v3.
