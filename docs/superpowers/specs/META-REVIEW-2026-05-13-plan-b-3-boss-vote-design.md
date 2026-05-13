# Meta-Review — Plan B.3 Boss Vote Design

**Date**: 2026-05-13
**Source spec**: [`2026-05-13-plan-b-3-boss-vote-design.md`](2026-05-13-plan-b-3-boss-vote-design.md) (committed as `plan-b-3/0.1`)
**Companion context**: [`2026-05-13-plan-b-3-boss-vote-design-CONTEXT.md`](2026-05-13-plan-b-3-boss-vote-design-CONTEXT.md) (committed as `plan-b-3/0.2`)
**Reviewers**: 8
**Code validation pass**: Yes — the meta-reviewer (the spec author) re-read `CardRewardVotePatch.cs`, `AncientVotePatch.cs`, `VoteSession.cs`, `VoteTallyLabel.cs`, `MapCmd.cs`, and the relevant `ActModel.cs` decompile to validate or refute every load-bearing reviewer claim.

---

## A.1 — Review Summary Table

| Reviewer | Sentiment | Key Focus | Unique Insight |
|---|---|---|---|
| 1 | Mixed (constructive) | Re-entrancy TBD; popup-construction try/catch; `Prepare` enumeration; layout commitment; tie-break & 0-vote unspecified | Reframes Smoke G as save-load mid-vote check; calls out that `Surfinite` name leaked into doc |
| 2 | Strongly critical | Two-flag semantics; DoubleBoss duplicate-boss risk; off-thread UI; flag-acquire ordering; pool-size policy inconsistent; sampling primitive smell | Most thorough — calls out `_voteInProgress` acquire-after-guards-after-start ordering, exact prefix pseudocode |
| 3 | Critical | Infinite-loop in resume flow; DoubleBoss exclusion fallacy; controller/hotkey input leak; RNG determinism; `TextureRect` layout blowout | Calls out `string.IsNullOrEmpty(BossNodePath)` defensive check |
| 4 | Strongly critical (opinionated) | `NOverlayStack` "non-negotiable"; codify B.2.1 re-entry exactly; deterministic seed from runState; defensive logging; pool-size runtime warnings | Most aggressive on `NOverlayStack`; suggests achievement-preserving reflection swap |
| 5 | Mixed | Two-flag missing; root-`CanvasLayer` orphan across scenes; off-thread UI; click-through; `VoteTallyLabel` interaction undefined; tie reversal under DoubleBoss | Parent-popup-to-NTreasureRoom alternative; rename test file to match helper |
| 6 | Mostly positive | DoubleBoss exclusion claim wrong; popup freed from background thread; re-entrancy TBD; backdrop input block; no streamer dismissal; save-load determinism | Save-load determinism framed cleanly as v1 acceptable limitation |
| 7 | Mostly positive | TBD on re-entrancy; pool-size spike gatesheet; CanvasLayer z-index 100 collision; sampling seed strategy needs explicit choice | Spike gatesheet output format proposal |
| 8 | Mixed | `_resumeInProgress` missing from spec; `MouseFilter.Stop` + `ProcessMode.Always`; `BossNodePath + ".png"` fragility; BBCode in titles; `Prepare` surface unnamed; act number off-by-one | BBCode handling on titles; force-close acknowledgment; logging point enumeration |

**Overall**: Three reviewers (4 strongly, 2/3 critically) say the spec is NOT ready to implement. The other five (1, 5, 6, 7, 8) say it's solid but needs the high-severity items resolved. **No reviewer rubber-stamped it.**

---

## A.2 — Consensus Points (raised by 2+ reviewers)

Ranked by reviewer count, then severity.

### C1 — Re-entrancy TBD must close to the two-flag pattern (8/8 reviewers)

Every single reviewer flagged the spec's `TBD on whichever is safer (or re-suspends silently)` as a blocking issue. The fix is in the existing code.

**Code validation**: ✅ Confirmed. Both `CardRewardVotePatch.cs:24-25` and `AncientVotePatch.cs:23-24` declare BOTH flags:
```csharp
private static int _voteInProgress;
private static int _resumeInProgress;
```
Prefix begins with: `if (_resumeInProgress == 1) return true;` Resume sets `Interlocked.Exchange(ref _resumeInProgress, 1)` at top, clears both in `finally` (resume first, then vote).

**Verdict**: Must-do. Spec is wrong; fix is one-to-one copy from `CardRewardVotePatch.cs`.

### C2 — DoubleBoss duplicate-boss risk under A10+ (Reviewers 2, 3, 5, 6 explicitly; others implicitly via "spike SetBossEncounter side effects")

The spec claims "vanilla's exclusion logic still holds — the second boss is picked from `AllBossEncounters` excluding the primary; after our swap, the second boss may or may not be one of our 3 candidates, which is fine."

