# Meta-Review — B.3.2 Act-Variant Vote Design

**Date**: 2026-05-18
**Input**: 9 external reviews of [`2026-05-18-plan-b-3-2-act-variant-vote-design.md`](2026-05-18-plan-b-3-2-act-variant-vote-design.md)
**Output**: This meta-review + [`2026-05-18-plan-b-3-2-act-variant-vote-design-v2.md`](2026-05-18-plan-b-3-2-act-variant-vote-design-v2.md)

---

## A.1 — Review Summary Table

| Reviewer | Sentiment | Key Focus Areas | Unique Insight |
|---|---|---|---|
| **R1** | Mixed (14 concerns, 7 high) | No-vote semantics, `Act1` persistence, cancellation flag, DTO nullability, orphan-session cleanup | Distinguish 4 outcome types (winner/tie/no-votes/cancelled) via dedicated enum on resume side |
| **R2** | Strongly critical on 3 items | UI/scene transition desyncs, `IsInstanceValid` lifecycle, multiplayer bail | `IsInstanceValid` on a non-`GodotObject` lobby always returns `true` — the cancellation probe is dead |
| **R3** | Mixed; 3 material bugs | No-votes receipt factually wrong, `_voteInProgress` catch-leak, layout per-column degradation | `half_width / 2` banner centering typo |
| **R4** | Mixed; 3 must-fix | Caller-continuation gap, **`PanelContainer` layout bug**, reflective re-invoke softlock | **Critical**: `PanelContainer` is a `Container` that sequentially stacks children — the spec's tree renders wrong |
| **R5** | Mixed; 5 concerns | `Act1` single-read needs runtime validation, asset paths absent, `_resumeInProgress` race window | Use a postfix on `BeginRunLocally` to log `Act1` reads during normal vanilla flow as the validation gate |
| **R6** | Mostly positive (brief) | `Act1` dependency, ultrawide aspect, reflection side effects | Add a `ModSetting` debug toggle to force L3 fallback for UI testing without finding textures |
| **R7** | Mixed; 5 concerns | Cancellation race (post-resolve, pre-resume), `[Collection]` contradiction, `AssetPaths` location | Gate 12 for Embark→ESC→Embark rapid cycle |
| **R8** | Sharpest critique | **`AssetPaths` name collision** with `ActModel.AssetPaths`, `Cache.GetTexture` sync unverified, bail-order ordering hole, testability inconsistency | Slice label `"Act 1 variant"` (drop "vote") for cleaner receipt interpolation |
| **R9** | Mixed; 2 high, 4 medium | `BeginRunLocally` idempotency, `_voteInProgress`/`_resumeInProgress` re-entrancy window, pre-warm timeout, DTO nullability, popup `_Process` not shown | Suggests collapsing the two flags into one |

---

## A.2 — Consensus Points (ranked by count)