**Code validation**: ✅ Confirmed WRONG. From `decompiled/sts2/MegaCrit/sts2/Core/Models/ActModel.cs:287-291`:

```csharp
if (_rooms.SecondBoss is DeprecatedEncounter) {
    EncounterModel secondBoss = rng.NextItem(
        AllBossEncounters.Where((EncounterModel e) => e.Id != _rooms.Boss.Id));
    _rooms.SecondBoss = secondBoss;
}
```

This runs **once at run start** — when `SecondBoss is DeprecatedEncounter` (the unset sentinel). The `e.Id != _rooms.Boss.Id` exclusion uses the primary at that moment. After our `SetBossEncounter(newPrimary)`, the SecondBoss is NEVER revalidated. From `MapCmd.cs`:

```csharp
public static void SetBossEncounter(IRunState runState, EncounterModel boss) {
    runState.Act.SetBossEncounter(boss);   // mutates _rooms.Boss; does NOT touch _rooms.SecondBoss
    if (TestMode.IsOff) { /* UI refresh only */ }
}
```

So: if chat votes for a boss whose Id equals the pre-rolled SecondBoss Id, the streamer fights the same boss twice on A10+ Act 3.

**Fix surface**: `ActModel.SecondBossEncounter` and `HasSecondBoss` are public (`ActModel.cs:162, 164`). In B.3's candidate sampling, exclude `SecondBossEncounter.Id` from the pool when `HasSecondBoss` is true. Two-line change.

**Verdict**: Must-do. This is a real correctness bug, not a theoretical risk.

### C3 — Popup input routing must explicitly block click-through (Reviewers 1, 2, 3, 4, 5, 6, 8)

The spec describes a semi-transparent `ColorRect` backdrop but doesn't specify `MouseFilter`. Default `MouseFilter` on `Control` is `Stop`, but on `ColorRect` it's also `Stop` — so clicks ARE blocked. However, this is enough of a footgun that 7/8 reviewers wanted it explicit.

**Code validation**: `VoteTallyLabel.cs` doesn't set `MouseFilter` (it's a tally label, not a modal). The spec's popup is a new modal surface — explicit input handling matters.

**Verdict**: Must-do — call out `MouseFilter = MouseFilterEnum.Stop` on the backdrop in the spec. Cheap clarification.

### C4 — Off-thread Godot mutation from `session.Closed` / `TallyChanged` (Reviewers 2, 3, 5, 6, 8)

Reviewers warned that `session.TallyChanged` and `session.Closed` may fire on non-main threads (chat parser callbacks, timer callbacks), and subscribing UI code to them risks off-thread `QueueFree()` / label mutation.

**Code validation**: ✅ Confirmed for `TallyChanged`. From `VoteSession.cs:196`: `TallyChanged?.Invoke(this, this);` is called from `OnChatMessage` (line 196), which fires on the chat consumer's thread. **Existing pattern in `VoteTallyLabel` deliberately does NOT subscribe to `TallyChanged`** — it polls via `_Process` (`VoteTallyLabel.cs:48-96`).

⚠️ Partial: For `Closed`, `VoteTallyLabel.cs:35-36` DOES subscribe directly via `session.Closed += handler` where the handler calls `SafeQueueFree()` → `QueueFree()`. Godot's `QueueFree()` is documented as deferred-and-thread-safe-ish (it queues for next idle frame), so this works in practice. The existing code's `Cancelled` path uses the same shape.

**Verdict**: Must-do — adopt the proven pattern verbatim:
- Live tally/timer: poll via `_Process`, NOT subscribed to `TallyChanged`.
- Cleanup: subscribe to `Closed` and `Cancelled`; handlers call `SafeQueueFree()` (the same idempotent wrapper used in `VoteTallyLabel`).

### C5 — Process-local RNG → save-load yields different candidates (Reviewers 3, 4, 5, 6, 7, 8)

The spec uses a process-local `new Random()` (time-seeded). Save-load mid-chest → different candidates. Reviewers 3, 4, 5, 6, 7, 8 all flagged this; reviewers 4, 5, 6, 8 specifically suggested seeding from `runState.Rng.StringSeed + actIndex` (or via `HashCode.Combine`).

**Code validation**: `runState.Rng.StringSeed` exists and is used throughout the existing patches as the run-id (e.g., `CardRewardVotePatch.cs:223`). It's stable across save-load (it's the run's seed). Deriving a sampler seed from `(StringSeed, actIndex)` doesn't consume the run-deterministic generator.

**Verdict**: Should-do — switch to a derived seed:
```csharp
var seed = HashCode.Combine(runState.Rng.StringSeed, runState.CurrentActIndex);
var rng = new Random(seed);
```
One line, no downside, gives save-load determinism as a free property.

### C6 — Popup lifecycle and orphan-across-scene-transition (Reviewers 2, 4, 5)

The popup is parented to `SceneTree.Root` (not `NTreasureRoom`), so it won't auto-clean with the chest room. Reviewers worried about orphan-across-scene-transition.

**Code validation**: ✅ Pattern is established. `VoteTallyLabel.cs:40` attaches under `tree.Root` and cleans up on `session.Closed` / `session.Cancelled`. This works in production across all existing slices (Neow, Card, Ancients). The resume sequence is:
1. `coordinator.Dispatcher.Post(() => ResumeOnMainThread(...))`
2. Inside `ResumeOnMainThread`, the synthetic re-call (or `SelectCard` re-invocation) happens.
3. `session.Closed` fires from `CloseNowInternal` (`VoteSession.cs:237`) — which runs synchronously from within `AwaitWinnerAsync` resolution, NOT after the synthetic re-call.
4. So `session.Closed` → `popup.QueueFree()` is queued BEFORE the synthetic re-call. The popup is gone by the time the chest→map transition fires.

**Verdict**: Must-do — adopt the `VoteTallyLabel.SafeQueueFree()` cleanup pattern verbatim. Add Smoke D's "no orphan CanvasLayer" check explicitly to the operator-validation gate.

### C7 — Pool-size policy is inconsistent (Reviewers 2, 4, 5, 6, 7)

Spec variously says: goal "3 candidates", guard `Count >= 2`, sample `Take(3)`, edge case "pool of 2 runs a 2-option vote", popup "3 columns (or 2 if degenerate)". Reviewer 2 calls this "half-designed".

**Code validation**: Existing patches (`CardRewardVotePatch.cs:207`, `AncientVotePatch.cs:129`) handle the degenerate case as `options.Count <= 1 → skip vote, bail to vanilla`. The 2-option case IS the normal path; only 0/1 is degenerate.

**Verdict**: Should-do — tighten the spec's policy to one clear statement: "Sample up to 3. If pool size is ≥2, run a vote (3-option if pool ≥3, 2-option if pool == 2). If pool ≤ 1, bail to vanilla."

Reviewer 2's stronger suggestion ("require ≥3, bail if <3, drop 2-option support") is reasonable but more conservative than the existing pattern. The codebase already supports degenerate vote counts via `coordinator.Start`'s validation. Keep the 2-option path.

### C8 — Reflection / `Prepare` surface is unnamed (Reviewers 1, 8)

Spec says `Prepare` "verifies vanilla shape via reflection" but doesn't enumerate which fields/methods.

**Code validation**: ✅ Pattern is established. `CardRewardVotePatch.cs:103-156` enumerates the reflected surface: `_options`, `_cardRow`, `_selectCardMethod`, plus soft check on `RunManager.Instance.DebugOnlyGetState`. `AncientVotePatch.cs:31-71` does the same with `_event` field.

**Verdict**: Should-do — name the B.3 `Prepare` surface explicitly:
- `NTreasureRoom.OnProceedButtonPressed` method signature (parameterless).
- `NTreasureRoom._proceedButton` field (or equivalent — TBD during spike).
- `RunManager.Instance.DebugOnlyGetState()` accessor (soft check, same as other patches).
- `runState.Act.AllBossEncounters` and `runState.Act.SecondBossEncounter` access.

### C9 — `VoteTallyLabel` interaction not specified (Reviewers 2, 5)

Does B.3 attach the corner `VoteTallyLabel` as well as the popup, or only the popup?

**Code validation**: Both `CardRewardVotePatch.cs:256` and `AncientVotePatch.cs:159` call `VoteTallyLabel.AttachTo(session)` unconditionally. The label is always attached.

**Verdict**: Should-do — pick one. Two options:
- **A**: Match existing pattern — attach `VoteTallyLabel` for boss votes too. Consistency wins; double-tally is mild clutter but not broken.
- **B**: Suppress `VoteTallyLabel` for boss votes — popup already shows per-column tallies, double-surface is redundant.

I lean **A (attach)** for consistency. The popup is in the center; `VoteTallyLabel` is in the corner — they don't visually overlap. Operator validation will tell us if it's actually a problem.

### C10 — Achievement risk verification (Reviewers 1, 4, 6)

Spec accepts achievement risk as Smoke G; reviewers want either deeper coverage or move it out of non-goals.

**Code validation**: Out of scope — `ActModel.DefeatedAllEnemiesAchievement` is referenced in the feasibility doc but I haven't dug into achievement-tracking internals. Reviewer 4's "achievement-preserving swap via reflection" is speculative and adds significant complexity.

**Verdict**: Should-do — minor rewording per Reviewer 2's framing: move "no special achievement compensation logic" to non-goals; keep Smoke G as an acceptance verification step.

---