| # | Issue | Reviewers | Severity |
|---|---|---|---|
| 1 | **No-votes outcome handling is incorrect** — `VoteSession` synthesizes a random winner index even when no votes arrive (confirmed via `EnglishReceipts.FormatClose`'s `NoVotesReceived` branch); our resolver would treat that as a real winner and override `Act1`, violating the "vanilla random pick stands" goal | R1, R3, R5, R7, R8, R9 (**6**) | 🔴 Critical |
| 2 | **Bail logic extraction for testability** — `ActVariantVotePatch` is excluded from test compile but spec claims Prefix is unit-testable; the bail logic is the meatiest part of the patch and is currently untestable | R1, R4, R5, R8, R9 (**5**) | 🟠 High |
| 3 | **DTO asset paths must be `string?`** — spec declares them `string` but L3 fallback requires nullable; `ActVariantVoteCandidatesTests` also contradicts L3 by asserting non-null | R1, R4, R5, R8, R9 (**5**) | 🟠 High |
| 4 | **`IsInstanceValid` cancellation probe is unverified / likely broken** — `StartRunLobby` is almost certainly not a `GodotObject` (it's a session manager in `MegaCrit.Sts2.Core.Multiplayer.Game.Lobby`); the probe would always return `true`, making cancellation dead code | R1, R2, R3, R4 (**4**) | 🔴 Critical |
| 5 | **`Act1` should be restored to `"random"` after re-invoke (one-shot)** — current spec never restores; if `StartRunLobby` is reused, the next Embark would silently skip the vote thinking it's an explicit pin | R1, R2, R4, R9 (**4**) | 🟠 High |
| 6 | **`Act1` single-read assumption needs runtime validation** — spec asserts but flagged as open item; sole basis for the override mechanism | R1, R5, R6, R9 (**4**) | 🟠 High |
| 7 | **`_voteInProgress` leak in outer catch / Dispatcher.Post failure** | R1, R3, R9 (**3**) | 🟡 Medium |
| 8 | **`BeginRunLocally` idempotency on re-invoke** — does it set instance fields before line 411 that affect the second call? | R4, R5, R9 (**3**) | 🟡 Medium |
| 9 | **Explicit cancellation state separate from object validity** (Reviewer 1's "PendingActVariantVote") | R1, R3, R4 (**3**) | 🟡 Medium |
| 10 | **Caller-continuation gap on `BeginRunLocally`** — what does the caller do after the prefix returns? Could it navigate away while the popup is up? | R2, R4 (**2**) | 🟠 High |
| 11 | **`half_width / 2` banner centering typo** — should be `half_width` or `column_width / 2` | R3, R4 (**2**) | 🟢 Low |
| 12 | **`SendCancellationReceipt` implementation underspecified** | R4, R9 (**2**) | 🟢 Low |
| 13 | **L3 fallback policy: per-column vs all-or-nothing undefined** | R3, R8 (**2**) | 🟡 Medium |
| 14 | **`AssetPaths` static class location undefined** | R7, R8 (**2**) | 🟢 Low |

---

## A.3 — Outlier Points Worth Keeping

Single-reviewer items that survive scrutiny:

- **R4: `PanelContainer` layout bug** — codebase-verifiable, **critical**. `PanelContainer extends Container`, which sequentially arranges children. The spec's tree (background, banner, tally as siblings inside `PanelContainer`) would render stacked vertically, not overlaid. **Must-fix.**
- **R8: `AssetPaths` name collision with `ActModel.AssetPaths`** — the very landmine the context doc flags. Trivial rename (`ActVariantAssetPaths`). Cognitive collision is real. **Must-fix.**
- **R8: Bail-order ordering hole** — chat-disconnect between click 1 and click 2 could let click 2 bail at #3 (chat-readable) and run vanilla while click 1's vote is in flight. Move `_voteInProgress` check higher. **Must-fix.**
- **R2: Multiplayer bail** — spec explicitly omitted "per non-goals." Single line, defensive, matches `BossVotePatch.TryGetPlayerCount` precedent. Even single-player runs go through `BeginRunLocally`; an MP run would silently mutate `Act1` host-side which could desync clients if MP is ever re-enabled. **Should-do.**
- **R7: Cancellation race (post-resolve, pre-resume)** — subtle, real, low impact (just a confusing log message). **Should-do** (log distinction).
- **R6: Debug toggle to force L3** — useful for UI testing during the asset spike. **Consider.**
- **R9: Pre-warm timeout** — defensive against slow-disk regressions; cheap. **Consider.**

---

## A.4 — Category Breakdown with Reality-Check

### 🏗️ Architecture & Design

| Item | Reviewers | Reality-check | Verdict |
|---|---|---|---|
| The `Act1` write-then-reinvoke seam is correct | All 9 | Confirmed against decompile: `Act1` is a trivial `public string` auto-property; line 412 is the single read site | ✅ Keep |
| `BeginRunLocally` idempotency unverified | R4, R5, R9 | Real concern — needs spike validation. From the decompile, `BeginRunLocally` calls `GetRandomList`, applies `Act1` override, builds players, calls `LobbyListener.BeginRun` (the actual launch). The pre-411 work is minimal (creates `Rng` from seed). Likely safe but should be operator-verified | ⚠️ Add explicit verification step |
| Caller-continuation gap | R2, R4 | The caller is the lobby `BeginRun(...)` flow per `StartRunLobby.cs:438`: `LobbyListener.BeginRun(seed, list, modifiers)`. The actual scene-transition happens INSIDE the `BeginRunLocally` body (via `LobbyListener.BeginRun → RunManager.SetUpNewSinglePlayer`). When we prefix-return-false, neither the listener call nor the scene transition happens. ✅ The caller doesn't run anything that conflicts | ✅ Lower severity than reviewers thought |
| Alternative: postfix on `GetRandomList` | R1 alt 2, R4 alt 3, R5 alt 1, R7 alt 1, R8 A1, R9 alt A | Multiple reviewers reject this for the same reasons the spec rejected it. Confirmed | ❌ Reject |
| Generic act-transition trigger today (YAGNI) | All 9 agree YAGNI | — | ❌ Reject |

### ⚠️ Risks & Concerns

| Item | Reviewers | Reality-check | Verdict |
|---|---|---|---|
| **No-votes semantic bug** | R1, R3, R5, R7, R8, R9 | **Codebase-verified**: `EnglishReceipts.FormatClose` line 30-31 explicitly handles `s.NoVotesReceived` branch, meaning `VoteSession` synthesizes a winner index even with zero votes. Our `winnerIndex >= 0 && winnerIndex < candidates.Count` check would pass with a synthesized index → `Act1` would be set → vanilla random is overridden. The "vanilla random pick stands" goal is violated | ✅ Must-fix |
| **`IsInstanceValid` cancellation is broken** | R1, R2, R3, R4 | `StartRunLobby` is not a `GodotObject` (it's in `Core.Multiplayer.Game.Lobby`, inherits from a non-Godot session class). `GodotObject.IsInstanceValid(non-GodotObject)` returns `true` for any non-null reference. The cancellation probe is dead code | ✅ Must-fix |
| **Layout bug in popup tree** | R4 | `PanelContainer extends Container`. Containers force layout on children. The spec's three siblings (TextureRect bg, TextureRect banner, Label) would stack vertically, not overlay. Must insert a `Control` between `PanelContainer` and the overlay children to opt out of container layout | ✅ Must-fix |
| **DTO nullability vs L3 fallback** | R1, R4, R5, R8, R9 | Spec says `string BackgroundPath` (non-null) but L3 fallback needs null. With NRT enabled, this is a type-system contradiction | ✅ Must-fix |
| **`_voteInProgress` re-entrancy window** | R9 | Real but narrow: spam-click during synthetic re-invoke would see `_resumeInProgress == 1` and let through. Same race exists in B.3's `BossVotePatch`. Practical risk is low because the synthetic invoke is sync (sub-frame window). Worth a comment, not a redesign | ⚠️ Document, not fix |
| **`_voteInProgress` leak in outer catch** | R1, R3, R9 | The `Dispatcher.Post(() => ResumeOnMainThread(...))` could theoretically throw under process teardown. The outer catch in `HandleVoteAsync` doesn't reset `_voteInProgress`. Asymmetric with `PrefixContinue`'s catch | ✅ Must-fix (one-line defensive add) |
| **Bail-order ordering hole** | R8 | Real: if chat disconnects between click 1 (vote in flight) and click 2, click 2 bails at chat-readable check (#3) and lets vanilla run while our vote is still going. The `_voteInProgress` atomic check (#6) should be higher | ✅ Must-fix |
| Cancellation race post-resolve pre-resume | R7 | Real, narrow. Same vulnerability in B.3. Mitigation: log distinction. Low impact | ⚠️ Should-do (log only) |

### 🗑️ Suggested Removals / Simplifications

| Item | Reviewers | Reality-check | Verdict |
|---|---|---|---|
| Remove "Only new voting-layer addition is the receipt-string set" from Goals | R1, R8 (8.1) | Stale wording from before commit `0b2131e`. True | ✅ Apply |
| Remove "Asset path fields are non-null" from `ActVariantVoteCandidatesTests` | R1 | Contradicts L3 fallback | ✅ Apply |
| Remove unit-testability claim for Prefix | R1, R8 | Contradicts test-compile exclusion. Fix is to extract bail logic; if not extracted, remove the claim | ✅ Apply (replaced by extraction) |
| Pool degeneracy guard (bail #5) is over-defensive | R7 | Cheap, fires only on code-change to `BuildCandidates`. Net neutral | ❌ Keep |
| Collapse `_voteInProgress` and `_resumeInProgress` | R9 | They serve distinct purposes (in-progress flag vs synthetic-resume passthrough). Established pattern from B.3. Diverging would create inconsistency | ❌ Reject |

### ➕ Suggested Additions / Features

| Item | Reviewers | Verdict |
|---|---|---|
| Multiplayer bail | R2 | ✅ Apply (cheap, defensive, matches `BossVotePatch` pattern) |
| Gate 12: Embark→ESC→Embark cycle | R7 | ✅ Apply (specific atomic-reset coverage) |
| Gate: chat-disconnect mid-vote | R9 | ✅ Apply (distinct from existing gates) |
| Verify `Cache.GetTexture` is synchronous (spike deliverable) | R8 | ✅ Apply (could silently break Gate 8) |
| Verify `BeginRunLocally` idempotency (spike deliverable) | R4, R5, R9 | ✅ Apply |
| Verify `StartRunLobby` lifecycle for cancellation probe (spike deliverable) | R1, R2, R3, R4 | ✅ Apply |
| Show `_Process` polling method | R9 | ✅ Apply (completeness) |
| Show `SendCancellationReceipt` implementation | R4, R9 | ✅ Apply (5 lines, matches B.3 pattern) |
| `MapBgColor` fallback color in DTO | R1 | ✅ Apply (as hex string per CLR-only DTO requirement) |
| Debug-mode L3 force toggle | R6 | 💡 Consider |
| Pre-warm timeout/degradation | R9 | 💡 Consider |
| Aspect-ratio awareness | R6, R8 | 💡 Consider |
| Twitch rate-limit interaction note | R8 | 💡 Consider |
| Receipt label `"Act 1 variant"` (drop "vote") | R8 | 💡 Consider |
| `VoteOnActVariant` naming granularity decision | R8 | 💡 Consider |
| Postfix runtime validation of `Act1` reads | R5 | 💡 Consider |
| Verify `BeginRunLocally` has only one call site | R8 ADD1 | 💡 Consider |
| `TargetInvocationException` unwrap in catch | R7, R8 | 💡 Consider |
| Subscribe to `TallyChanged` in popup constructor | R8 L3 | 💡 Consider |

### 🔄 Alternative Approaches

All reviewers who proposed alternatives ended up rejecting them or marking as fallback-only. The `Act1` write-then-reinvoke is unanimously the right primary approach. ✅ No change to fundamental design.

### ✅ Confirmed Good

All 9 reviewers approve:
- `Act1` override hook as the seam (vs intercepting `GetRandomList`)
- Suspend-and-resume pattern
- TI/Game seam discipline
- L3 fallback strategy
- Forward-compatibility framing ("~1 day per future variant", not zero)
- Hardcoded 2-element pool, no unlock gating
- No `EnglishReceipts.cs` edits (generic formatters work)
- Pre-warm BOTH variants
- Direct path construction over `AssetPaths` enumeration
- Gate 11 (save-quit preservation)
- No idempotency marker (each Embark is a fresh start)

### 🔧 Implementation Details & Nits

- `half_width / 2` typo (R3, R4) — must-fix
- `AssetPaths` name (R7, R8) — must-fix (rename + locate)
- `[Collection("TiLog.Sink")]` carve-out for resolver tests (R7) — must-fix (one sentence)
- `string.Equals(Act1, "random", StringComparison.Ordinal)` (R1) — consider
- `ActVariantOption` as `record struct` explicit declaration (R9) — consider
- `_beginRunLocallyMethod` naming (R4) — nit, skip
- `internal const string` location for asset paths (R7, R8) — must-fix (locate it)
- Modifier list copy at prefix time (R1, R4) — should-do (one line, defensive)

### 📦 Dependencies & Integration

| Item | Reviewers | Verdict |
|---|---|---|
| `PreloadManager.Cache.GetTexture` sync verification | R8 | ✅ Must-fix (spike deliverable) |
| `VoteSession.AwaitWinnerAsync` return-on-cancel behavior | R1, R7, R9 | ⚠️ Affected by no-votes fix (M1) |
| `Voter.Default` documented in CONTEXT | R3 | ⚠️ Apply to CONTEXT doc (separate from spec) |
| `BeginRunLocally` other call sites audit | R8 ADD1 | 💡 Consider |

### 🔮 Future Considerations

- `VoteOnActVariant` granularity when Act 2 variants ship (R8)
- Aspect-ratio reflow on viewport resize (R6, R8)
- Global vote-receipt rate-limit policy across slices (R8)
- `WasVanillaRandom` flag on `VoteSnapshot` for better receipts (R5)

---

## A.5 — Conflicts & Contradictions

**Conflict 1: L3 fallback — per-column vs all-or-nothing.**
- R3 argues all-or-nothing (avoid one-pretty-one-ugly visual split).
- R8 argues per-column (strictly richer fallback at no widget cost).
- **My recommendation**: all-or-nothing. Mixed quality is more jarring than uniform L3. The implementation cost difference is negligible. R3 wins.

**Conflict 2: `_resumeInProgress` redundancy.**
- R9 suggests collapsing into single `_voteInProgress` flag.
- R8 / R7 / R4 / R3 / R1 implicitly accept the two-flag pattern as inherited from B.3.
- **My recommendation**: Keep both. They serve distinct purposes; collapsing would diverge from the proven B.3 pattern and introduce inconsistency.

**Conflict 3: Cancellation strategy.**
- R1 wants an explicit `Cancelled` atomic flag separate from `IsInstanceValid`.
- R3 / R4 want a different probe (parent-in-tree, screen-visibility).
- R2 wants `RunManager.CurrentState` or equivalent.
- **My recommendation**: All three reviewers correctly identify that `IsInstanceValid` is broken. Combine: (a) explicit cancellation flag (R1's approach) AND (b) a meaningful probe identified during the spike (R3/R4 direction). The spike's deliverable is the probe; the explicit flag is the safety net.

**Conflict 4: Pre-warm timeout.**
- R9 wants a hard 200ms cap with degradation.
- Other reviewers don't address this; spec assumes ≤ 100ms envelope.
- **My recommendation**: Surface as `Consider`. The B.3.1 baseline was 76–82ms; if real-world measurement shows multi-frame stalls, easy to add later. YAGNI today.

---

## A.6 — Recommended Plan Changes

### Must-do (12)

| # | Change | Reviewers |
|---|---|---|
| M1 | **Fix no-votes outcome detection**. Read `VoteSession`'s post-close state (e.g., `session.Snapshot.NoVotesReceived` or equivalent) to distinguish "no votes → keep vanilla" from "votes cast → apply winner." Implementation must confirm the exact API surface during the spike | R1, R3, R5, R7, R8, R9 |
| M2 | **Fix popup tree structure**. Insert a `Control` (anchors full-rect, mouse_filter = Ignore) inside each `PanelContainer` as a free-positioning parent for the overlay children. `PanelContainer` is a `Container` and forces sequential layout otherwise | R4 (codebase-verified) |
| M3 | **Make `Act1` write one-shot**. Capture `previousAct1 = instance.Act1` before write; restore in `finally` after `method.Invoke` | R1, R2, R4, R9 |
| M4 | **DTO asset paths must be `string?`**. Update `ActVariantOption` record fields, update `ActVariantVoteCandidatesTests` (remove "non-null" assertion) | R1, R4, R5, R8, R9 |
| M5 | **Add fallback color to DTO**. `string FallbackColorHex` (matches vanilla's `Color("9F95A5")` convention, test-csproj-friendly — no `Godot.Color` dependency in the DTO so it can stay in the test compile via surgical include) | R1 (forced by M4 + L3) |
| M6 | **Extract bail logic to pure helper in `ActVariantVoteResolver`**. Signature: `BailReason? ShouldBail(bool resumeInProgress, bool settingsEnabled, ChatConnectionState chatState, string act1, int candidateCount, bool voteInProgress)`. Patch's Prefix calls this; tests cover all branches | R1, R4, R5, R8, R9 |
| M7 | **Replace `IsInstanceValid` cancellation probe**. Add explicit `Cancelled : int` atomic on a shared state object; popup sets it on ESC/screen-change; resume checks it. Identify a real run-start-abandonment probe during the spike (deliverable) | R1, R2, R3, R4 |
| M8 | **Add multiplayer bail**. Check `__instance.Players.Count > 1` (or `NetService.Type.IsMultiplayer()`) and bail to vanilla — matches `BossVotePatch.TryGetPlayerCount` defensive pattern | R2 |
| M9 | **Rename `AssetPaths` to `ActVariantAssetPaths`** and explicitly locate it (nested static class inside `ActVariantVoteResolver`, populated during the spike) | R7, R8 |
| M10 | **Reorder bail conditions**: move `_voteInProgress` atomic check up to position #2 (after synthetic-resume, before settings) to close the chat-disconnect race | R8 |
| M11 | **Reset `_voteInProgress` in `HandleVoteAsync`'s outer catch**. Add `finally { Interlocked.Exchange(ref _voteInProgress, 0); }` for symmetry with `PrefixContinue`'s catch | R1, R3, R9 |
| M12 | **Spike deliverables expand**: (a) verify `Cache.GetTexture` is synchronous; (b) verify `BeginRunLocally` is idempotent on re-invoke (no side effects pre-411 that affect a second call); (c) identify run-start-abandonment probe to replace `IsInstanceValid`; (d) verify `Act1` is read exactly once (postfix-log during a vanilla run) | R1, R2, R4, R5, R6, R8, R9 |

### Should-do (11)

| # | Change | Reviewers |
|---|---|---|
| S1 | Fix `half_width / 2` banner centering typo → `half_width` (or `column_width / 2`) | R3, R4 |
| S2 | Show `_Process` polling implementation in spec for completeness | R9 |
| S3 | Show `SendCancellationReceipt` implementation (5 lines, matches `BossVotePatch.SendIgnoredResultReceipt`) | R4, R9 |
| S4 | Add Gate 12: Embark→ESC→Embark cycle (covers atomic-reset and re-entry into prefix) | R7 |
| S5 | Add Gate 13: chat-disconnect mid-vote (covers `AwaitWinnerAsync` behavior under chat dropout) | R9 |
| S6 | Add log distinction for "vote completed but resume aborted" vs "vote timed out" cancellation paths | R7 |
| S7 | All-or-nothing L3 degradation: if any of 4 assets fails pre-warm, all columns degrade. Document the policy explicitly | R3 (consensus over R8 per-column) |
| S8 | Modifier list copy at prefix time: `var capturedModifiers = modifiers.ToList()` before HandleVoteAsync | R1, R4 |
| S9 | `[Collection("TiLog.Sink")]` carve-out for `ActVariantVoteResolverTests` — pure CLR, no logging. Fix the spec contradiction | R7 |
| S10 | Fallback re-invoke on reflective failure: log Error, reset `Act1`, attempt one more invoke. Prevents soft-locked player at character-select | R4 |
| S11 | Remove stale Goals line: "Only new voting-layer addition is the receipt-string set" (contradicts no-Ti-edits posture from commit `0b2131e`) | R1, R8 |

### Consider (deferred to optional enhancements section)

C1. Receipt label `"Act 1 variant"` (drop "vote") — R8
C2. Debug `ModSetting` to force L3 fallback for UI testing — R6
C3. Pre-warm timeout/degradation (≤ 200ms wall-clock cap) — R9
C4. `VoteOnActVariant` granularity — rename to `VoteOnAct1Variant` or document single-toggle policy — R8
C5. Twitch rate-limit interaction note in "Open items / risks" — R8
C6. Postfix on `BeginRunLocally` for runtime `Act1`-read validation during spike — R5
C7. Subscribe to `session.TallyChanged` in popup constructor not `Show()` — R8 L3
C8. Log unwrap `TargetInvocationException` from `Method.Invoke` — R7, R8
C9. Aspect-ratio awareness for ultrawide monitors — R6, R8
C10. Verify `BeginRunLocally` has no other call sites — R8 ADD1
C11. `string.Equals(Act1, "random", StringComparison.Ordinal)` — R1
C12. Explicit `readonly record struct` declaration for `ActVariantOption` — R9
C13. `Voter.Default` documented in CONTEXT doc — R3 (CONTEXT-side, not spec)

### Reject (with reason)

- **Postfix `GetRandomList` alternative** — multiple reviewers agree this is worse (RNG already drawn, logic duplication, doesn't fit suspend-and-resume model). Spec was right.
- **Transpiler on line 412** — fragile across game updates, project doesn't use transpilers as convention.
- **Generic act-transition trigger today** — YAGNI, all 9 reviewers confirm.
- **Collapse `_voteInProgress` and `_resumeInProgress`** (R9) — they serve distinct purposes; diverging from B.3's proven pattern adds inconsistency without clear gain.
- **Patch `NCharacterSelectScreen.Embark`** — further from data, multiple reviewers reject. Spec was right.
- **Per-column L3 degradation** (R8 M1) — mixed-quality visual split is worse UX than uniform L3. Going with R3's consensus position.

---

## A.7 — What Stays (Universal Approval)

The following are confirmed solid by all 9 reviewers and should NOT be touched:

1. **`Act1` write-then-reinvoke override seam** — co-opting vanilla's existing override hook is the right primary approach.
2. **Suspend-and-resume Harmony pattern** — proven across B.1/B.2.1/B.3.
3. **TI/Game seam discipline** — `ActVariantOption` MegaCrit-free, popup MegaCrit-free at public interface.
4. **L3 fallback strategy** — text-only column with `MapBgColor` is the right risk mitigation.
5. **Forward-compatibility framing** — "~1 day per future variant, NOT zero" is the right honesty.
6. **Hardcoded 2-element pool with no unlock gating** — design under `unlock all`, matches B.3 philosophy.
7. **No `EnglishReceipts.cs` edits** — generic formatters work as-is (with the one exception for no-votes fix M1).
8. **Pre-warm BOTH variants on main thread before popup opens** — necessary because `LoadActAssets` hasn't run yet.
9. **Direct path construction over `AssetPaths` enumeration** — B.3.1's lesson generalized correctly.
10. **Gate 11 (save-quit preservation)** — folded in from B.3.1 memo, correctly justified.
11. **No idempotency marker** — each Embark is a fresh run-start; no Golden Compass / back-arrow re-fire path.
12. **`plan-b-3-2/N.M:` commit prefix** — already in CLAUDE.md.
13. **`voteOnActVariant: bool` settings field, default true, no schema bump.**
14. **30s vote duration matching other slices.**

---

## Summary

The design is fundamentally sound — all 9 reviewers approve the `Act1` override seam, the suspend-and-resume pattern, the TI/Game seam, and the L3 fallback. The bulk of the must-do changes are correctness fixes around the no-votes outcome path (consensus 6 reviewers), the cancellation probe (4 reviewers found that `IsInstanceValid` on a non-`GodotObject` is dead code), the layout tree bug (codebase-verified), DTO nullability, and testability via bail-logic extraction. The 11 should-do items are mostly defensive cleanups and explicit gates. Nothing in the consensus list challenges the fundamental design — every reviewer wants this slice built, just with these specific tightenings.

The updated plan (v2) auto-applies all 12 must-do and 11 should-do changes inline with `<!-- CHANGED -->` annotations. The 13 consider-tier items are surfaced as an Optional Enhancements pick-list at the end of v2 for explicit user selection.