## A.3 — Outlier Points (raised by 1 reviewer; worth considering)

### O1 — `NOverlayStack` is non-negotiable (Reviewer 4)

Reviewer 4 strongly argues that `NOverlayStack` IS the right popup architecture, with `IOverlayScreen` providing focus/input/scene-transition handling for free. They call self-owned `CanvasLayer` "game-crashing potential."

**Code validation**: ❌ Disagree. Pushback:
- The existing codebase has ZERO uses of `NOverlayStack` for any mod-side UI. `VoteTallyLabel` is parented directly under `tree.Root` and has been production-validated across B.1, B.2.1, B.2.2, and yt-chat. No orphan-across-scene-transition issues observed.
- Reviewer 4's claim "vanilla *clears* the scene tree" during chest→map transition is unverified. The actual chest→map transition is a vanilla scene operation that doesn't touch nodes under `tree.Root` that aren't children of the chest scene.
- Implementing `IOverlayScreen` couples B.3 to a vanilla interface contract. If MegaCrit changes the contract, we break silently.
- The self-owned pattern is also what B.4 / B.5 / B.6+ will reuse. Switching away now creates inconsistency.

**Verdict**: Reject (with reason). Document the rationale in the spec: "Self-owned CanvasLayer is the established mod-UI pattern (`VoteTallyLabel`); reverting to `NOverlayStack` is rejected on coupling and consistency grounds."

### O2 — `ProcessMode = Always` to survive pause (Reviewer 8)

Reviewer 8 wants the `CanvasLayer.ProcessMode = ProcessModeEnum.Always` so the timer label keeps updating when the streamer pauses mid-vote.

**Verdict**: Should-do — set `ProcessMode = Always`. The chat-side vote timer continues during pause (real-time clock), so the visual should match. Verify in operator validation that this is actually what happens.

### O3 — Parent popup to `NTreasureRoom` instead of `SceneTree.Root` (Reviewer 5)

Reviewer 5 suggests parenting the popup under the chest room so it auto-cleans with the room.

**Verdict**: Reject (with reason). Doesn't match the established `VoteTallyLabel` pattern (`tree.Root` parent). And the popup needs to survive the resume's synthetic re-click — if it's parented under the chest room, the scene transition would tear it down WHILE the resume callback is running. Cleaner to own the cleanup explicitly.

### O4 — Streamer dismissal / skip mechanism for the modal (Reviewer 6)

Reviewer 6 notes that prior votes (Neow, card reward, Ancients) are non-modal; B.3's modal popup is the first time the streamer is fully blocked for 30s with no escape.

**Verdict**: Reject for v1 (with reason). Matches the chat-vs-streamer asymmetry principle — chat has agency once the vote starts. Streamer can abandon the run if truly stuck (Smoke D handles this). Document as a v1 design choice in the spec.

### O5 — Pre-vote 2-3 second countdown for chat lag (Reviewer 1)

Adds latency before the 30s vote window for lagged-Twitch viewers.

**Verdict**: Defer to polish. Not in scope.

### O6 — Logging point enumeration (Reviewer 8)

Spec says check for `[boss-vote]` log lines in the acceptance gate but doesn't enumerate WHAT gets logged.

**Verdict**: Should-do — match the existing pattern: `[boss-vote] target resolved`, `[boss-vote] opening vote for N options`, `[boss-vote] resume: applying winner #N on main thread`, `[boss-vote] resume aborted: <reason>`, `[boss-vote] cancellation receipt queued` (if cancellation is wired).

### O7 — Tween fade-in races `QueueFree` (Reviewers 7, 8)

Spec describes "Optional Tween modulate fade-in over ~150ms" as polish. Reviewers want either commit-to-it or drop-it.

**Verdict**: Should-do — drop the Tween from v1. Instant popup is fine. Defer animation to polish.

### O8 — `BossNodePath + ".png"` concatenation fragility (Reviewer 8)

If `BossNodePath` already ends in `.png`, naive concat breaks. Worth flagging as spike item.

**Verdict**: Should-do — add to pre-implementation spike items.

### O9 — `EncounterModel.Title.GetFormattedText()` BBCode handling (Reviewer 8)

If `LocString` can contain BBCode markup and `Label.BBCodeEnabled = false`, viewers see literal markup.

**Code validation**: `VoteTallyLabel.cs:27` sets `BbcodeEnabled = true` on its `RichTextLabel`. Established pattern. B.3 popup's title labels should also be `RichTextLabel` with `BbcodeEnabled = true` (or `Label` with explicit `BbcodeEnabled` if it exists on `Label` — typically `RichTextLabel` is correct).

**Verdict**: Should-do — use `RichTextLabel` (matching `VoteTallyLabel`) with `BbcodeEnabled = true` for boss title rendering.

### O10 — Z-index 100 collision risk (Reviewer 7)

Spec hardcodes `CanvasLayer.Layer = 100` with no justification.

**Verdict**: Should-do — make it a named `const` (e.g., `BossVotePopup.LAYER_INDEX = 100`) and add the rationale to a spike item: verify vanilla doesn't use layer 100.

### O11 — Receipt examples use Ancient names not boss names (Reviewer 2)

Spec's receipt examples say `#0 Pael, #1 Tezcatara` — those are Ancients, not bosses.

**Verdict**: Should-do — replace with placeholder boss names or generic `Boss0`/`Boss1`/`Boss2` in the receipt-string examples. Trivial.

### O12 — `Surfinite` name leaks into doc as if it's domain term (Reviewers 1, 2)

Some readers won't recognize "Surfinite" as the author.

**Verdict**: Should-do — replace "Surfinite has explicitly noted" with "Project decision:" or "The author has chosen". Trivial.

### O13 — Spike gatesheet output format (Reviewer 7)

Reviewer 7 proposes the spike output a structured gatesheet so implementation can't proceed on optimistic assumptions.

**Verdict**: Consider — adds discipline but the existing pattern uses prose in `notes/06`. Lean yes if you want strict gating, neutral otherwise.

### O14 — Achievement-preserving swap via reflection (Reviewer 4)

Speculative. Adds complexity.

**Verdict**: Reject (with reason). Not engineering around — see C10.

### O15 — `SuspendAndResumePatchBase` extraction now (Reviewer 4 implicit)

With n=3 patches, Rule of Three has fired.

**Code validation**: Looking at the three actual patches:
- `AncientVotePatch.ResumeOnMainThread`: re-calls `room.OptionButtonClicked(winnerOption, applyIndex)`.
- `CardRewardVotePatch.ResumeOnMainThread`: uses reflection to call private `SelectCard` via `method.Invoke(screen, new object[] { winnerHolder })`.
- B.3's `ResumeOnMainThread` would: call `MapCmd.SetBossEncounter(runState, winnerBoss)` THEN re-call `NTreasureRoom.OnProceedButtonPressed()`.

These are structurally different in the resume body. A shared base would need a `ResumeAction` hook — feasible but not trivial.

**Verdict**: Defer (consistent with current spec). Add a one-line justification: "B.3's resume differs structurally; extraction deferred to a post-B.3 refactor slice." Reviewer 2's note "B.3's resume is structurally asymmetric — that asymmetry is *exactly* the kind of detail Rule of Three is supposed to surface before extraction" is correct.

### O16 — Cancellation receipt for boss vote (implicit; Reviewer 2 mentions cancellation generally)

`CardRewardVotePatch.cs:397-408` has `SendCancellationReceipt()` for when the resume drops. `AncientVotePatch` does NOT have one.

**Code validation**: The cancellation receipt is B.2.1 territory. The spec says "Cancellation (run abandoned mid-vote): reuses the existing cancellation receipt path verbatim from B.2.1; no new string needed." Slightly misleading — the cancellation receipt is patch-specific in B.2.1 (`SendCancellationReceipt` is a private helper). B.3 needs its own version (~6 lines) if it wants the same UX.

**Verdict**: Should-do — clarify in the spec that B.3 will have its own `SendCancellationReceipt` private helper modeled on `CardRewardVotePatch.cs:397-408`. Receipt text: `"Vote result ignored — run abandoned during boss vote"` or similar.

---

## A.4 — Category Breakdown

### 🏗️ Architecture & Design

- **C1 Re-entrancy two-flag**: Must-do (8 reviewers; code-validated against B.2.1/B.2.2).
- **C2 DoubleBoss duplicate**: Must-do (4-6 reviewers; code-validated against `ActModel.cs:287-291`).
- **C6 Popup lifecycle**: Must-do (3 reviewers; pattern validated against `VoteTallyLabel`).
- **C9 `VoteTallyLabel` attach**: Should-do, lean attach (2 reviewers; existing pattern always attaches).
- **O1 NOverlayStack non-negotiable**: Reject (1 reviewer; goes against established codebase pattern).
- **O3 Parent popup to NTreasureRoom**: Reject (1 reviewer; cleanup timing concerns).
- **O15 Extract base class**: Defer (consistent with spec; reviewer agreement on Rule of Three deferral).

### ⚠️ Risks & Concerns

- **C3 Input routing (MouseFilter)**: Must-do (7 reviewers).
- **C4 Off-thread UI mutation**: Must-do (5 reviewers; code-validated — poll via `_Process`, not `TallyChanged` subscription).
- **C5 RNG determinism**: Should-do (6 reviewers; switch to seeded `Random`).
- **C7 Pool-size policy**: Should-do (5 reviewers; tighten to single clear policy).
- **C10 Achievement risk**: Should-do (3 reviewers; minor rewording).
- **O2 ProcessMode = Always**: Should-do (1 reviewer).
- **O10 Z-index 100 rationale**: Should-do (1 reviewer; make it a named const).

### 🗑️ Suggested Removals / Simplifications

- **O7 Tween fade-in**: Should-do — remove (2 reviewers; instant popup is fine).
- **2-option degenerate popup support** (Reviewer 2 suggests removing): Reject — established codebase pattern supports it.
- **`_outline.png` overlay** (Reviewer 8): Should-do — remove the optional second TextureRect mention; one TextureRect is sufficient.
- **Smoke G achievement gate** (Reviewer 5 suggests downgrading): Reject — keep as verification, just reframe per C10.

### ➕ Suggested Additions / Features

- **O6 Enumerate `[boss-vote]` log points**: Should-do.
- **O16 Cancellation receipt** (`SendCancellationReceipt` helper modeled on `CardRewardVotePatch`): Should-do.
- **C8 `Prepare` surface enumeration**: Should-do.
- **Pre-implementation spike additions**:
  - O8 `BossNodePath` extension handling: Should-do.
  - O9 BBCode handling on titles: Should-do.
  - Reviewer 6's `LocString.GetFormattedText()` confirmation: Should-do.
  - Reviewer 8's `CurrentActIndex` semantics check: Should-do.
- **Stale-popup check added to Smoke D** (Reviewers 2, 5): Should-do.
- **Force-close edge case acknowledgment** (Reviewer 8): Consider.
- **O13 Spike gatesheet output format**: Consider.

### 🔄 Alternative Approaches

- **Alt: Use delegate `Action<IRunState, EncounterModel>` instead of `IBossSwapper` interface** (Reviewers 3, 5, 8): Should-do.
  - Code validation: `CardRewardVotePatch.cs:52-53` uses `Lazy<MethodInfo?>` for the reflected `SelectCard` method — there is no abstract "mutator" seam in B.2.1. The test seam for `MapCmd.SetBossEncounter` in B.3 is genuinely new. Delegate is lighter than interface.

### ✅ Confirmed Good / Keep As-Is

- Reuse of B.1/B.2.1 suspend-and-resume (all reviewers).
- `MapCmd.SetBossEncounter` as single mutation point (all reviewers).
- Self-owned `CanvasLayer` v1 choice (7/8 reviewers, with fixes).
- Multiplayer bail (all reviewers).
- Test factoring discipline `[Collection("TiLog.Sink")]` (all reviewers).
- Pure-logic helper carve-out (all reviewers).
- Scope discipline / explicit non-goals (all reviewers).
- 7 operator smokes (all reviewers).
- Defer suspend-and-resume base extraction (Reviewers 1, 6, 7, 8 explicit; others implicit).

### 🔧 Implementation Details & Nits

- **O11 Receipt example boss names** (Reviewer 2): Should-do — replace Ancient names.
- **O12 "Surfinite" name leak** (Reviewers 1, 2): Should-do.
- **Code-size estimate too low** (Reviewers 2, 6 — estimate 350-500 LOC vs spec's 300): Update estimate.
- **`AllBossEncounters.Count()` enumeration** (Reviewer 2): Materialize once to `List<EncounterModel>` to avoid double enumeration.
- **Timer label update cadence** (Reviewer 8): Trivial — `_Process` polling is fine; throttling is over-engineering.
- **`CurrentActIndex` 0-based vs 1-based** (Reviewer 8): Spike item.

### 📦 Dependencies & Integration

- N/A — no reviewer raised dependency concerns specific to B.3.

### 🔮 Future Considerations

- **DoubleBoss pair-vote / chained vote** (deferred in non-goals): Reviewer 1 suggests it as polish; consistent with spec.
- **Spine portraits** (deferred in non-goals): consistent with spec.
- **Silhouette / spoiler mode** (deferred in non-goals): consistent with spec.
- **Boss-vote-specific settings toggle**: deferred to cross-cutting slice; consistent with spec.

---

## A.5 — Conflicts & Contradictions

### Conflict 1: `NOverlayStack` vs self-owned `CanvasLayer`

- **Reviewer 4**: `NOverlayStack` is non-negotiable; self-owned is "game-crashing potential."
- **Reviewers 2, 5, 6, 7, 8**: Self-owned is correct for v1 with fixes (input routing, lifecycle, layer index).

**Resolution**: Self-owned wins. The codebase already established this pattern with `VoteTallyLabel` (parented under `tree.Root`, self-cleaning on `session.Closed`/`Cancelled`). Reviewer 4's claim that scene-tree clearing orphans the popup is unverified and contradicted by `VoteTallyLabel`'s production track record across 4 prior slices. Reject Reviewer 4's recommendation; adopt input-routing + lifecycle fixes from the other reviewers.

### Conflict 2: 2-option vote support (keep vs remove)

- **Reviewer 2**: Remove 2-option support; require pool ≥ 3 or bail.
- **Reviewer 6, others**: 2-option degenerate handling is good defensive design.

**Resolution**: Keep 2-option support. The existing codebase patches (`CardRewardVotePatch.cs:207`, `AncientVotePatch.cs:129`) handle `options.Count <= 1` specifically as the degenerate case. 2-option is the normal "small pool" path. Tighten the spec's policy statement so it's unambiguous (per C7).

### Conflict 3: Achievement risk handling (downgrade Smoke G vs keep)

- **Reviewer 5**: Downgrade Smoke G — only verify "no crash, no soft-lock."
- **Reviewer 1, 4, 6**: Keep Smoke G as is; reframe achievement-engineering as non-goal.

**Resolution**: Keep Smoke G; reword the non-goal entry per Reviewer 2's framing ("no special achievement compensation logic" rather than "achievement-gate verification"). Reviewer 5's downgrade is too narrow — operator validation should at least observe whether the achievement fires.

### Conflict 4: Sampling seed approach

- **Reviewer 4**: `(runState.Rng.Seed << 8) | (runState.CurrentActIndex + 1)` then `.GetHashCode()`.
- **Reviewers 5, 6, 8**: `HashCode.Combine(runState.Rng.StringSeed, runState.CurrentActIndex)`.
- **Reviewer 3**: Hash of `runState.Rng.StringSeed + runState.CurrentActIndex`.
- **Spec**: process-local `new Random()`.

**Resolution**: All deterministic-seed proposals are equivalent in effect. Pick the cleanest form: `new Random(HashCode.Combine(runState.Rng.StringSeed, runState.CurrentActIndex))`. Don't over-think it.

---

## A.6 — Recommended Plan Changes (prioritized)

### Must-do (apply automatically)

1. **C1 — Resolve re-entrancy TBD** by adopting the two-flag pattern verbatim from `CardRewardVotePatch.cs`. Update the spec's "Trigger surface" and "Vote flow" sections to show the actual prefix order: `_resumeInProgress` check first → guards → atomic `_voteInProgress` acquire → side effects → release-on-bail. (8 reviewers)

2. **C2 — Exclude `SecondBossEncounter` from candidate pool when `HasSecondBoss` is true.** Two-line fix in candidate sampling. Add to spec architecture and to Smoke C operator validation. (4-6 reviewers; code-validated against `ActModel.cs:287-291`)

3. **C3 — Specify `MouseFilter = MouseFilterEnum.Stop` on the backdrop `ColorRect`** in the Popup architecture section. (7 reviewers)

4. **C4 — Adopt the `VoteTallyLabel` pattern**: `BossVotePopup` polls via `_Process` for tally + timer (NOT subscribed to `TallyChanged`); subscribes to `session.Closed` and `session.Cancelled` with handlers that call an idempotent `SafeQueueFree()`. (5 reviewers; code-validated against `VoteTallyLabel.cs`)

5. **C6 — Explicit popup lifecycle**: parent to `tree.Root`, cleanup via `SafeQueueFree`, add "no orphan CanvasLayer" check to Smoke D. (3 reviewers; pattern validated)

6. **Vote-flow diagram correction (Reviewer 2)**: the spec's current diagram shows `coordinator.Start` before `_voteInProgress = true`. Fix to match actual code order: cheap guards → atomic flag acquire → snapshot → coordinator.Start (in try/catch with flag-release on throw) → fire async handler → return false. (1 reviewer, but code-validated as wrong in spec)

### Should-do (apply automatically)

7. **C5 — Switch sampling to seeded `Random`**: `new Random(HashCode.Combine(runState.Rng.StringSeed, runState.CurrentActIndex))`. (6 reviewers)

8. **C7 — Tighten pool-size policy** to one clear statement. Keep 2-option support; bail only when pool ≤ 1. (5 reviewers)

9. **C8 — Enumerate `Prepare` surface**: `NTreasureRoom.OnProceedButtonPressed` method signature; one or two reflected fields (TBD during spike); soft-check on `RunManager.Instance.DebugOnlyGetState`. (2 reviewers)

10. **C9 — `VoteTallyLabel` decision**: explicitly state that B.3 attaches `VoteTallyLabel.AttachTo(session)` for consistency with existing slices. The corner label and the popup serve different surfaces (corner = small persistent tally, popup = modal-context tally). (2 reviewers)

11. **C10 — Achievement framing**: rephrase non-goal to "no special achievement compensation logic." Keep Smoke G. (3 reviewers)

12. **O2 — Set `ProcessMode = ProcessModeEnum.Always` on the popup CanvasLayer** so timer keeps updating during pause. (1 reviewer)

13. **O6 — Enumerate `[boss-vote]` log points** in the acceptance gate. (1 reviewer)

14. **O7 — Drop Tween fade-in.** Instant popup for v1. (2 reviewers)

15. **O8 — Add `BossNodePath` extension handling to spike items** (verify whether append `.png` is correct or path-conditional). (1 reviewer)

16. **O9 — Use `RichTextLabel` with `BbcodeEnabled = true`** for boss title rendering, matching `VoteTallyLabel`. (1 reviewer)

17. **O10 — Make z-index a named const** (`BossVotePopup.LAYER_INDEX = 100`); add spike item for layer-collision check. (1 reviewer)

18. **O11 — Replace Ancient names with boss names** in receipt examples. (1 reviewer)

19. **O12 — Replace "Surfinite has explicitly noted"** with neutral framing. (2 reviewers)

20. **O16 — Spec the cancellation receipt path explicitly**: B.3 has its own `SendCancellationReceipt` helper modeled on `CardRewardVotePatch.cs:397-408`. (1 reviewer; code-validated as missing)

21. **Update code-size estimate** to 350-500 LOC (Reviewers 2, 6).

22. **Materialize `AllBossEncounters` to `List` once** to avoid double-enumeration in guard + sampling. (1 reviewer)

23. **Alt seam: use `static Action<IRunState, EncounterModel>` delegate** instead of `IBossSwapper` interface. (3 reviewers)

24. **Justify deferred extraction**: add one-line note that B.3's resume differs structurally from B.1/B.2.2/B.2.1 (call API THEN re-click vs. just re-call), making shared base non-trivial. (1 reviewer)

### Consider (presented as pick list — see Part B)

- O5 Pre-vote countdown.
- O13 Spike gatesheet output format.
- Reviewer 1's Smoke H "save & reload mid-vote".
- Reviewer 8's force-close acknowledgment.
- Reviewer 7's CanvasLayer `tree.Root` collision investigation (deeper than just naming it as const).
- Reviewer 3's `string.IsNullOrEmpty(BossNodePath)` defensive check.
- Reviewer 5's "rename test file to match helper class" — actually adopt as part of C8 helper-naming.
- Reviewer 4's runtime pool-size warnings (logging only).
- Reviewer 4's "log sampled boss IDs at `[boss-vote]` level".

### Reject (with reason)

- **O1 NOverlayStack non-negotiable**: Codebase pattern is self-owned `tree.Root`; `VoteTallyLabel` validates this across 4 slices. Reviewer 4's "scene tree clearing" claim is unverified and contradicted by production track record.
- **O3 Parent popup to NTreasureRoom**: Conflicts with `VoteTallyLabel` pattern; cleanup timing across scene transitions is harder.
- **O4 Streamer dismissal mechanism**: Conflicts with chat-vs-streamer asymmetry principle.
- **O14 Achievement-preserving swap via reflection**: Speculative; out of scope.
- **Reviewer 4's specific code snippets** that show wrong flag order (`Interlocked.CompareExchange(ref _resumeInProgress, 0, 1)` reads the flag with a `cmpxchg`, not a plain read — the correct pattern is `if (_resumeInProgress == 1) return true;` per actual code).

---

## A.7 — What Stays

Explicit confirmation list — these aspects of the spec are validated by both reviewer consensus and code inspection. Do NOT modify in v2:

- **Trigger surface**: `NTreasureRoom.OnProceedButtonPressed` is the right Harmony target.
- **Vote-swap API**: `MapCmd.SetBossEncounter` is the single mutation point.
- **Suspend-and-resume pattern reuse**: copy-paste-modify of `CardRewardVotePatch.cs` shape.
- **Popup architecture choice (self-owned vs vanilla)**: self-owned wins.
- **Vote duration**: 30s, matching all other votes.
- **Scope discipline**: 8 non-goals stand.
- **DoubleBoss approach (primary-only vote)**: stays; the second-boss exclusion is just a sampling adjustment, not a different vote-architecture choice.
- **All 3 acts get a vote**: stays.
- **3-fresh sample, no current-boss preservation**: stays.
- **No `voteOnBoss` settings toggle in v1**: stays (consistent with unwired-toggle baseline).
- **Process-local RNG to avoid polluting `runState.Rng`**: stays; just seed it deterministically.
- **Multiplayer bail**: stays.
- **Test-seam factoring** (pure helpers compiled into tests, patch excluded): stays.
- **Operator-validation smokes A-G**: all stay; Smoke C gains the duplicate-boss assertion; Smoke D gains the orphan-CanvasLayer check.
- **Deferred extraction of suspend-and-resume base class**: stays.
- **Cross-references to `notes/10`, `notes/06`, `notes/09`**: stay.

---

## Part B follows in the v2 spec file.
