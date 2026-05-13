# 2026-05-13 — Plan B.3: Boss Vote (design v3)

**Date**: 2026-05-13
**Status**: Design v3 — post-round-2-meta-review. Implementation pending.
**Slice**: B.3 (chat-picks-the-next-act-boss vote) — fifth voting slice after B.1 (Neow), B.2.1 (card reward), v0.2 yt-chat, and B.2.2 (Ancients, implementation-complete on `main` at time of writing — operator-validation gate pending, not yet tagged complete; B.3 does not depend on B.2.2's tag).
**Scope**: Add a chest-room-exit vote where chat picks the next act's boss from up to 3 randomly sampled candidates. Triggered on `NTreasureRoom.OnProceedButtonPressed` via suspend-and-resume; resolved via `MapCmd.SetBossEncounter`. New Godot `CanvasLayer`-rooted popup with portrait columns and per-column tally labels.

## TL;DR

Feasibility was settled in [`notes/10-boss-vote-feasibility.md`](../../../notes/10-boss-vote-feasibility.md). Vanilla exposes `MapCmd.SetBossEncounter(IRunState, EncounterModel)` — a public static one-liner that swaps the act's boss, refreshes the top-bar boss icon, and re-renders the map preserving streamer drawings. We hook `NTreasureRoom.OnProceedButtonPressed` with a Harmony prefix using the two-flag suspend-and-resume pattern (`_voteInProgress` + `_resumeInProgress`) verbatim from `CardRewardVotePatch.cs`: prefix returns `false`, kicks off a 30s vote async, resumes the Proceed click on the main thread after `MapCmd.SetBossEncounter` is applied. The popup is a self-owned `CanvasLayer` + opaque-input `ColorRect` backdrop + N-column Control rendering `BossVotePopupOption` DTOs (kept MegaCrit-free), with no interaction with vanilla's overlay-stack. Vote fires on every act (1, 2, 3). On A10+ Ascension's `DoubleBoss` runs (Act 3 only), the chat-picked primary boss is excluded from colliding with the pre-rolled second boss by removing `SecondBossEncounter.Id` from the candidate pool before sampling. Sampling RNG seeded with a stable FNV-1a hash of `(StringSeed, ActIndex)` for save-load determinism. Vote-failure fallback is "skip boss swap, synthetic Proceed only" — there is no streamer-encoded choice to fall back to on Proceed. Net change: ~1 new patch class, ~1 new popup Control, ~3 new pure helpers (`BossCandidateSampler`, `BossVoteSeed`, `BossVoteResolver`), ~1 cancellation-receipt helper, modest receipt additions, no new settings knobs.

<!-- CHANGED v3: TL;DR adds stable-hash seeding, DTO-based popup, "skip boss swap on no-winner" fallback shape, three pure helpers — R2A + R2B round-2 consensus on playerClickIndex, R2B on HashCode/DTOs/test-seam -->

## Goals

- Chat votes on the upcoming act boss every time the streamer exits a chest room. Up to 3 random candidates from `runState.Act.AllBossEncounters` (excluding the pre-rolled SecondBossEncounter on A10+ Act 3), 30s vote window, latest-wins semantics, 0-indexed labels (`#0 / #1 / #2`).
- Visual popup overlays the chest scene (matches the original Tempus mod's screenshot reference) with portrait columns showing each candidate's boss icon, title, and live tally bar. Backdrop blocks mouse, keyboard, and gamepad-confirm input.
- Winner is applied via `MapCmd.SetBossEncounter` before the streamer transitions to the map screen, so the new boss is in place when the boss room is later entered.
- Reuse B.1 / B.2.1 / B.2.2 infrastructure verbatim where applicable: `VoteCoordinator`, `VoteSession`, two-flag suspend-and-resume Harmony pattern, `EnglishReceipts`, periodic-tally cadence, multi-platform aggregation, run-id guards.
- Singleplayer-only v1 — bail to vanilla in multiplayer (matches existing slices).

## Non-goals

- **Per-vote settings toggle (`voteOnBoss: bool`)** — not wired in v1. Matches the current state of Neow and Card Reward votes (also no toggle yet per [`notes/09`](../../../notes/09-settings-and-tunable-knobs.md)). Will be addressed in a future cross-cutting "B.2 surface toggles" slice rather than per-slice.
- **Per-decision vote-duration override** — 30s hardcoded to match Neow / Card Reward. Project decision: durations will eventually become globally-configurable (one knob, not per-vote); deferred to a future settings slice.
- **Silhouette / spoiler-safe mode** (`showBossNames: false`) — target audience is unlocked-everything streamers per [`notes/10`](../../../notes/10-boss-vote-feasibility.md); deferred to future polish.
- **A10+ DoubleBoss pair-vote / chained second-boss vote** — vote on primary only; vanilla's pre-rolled second boss is preserved (and excluded from the sample to avoid duplicate-boss collision).
- **Multiplayer sync** — `MapCmd.SetBossEncounter` mutates `runState.Act` locally and has no network propagation in vanilla. We bail to vanilla in multiplayer.
- **Spine-animated boss portraits** — PNG path only (no `_outline.png` overlay in v1). Spine animation is polish.
- **"Keep current boss" explicit option** — sample is fresh from the full pool; the current boss may appear by chance.
- **No special achievement compensation logic** — Smoke G verifies first-defeat tracking empirically.
- **Full gamepad/controller navigation capture inside the modal** — v1 swallows mouse + `ui_accept` + `ui_cancel` to prevent accidental Proceed under the popup, but does not implement full controller-focus management. Keyboard+mouse streamers are the target audience; full gamepad-focus is v0.2 polish. <!-- CHANGED v3: clarified what input IS swallowed vs what isn't — R2B O5 -->

## Architecture

### Trigger surface

Harmony **prefix** on `NTreasureRoom.OnProceedButtonPressed` (Option B in the feasibility doc). Prefix returns `false` to suspend the original click, then resumes it on the main thread after the vote closes via `coordinator.Dispatcher.Post`.

**Two-flag re-entry guard** (copied verbatim from `CardRewardVotePatch.cs` / `AncientVotePatch.cs`):

```csharp
private static int _voteInProgress;
private static int _resumeInProgress;
```

**Prefix ordering** (matches `CardRewardVotePatch.Prefix` shape):

1. `if (_resumeInProgress == 1) return true;` — this is the synthetic resume re-call; let vanilla through unimpeded.
2. `if (!GodotObject.IsInstanceValid(__instance)) return true;` — defensive node-validity check.
3. Multiplayer bail.
4. Chat-readable bail: `if (Voter.Default?.Chat.State is not (ConnectedReadOnly or ConnectedReadWrite)) return true;`.
5. `if (Interlocked.CompareExchange(ref _voteInProgress, 1, 0) != 0) return false;` — atomic acquire; rejects streamer double-click silently.
6. Snapshot the candidate pool (see Candidate sampling below). If pool size ≤ 1, **release `_voteInProgress` and return `true`** (single-option degenerate path).
7. Capture run-id soft guard.
8. Map `EncounterModel` candidates → `BossVotePopupOption` DTOs before constructing the popup. <!-- CHANGED v3: DTO mapping happens before popup construction — R2B O3 -->
9. `coordinator.Start(...)` inside try/catch. On throw: release `_voteInProgress`, return `true`.
10. Instantiate `BossVotePopup(dtos, session, coordinator.Dispatcher)` inside try/catch. On throw: cancel the session via `session.Cancel()`, release `_voteInProgress`, return `true`.
11. `_ = HandleVoteAsync(coordinator, __instance, session, sample, runIdAtStart)` — fire-and-forget.
12. `return false` — suspend original click.

**Resume ordering** (`ResumeOnMainThread` on the main thread via `coordinator.Dispatcher.Post`, takes `int? winnerIndex`):

```csharp
Interlocked.Exchange(ref _resumeInProgress, 1);
try {
    // validity + liveness checks (room valid, RunManager not abandoned, IsGameOver false,
    //   run-id drift check, SecondBossEncounter still present if A10+)
    // if any check fails: SendIgnoredResultReceipt(); return without ApplyBossSwap.
    if (winnerIndex.HasValue) {
        var winnerEncounter = BossVoteResolver.ResolveWinner(sample, winnerIndex.Value);
        ApplyBossSwap(runState, winnerEncounter);
    }
    // No winner (AwaitWinnerAsync failed): skip ApplyBossSwap (preserve vanilla pre-rolled boss).
    // Either way, fire the synthetic Proceed re-click so the streamer progresses.
    treasureRoom.OnProceedButtonPressed();
} catch (Exception ex) {
    TiLog.Error("[SlayTheStreamer2][boss-vote] resume threw", ex);
} finally {
    Interlocked.Exchange(ref _resumeInProgress, 0);
    Interlocked.Exchange(ref _voteInProgress, 0);
}
```

<!-- CHANGED v3: ResumeOnMainThread takes int? winnerIndex; no-winner path skips ApplyBossSwap but still re-clicks Proceed; uses BossVoteResolver helper — R2A + R2B 2/2 on playerClickIndex copy-paste artifact -->

### Vote flow (corrected for v3)

```
Streamer clicks Proceed in chest room
    │
    ▼
BossVotePatch.Prefix                              ─ main thread
    │  ① if (_resumeInProgress == 1) return true            (synthetic re-call passes through)
    │  ② IsInstanceValid / MP / chat-readable cheap guards
    │  ③ Interlocked.CompareExchange(ref _voteInProgress, 1, 0)  (atomic acquire)
    │     ← rejects double-click with return false
    │  ④ snapshot pool, exclude SecondBossEncounter.Id if A10+, sample up to 3
    │     ← degenerate (≤1 candidate) releases flag and bails
    │  ⑤ map EncounterModel candidates → BossVotePopupOption DTOs
    │  ⑥ coordinator.Start("Act N boss vote", ...)         (try/catch → release+bail on throw)
    │  ⑦ new BossVotePopup(dtos, session, dispatcher)      (try/catch → cancel+release+bail on throw)
    │  ⑧ _ = HandleVoteAsync(coordinator, __instance, session, sample, runIdAtStart)
    │  ⑨ return false                                      (suspend original click)
    │
    ▼
HandleVoteAsync                                   ─ background
    │  coordinator.Dispatcher.Post(() => VoteTallyLabel.AttachTo(session))   ← consistency with B.1/B.2.1
    │  int? winnerIndex;
    │  try:
    │    int idx = await session.AwaitWinnerAsync();
    │    if (idx < 0 || idx >= sample.Count) winnerIndex = null;  (out-of-range → no swap)
    │    else winnerIndex = idx;
    │  catch (Exception ex):
    │    TiLog.Error; winnerIndex = null;                  (no-winner fallback; preserve vanilla boss)
    │  coordinator.Dispatcher.Post(() => ResumeOnMainThread(winnerIndex, ...))
    │  outer try/catch posts ResumeOnMainThread(winnerIndex: null) on async failure
    │
    ▼
ResumeOnMainThread(int? winnerIndex, ...)         ─ main thread
    │  Interlocked.Exchange(ref _resumeInProgress, 1)
    │  try:
    │    liveness + validity + run-id-drift checks
    │      → on any fail: SendIgnoredResultReceipt(); return (no ApplyBossSwap, no synthetic Proceed)
    │    if (winnerIndex.HasValue) ApplyBossSwap(runState, BossVoteResolver.ResolveWinner(sample, winnerIndex.Value))
    │    else: log "[boss-vote] resume: no winner; preserving vanilla boss"
    │    treasureRoom.OnProceedButtonPressed()             (synthetic re-call; prefix passes through via _resumeInProgress)
    │  catch: TiLog.Error
    │  finally:
    │    Interlocked.Exchange(ref _resumeInProgress, 0)
    │    Interlocked.Exchange(ref _voteInProgress, 0)      (vote-flag cleared LAST)
    │
    ▼
Vanilla chest → map → boss room with swapped boss (or original boss if no winner)
    │  BossVotePopup freed via session.Closed / session.Cancelled subscription
    │  (handlers marshal SafeQueueFree through dispatcher.Post for main-thread guarantee)
```

<!-- CHANGED v3: HandleVoteAsync now produces int? winnerIndex; out-of-range and exception paths both produce null; ResumeOnMainThread skips ApplyBossSwap on null but still re-clicks Proceed — R2A + R2B 2/2 -->

### Candidate sampling

```csharp
List<EncounterModel> pool = runState.Act.AllBossEncounters.ToList();     // materialize once
if (runState.Act.HasSecondBoss) {
    string? secondId = runState.Act.SecondBossEncounter?.Id;
    if (!string.IsNullOrEmpty(secondId)) {
        pool.RemoveAll(e => e.Id == secondId);
    } else {
        TiLog.Warn("[SlayTheStreamer2][boss-vote] HasSecondBoss true but SecondBossEncounter missing");
    }
}
if (pool.Count < 3) {
    TiLog.Warn($"[SlayTheStreamer2][boss-vote] only {pool.Count} bosses available for Act {runState.CurrentActIndex + 1} — possible content change?");
}
if (pool.Count <= 1) {
    // degenerate — release _voteInProgress and bail to vanilla
    return /* degenerate path */;
}

// Stable deterministic seed: same run + same act → same int across process launches.
// HashCode.Combine is NOT used because string.GetHashCode() is per-process randomized
// in .NET 5+ — it would break save-load determinism (Smoke H).
int seed = BossVoteSeed.Stable(runState.Rng.StringSeed, runState.CurrentActIndex);
var rng = new Random(seed);
var sample = BossCandidateSampler.SampleDistinct(pool, count: 3, rng);    // pure helper; partial Fisher-Yates
```

<!-- CHANGED v3: SecondBossEncounter null-defensive; stable FNV-1a hash instead of HashCode.Combine — R2B O1 (code-validated) + R2B O9 -->

`BossVoteSeed.Stable` is a new pure helper in `src/Game/DecisionVotes/`. Shape:

```csharp
internal static class BossVoteSeed {
    public static int Stable(string runSeed, int actIndex) {
        unchecked {
            const int fnvOffset = unchecked((int)2166136261);
            const int fnvPrime = 16777619;
            int hash = fnvOffset;
            foreach (char c in runSeed ?? string.Empty) {
                hash ^= c;
                hash *= fnvPrime;
            }
            hash ^= actIndex;
            hash *= fnvPrime;
            return hash;
        }
    }
}
```

FNV-1a 32-bit. Stable across process launches (does not depend on `string.GetHashCode()`). Null-safe. Unit-tested for: same input → same output across repeated calls + different acts produce different outputs.

Policy summary:
- **Pool ≥ 3**: sample 3 → 3-option vote.
- **Pool == 2**: sample 2 → 2-option vote.
- **Pool ≤ 1**: release flag, bail to vanilla.
- **Pool < 3 (but ≥ 2)**: log warning (future-proofing against MegaCrit content updates).
- **A10+ Act 3**: `SecondBossEncounter` excluded from the pool (defensively null-guarded).
- **Tie-break / 0-vote**: inherited from `VoteSession.cs:267-277`. Tie among N options → `_random.Next(N)` pick from tied options. Zero votes received → `_random.Next(Options.Count)` pick from all candidates (`noVotes=true` flag set in snapshot). **B.3 does not override this behavior.** Chat absence is rare; in that case a random candidate fires, which is still preferable to overriding the vote with vanilla's pre-rolled pick (the random pick at least respects that the streamer opted-in to letting chat decide). <!-- CHANGED v3: explicit tie-break + 0-vote documentation — R2A O6 -->

### Boss-swap API, test seam, and helper factoring

**Three pure helpers** carry the testable logic (all compiled into the test project, all `MegaCrit`-free except where noted):

- **`BossCandidateSampler.SampleDistinct<T>(IReadOnlyList<T> pool, int count, Random rng) → IReadOnlyList<T>`** — partial Fisher-Yates; deterministic under seeded RNG; no game references. Unit-tested.
- **`BossVoteSeed.Stable(string? runSeed, int actIndex) → int`** — FNV-1a 32-bit stable hash. No game references. Unit-tested.
- **`BossVoteResolver.ResolveWinner<T>(IReadOnlyList<T> options, int winnerIndex) → T`** — bounds-checked index → option lookup. Throws `ArgumentOutOfRangeException` on invalid index (caller handles by setting winnerIndex to null upstream). Unit-tested.

**Runtime hook** (NOT a test seam, NOT unit-tested — operator-validated only):

```csharp
internal static Action<IRunState, EncounterModel> ApplyBossSwap { get; set; }
    = (rs, boss) => MapCmd.SetBossEncounter(rs, boss);
```

Lives inside `BossVotePatch.cs` (which is excluded from `Compile` in the test csproj). The delegate is a runtime override hook for operator debugging (e.g., logging swaps without actually applying), not for unit tests. The unit-testable winner-mapping is in `BossVoteResolver`.

<!-- CHANGED v3: explicit decoupling of testable helpers vs runtime hook — R2B O2 (code-validated against B.2.1 SkipBudgetTracker pattern) -->

`MapCmd.SetBossEncounter` side effects (from decompile):
- `runState.Act._rooms.Boss = winnerEncounter` (via `ActModel.SetBossEncounter`)
- `NRun.Instance.GlobalUi.TopBar.BossIcon.RefreshBossIcon()` (skipped under `TestMode.IsOn`)
- `NMapScreen.Instance?.SetMap(runState.Map, runState.Rng.Seed, clearDrawings: false)` (skipped under `TestMode.IsOn`)

**Critical**: `MapCmd.SetBossEncounter` does NOT re-validate or re-roll `_rooms.SecondBoss`. The DoubleBoss exclusion in candidate sampling above is required because `ActModel.cs:287-291` rolls the SecondBoss once at run start against the original primary, never re-validating after a primary swap.

### Popup architecture

**Pattern**: matches the established `VoteTallyLabel` pattern verbatim where applicable — parented under `tree.Root`, `BbcodeEnabled = true` on text rendering, lifecycle via `session.Closed` / `session.Cancelled` event subscriptions calling an idempotent `SafeQueueFree()` **marshaled through `IMainThreadDispatcher.Post(...)`** to guarantee main-thread context. Live tally + timer updates via `_Process` polling `session.Snapshot()` — NOT subscribed to `TallyChanged` (which can fire on the chat parser's thread per `VoteSession.cs:196`).

<!-- CHANGED v3: cleanup is now dispatcher-marshaled, not relying on QueueFree thread-safety claim — R2B O4 -->

**Constructor**: `BossVotePopup(IReadOnlyList<BossVotePopupOption> options, VoteSession session, IMainThreadDispatcher dispatcher)`. The popup is MegaCrit-free — it consumes `BossVotePopupOption` DTOs, not `EncounterModel`. The patch maps `EncounterModel` → `BossVotePopupOption` before constructing the popup (Prefix step 8).

```csharp
internal sealed record BossVotePopupOption(int Index, string Title, string? PortraitPath);
```

<!-- CHANGED v3: DTO record introduced; popup is MegaCrit-free — R2B O3 -->

**Root**: a new `CanvasLayer` parented to `(Engine.GetMainLoop() as SceneTree).Root`. Owned by `BossVotePopup`. Layer index exposed as `BossVotePopup.LAYER_INDEX = 100` const (spike item: verify no vanilla `CanvasLayer` uses 100 — adjust if it does). `ProcessMode = ProcessModeEnum.Always` so the timer label keeps updating if the streamer pauses mid-vote (vote timer is real-time, not game-time).

**Children**:
- **Backdrop**: full-screen `ColorRect` with `Color(0, 0, 0, 0.6)` and `MouseFilter = MouseFilterEnum.Stop` so clicks do not reach the chest-room UI beneath it.
- **Input swallowing**: popup root `Control` overrides `_UnhandledInput` to swallow `ui_accept` and `ui_cancel` actions, preventing accidental keyboard/gamepad-confirm activation of the underlying Proceed button: <!-- CHANGED v3: keyboard ui_accept swallowing — R2B O5 -->
  ```csharp
  public override void _UnhandledInput(InputEvent @event) {
      if (@event.IsActionPressed("ui_accept") || @event.IsActionPressed("ui_cancel")) {
          GetViewport().SetInputAsHandled();
      }
  }
  ```
- **Content `VBoxContainer`**:
  - **Title `RichTextLabel`** with `BbcodeEnabled = true`, rendering `"ACT {n} BOSS VOTE"` where `{n}` is `runState.CurrentActIndex + 1` (spike item: verify `CurrentActIndex` is 0-based).
  - **Timer `RichTextLabel`** with `BbcodeEnabled = true`, updated from `_Process` polling `session.TimeRemaining.TotalSeconds`.
  - **`HBoxContainer`** with up to 3 column `VBoxContainer`s (2 if degenerate pool). Each column:
    - `TextureRect` loading from `option.PortraitPath`. Defensive: check `string.IsNullOrEmpty(option.PortraitPath)` first and wrap `ResourceLoader.Load<Texture2D>` in try/catch; on null/empty/throw/null-result, leave `TextureRect.Texture = null` (empty box).
    - `RichTextLabel` with `BbcodeEnabled = true` rendering `#{option.Index} {option.Title}`.
    - Tally `RichTextLabel` updated from `_Process` polling `session.Tallies[option.Index]` — bar made of `▮` characters + count.

**Lifecycle**:
- `session.Closed += handler` and `session.Cancelled += handler`; handlers do `_dispatcher.Post(SafeQueueFree)` — dispatcher marshals to main thread before the actual free. <!-- CHANGED v3: dispatcher-marshaled cleanup — R2B O4 -->
- `_ExitTree` unsubscribes from both events to prevent dangling delegate handlers.
- `SafeQueueFree` is idempotent (`GodotObject.IsInstanceValid` + `!IsQueuedForDeletion` checks before `QueueFree`).

**Coexistence with `VoteTallyLabel`**: B.3 also calls `coordinator.Dispatcher.Post(() => VoteTallyLabel.AttachTo(session))` matching the existing pattern. Smoke A includes a stream-feed visual check: if the corner `VoteTallyLabel` + modal `BossVotePopup` showing the same tally simultaneously feels redundant on a typical viewer-aspect-ratio capture, raise as polish (operator's call). <!-- CHANGED v3: explicit falsifiable threshold — R2A micro-nit -->

**No interaction with vanilla's `NOverlayStack`**.

### Reused verbatim (no change)

- `VoteCoordinator` + `VoteSession`
- `EnglishReceipts`
- `VoteReceiptPolicy.Default`
- 0-indexed labels
- Multi-platform tally aggregation
- Run-id guards
- Two-flag suspend-and-resume Harmony pattern
- Cancellation/ignored-result receipt path (B.3 has its own private helper modeled on `CardRewardVotePatch.cs:397-408`).
- `[Collection("TiLog.Sink")]` isolation for any test class that triggers `TiLog.*`

## Code changes

**New files:**
- `src/Game/DecisionVotes/BossVotePatch.cs` — Harmony prefix on `NTreasureRoom.OnProceedButtonPressed` + `HandleVoteAsync` + `ResumeOnMainThread(int? winnerIndex, ...)` + `SendIgnoredResultReceipt` + `ApplyBossSwap` runtime hook. Modelled on `CardRewardVotePatch.cs`. Excluded from `Compile` in the test csproj.
- `src/Game/DecisionVotes/BossCandidateSampler.cs` — pure helper. `SampleDistinct<T>(IReadOnlyList<T> pool, int count, Random rng)`. Partial Fisher-Yates; no game references; compiles into tests.
- `src/Game/DecisionVotes/BossVoteSeed.cs` — pure helper. `Stable(string? runSeed, int actIndex) → int`. FNV-1a 32-bit; null-safe; compiles into tests. <!-- CHANGED v3: new pure helper — R2B O1 -->
- `src/Game/DecisionVotes/BossVoteResolver.cs` — pure helper. `ResolveWinner<T>(IReadOnlyList<T> options, int winnerIndex) → T`. Bounds-checked; compiles into tests. <!-- CHANGED v3: new pure helper — R2B O2 -->
- `src/Game/Ui/BossVotePopupOption.cs` — DTO record: `(int Index, string Title, string? PortraitPath)`. No game references. <!-- CHANGED v3: DTO file — R2B O3 -->
- `src/Game/Ui/BossVotePopup.cs` — Godot `CanvasLayer`-rooted Control consuming `BossVotePopupOption` DTOs and `VoteSession`. Accepts `IMainThreadDispatcher` in constructor for cleanup marshalling. Subscribes to `session.Closed` / `session.Cancelled` (NOT `TallyChanged`); polls `session.Snapshot()` from `_Process`.
- `tests/Game/DecisionVotes/BossCandidateSamplerTests.cs` — pure-logic tests for sampling.
- `tests/Game/DecisionVotes/BossVoteSeedTests.cs` — pure-logic tests for stable hash: same input → same output across repeated calls; different acts produce different outputs; null/empty seed handled. <!-- CHANGED v3: new test file -->
- `tests/Game/DecisionVotes/BossVoteResolverTests.cs` — pure-logic tests for resolver: valid index → correct option; out-of-range throws. <!-- CHANGED v3: new test file -->

All test files marked `[Collection("TiLog.Sink")]`.

**Modified files:**
- `src/Ti/Voting/EnglishReceipts.cs` — new receipt entries (boss vote open / close / ignored-result).
- `src/ModEntry.cs` — register `BossVotePatch` with the Harmony instance during init.
- `tests/slay_the_streamer_2.tests.csproj` — include the new test file paths; exclude `BossVotePatch.cs` from `Compile`. `BossVotePopup.cs` compiles into tests (it's MegaCrit-free now that it takes DTOs).
- `notes/06-followups-and-deferred.md` — add B.3 acceptance-gate results section.
- `README.md` — status section bump after B.3 ships.

**Code-size estimate:** 400–550 LOC across the patch + popup + 3 helpers + DTO + tests. Larger than v2's estimate of 350-500 because of the additional helpers + DTO + their tests. Still comparable to a single B.2.1-style decision-vote slice. <!-- CHANGED v3: estimate bumped slightly for new helpers — R2B O1, O2, O3 -->

## Receipt strings

Match the voice / shape of existing slices. `{BossN}` resolves to `EncounterModel.Title.GetFormattedText()` (rendered in popup column labels via the DTO's `Title` field).

- **Open**: `"Act {n} boss vote opened — !vote #0 {Boss0}, #1 {Boss1}, #2 {Boss2} ({duration}s)"` (2-option variant drops `#2`).
- **Periodic tally**: reuses `VoteReceiptPolicy.Default`. Format: `"Boss tally — #0 {a}, #1 {b}, #2 {c} ({remaining}s left)"`. Periodic-tally dedup compares structural tally state.
- **Close**: `"Chat picked #{winner} {WinnerBoss} ({votes} votes)"` — fired by `VoteSession.CloseNowInternal` regardless of whether the resume succeeds.
- **Ignored-result** (resume liveness check fails): `SendIgnoredResultReceipt()` private helper. Fires AFTER the normal close receipt — chat may see both. Receipt text: `"Vote result ignored — run abandoned during boss vote"`. Wording acknowledges the dual-receipt reality: chat sees "Chat picked X" followed by "ignored". This is consistent with B.2.1 behavior. <!-- CHANGED v3: wording acknowledges close + ignored sequence — R2B O11 -->

## Edge cases & risks

- **Pool size ≤ 1** — release `_voteInProgress`, bail to vanilla. Logged at `Info`.
- **Pool size 2 (degenerate-but-valid)** — 2-option vote. Implementation must verify every downstream path is option-count-agnostic (no hardcoded `#2` in receipts, popup column count, log strings). The upcoming implementation plan includes an explicit task to grep for hardcoded `#2`. <!-- CHANGED v3: explicit grep task callout — R2B O12 -->
- **A10+ DoubleBoss duplicate-boss collision** — **Resolved by exclusion** with null-defensive guard.
- **Re-entrancy on double-Proceed-click vs synthetic resume click** — Three-way prefix branch as specified above.
- **Vote-result fallback when AwaitWinnerAsync throws / out-of-range** — `winnerIndex = null` propagates to `ResumeOnMainThread`; the resume skips `ApplyBossSwap` but still re-clicks Proceed. Vanilla's pre-rolled boss is preserved. **The "no lost click" invariant is**: once the prefix returns `false`, the synthetic Proceed re-click ALWAYS fires unless a liveness check (run-abandon, IsGameOver, run-id drift) fails — in which case the run is over anyway. <!-- CHANGED v3: explicit no-winner fallback shape — R2A + R2B 2/2 -->
- **`_isRelicCollectionOpen` at resume time** — spike item below. What if the streamer opened the relic-collection overlay during the 30s vote? Verify whether `OnProceedButtonPressed()` no-ops or throws in that case. <!-- CHANGED v3: new edge case — R2A -->
- **Run-id drift / abandonment / IsGameOver during the vote** — covered by the existing run-id-guard pattern. `SendIgnoredResultReceipt()` fires AFTER `VoteSession`'s normal close receipt (matching B.2.1's double-message reality).
- **First-defeat achievement key** — Smoke G verifies empirically.
- **`MapCmd.SetBossEncounter` in `TestMode.IsOn`** — informational; we don't run in test mode at runtime.
- **Asset load failure** — defensive null/empty/try-catch on `ResourceLoader.Load<Texture2D>`; empty box on failure.
- **Force-close (Alt+F4) mid-vote** — `CanvasLayer` dies with the process; no swap fires.
- **Save-load mid-vote** — vote is ephemeral. Reload returns the streamer to the chest room with the pre-Proceed state. Re-clicking Proceed produces the **same 3 candidates** via the stable `BossVoteSeed.Stable(StringSeed, ActIndex)` hash. Smoke H verifies. <!-- CHANGED v3: stable hash claim — R2B O1 -->
- **Pause mid-vote** — `CanvasLayer.ProcessMode = Always`.
- **Keyboard `ui_accept` / `ui_cancel` during vote** — swallowed by popup's `_UnhandledInput` override.
- **TiLog test isolation** — `[Collection("TiLog.Sink")]` on all new test classes.
- **Periodic-tally dedup** — comparing tally STATE.

## Testing strategy

### Unit tests

All test files marked `[Collection("TiLog.Sink")]`.

**`tests/Game/DecisionVotes/BossCandidateSamplerTests.cs`** — pure-logic tests:
1. Sampling determinism (same seed → same N candidates).
2. No duplicates.
3. Pool size 3+ → exactly 3.
4. Pool size 2 → exactly 2.
5. Pool size 1 → 1.
6. Pool size 0 → 0.

**`tests/Game/DecisionVotes/BossVoteSeedTests.cs`** — pure-logic tests for stable hash: <!-- CHANGED v3: new test file -->
1. Same `(runSeed, actIndex)` → same int across repeated calls (within-process determinism).
2. Different `actIndex` with same `runSeed` → different int.
3. Different `runSeed` with same `actIndex` → (almost always) different int.
4. Empty `runSeed` returns a valid int (no throw).
5. Null `runSeed` returns a valid int (no throw).
6. Specific cross-process determinism is operator-validated via Smoke H, not unit-testable.

**`tests/Game/DecisionVotes/BossVoteResolverTests.cs`** — pure-logic tests: <!-- CHANGED v3: new test file -->
1. Valid index → correct option.
2. Out-of-range index → `ArgumentOutOfRangeException`.
3. Negative index → `ArgumentOutOfRangeException`.

The Harmony patch file (`BossVotePatch.cs`) is excluded from `Compile`. `BossVotePopup.cs` is included (now MegaCrit-free thanks to the DTO).

### Operator-validation gate

Manual smoke tests required before tagging `plan-b-3-complete`:

1. **Smoke A — Act 1 happy path**: standard run, exit Act 1 chest, vote popup appears, 3 portraits render, 30s timer counts down (including during pause), chat votes via `!vote #N`, popup closes, top-bar boss icon updates, walk to Act 1 boss → expected fight starts. **Visual check**: no orphaned `CanvasLayer`. **Stream-feed visual check**: if corner `VoteTallyLabel` + modal `BossVotePopup` showing the same tally feels redundant, flag as polish (operator's judgment call). Also verify pressing Enter / Space / gamepad-A during the vote does NOT advance Proceed. <!-- CHANGED v3: keyboard-confirm check + falsifiable redundancy threshold — R2A + R2B -->
2. **Smoke B — Act 2 + Act 3 coverage** (non-DoubleBoss).
3. **Smoke C — A10+ DoubleBoss**: A10 run, Act 3 chest. **Verification**: log `[boss-vote] HasSecondBoss=true; excluding {id}` and confirm the popup's 3 candidates do NOT include the second boss. Vote on primary. After swap, **assert primary ≠ second**. Confirm both bosses fight in sequence. <!-- CHANGED v3: explicit log-line check for HasSecondBoss timing — R2A O8 -->
4. **Smoke D — run abandoned mid-vote**: open chest-room vote, abandon run. Chat sees both the normal close receipt AND the ignored-result receipt. No orphaned `CanvasLayer`. <!-- CHANGED v3: dual-receipt acknowledged — R2B O11 -->
5. **Smoke E — chat disabled**.
6. **Smoke F — multiplayer bail**.
7. **Smoke G — first-defeat achievement check**.
8. **Smoke H — save & reload mid-vote**: open vote, note the 3 candidates, Save & Quit. Reload. Re-click Proceed; **the same 3 candidates appear** (validates `BossVoteSeed.Stable` is genuinely stable across process launches — Smoke H is the cross-process determinism test, since unit tests can only verify within-process). Vote proceeds normally. <!-- CHANGED v3: explicit cross-process determinism check — R2B O1 -->
9. **Smoke I — relic-collection overlay mid-vote**: open chest, click on the relic in the chest, click Proceed to open the vote, then while vote is running, open the relic-collection overlay (deck button or similar). Verify popup stays modal (input swallowed), vote completes, synthetic Proceed re-click works correctly even with the overlay open or after it's closed. <!-- CHANGED v3: new smoke — R2A O7 -->

Results recorded in [`notes/06-followups-and-deferred.md`](../../../notes/06-followups-and-deferred.md).

### Log points

Expected `[SlayTheStreamer2][boss-vote]` log lines: <!-- CHANGED v3: log enumeration unchanged from v2 plus the new HasSecondBoss + no-winner lines -->

- `[boss-vote] target resolved: NTreasureRoom.OnProceedButtonPressed` (Prepare, once)
- `[boss-vote] degenerate pool (count=N); skipping vote`
- `[boss-vote] only N bosses available for Act X — possible content change?` (warn, pool < 3)
- `[boss-vote] HasSecondBoss=true; excluding {secondBossId} from sample` (info, A10+ Act 3)
- `[boss-vote] HasSecondBoss true but SecondBossEncounter missing` (warn, defensive)
- `[boss-vote] multiplayer detected ...` (one-shot warn, then debug)
- `[boss-vote] chat not readable (state=X); bailing to vanilla` (debug)
- `[boss-vote] opening vote for N options; seed={hash}` (info — includes the FNV-1a seed for cross-platform debugging)
- `[boss-vote] sampled candidates: #0=BossA(id), #1=BossB(id), #2=BossC(id)` (info)
- `[boss-vote] resume: applying winner #N on main thread` (info)
- `[boss-vote] resume: no winner; preserving vanilla boss` (info — when AwaitWinnerAsync threw or out-of-range) <!-- CHANGED v3: new log line for no-winner path -->
- `[boss-vote] resume aborted: <reason>` (warn — run-abandon, IsGameOver, run-id drift, room invalid)
- `[boss-vote] ignored-result receipt queued` (info) <!-- CHANGED v3: renamed from "cancellation receipt" — R2B O11 -->

## Pre-implementation verifications (spike items)

1. **`ActModel.AllBossEncounters` returns ≥ 3 per vanilla act** — confirm via decompile.
2. **`ActModel.AllBossEncounters` is NOT unlock-filtered** — confirm via decompile.
3. **`ActModel.SecondBossEncounter` and `HasSecondBoss` accessibility + timing** — `HasSecondBoss` is set at run start when `AscensionManager.HasLevel(DoubleBoss)` is true AND on the final act (per `RunManager.cs:499-502`). Confirm by logging during Smoke C. <!-- CHANGED v3: timing clarified — R2A O8 -->
4. **`NTreasureRoom.OnProceedButtonPressed` is parameterless** — confirm method signature.
5. **`BossVotePatch.Prepare` reflection surface** — ONLY check what the patch body actually uses (the method signature itself). Do NOT preemptively reflect `_proceedButton` or other fields the patch does not read; per `CardRewardVotePatch` pattern, `Prepare` only verifies what's load-bearing. <!-- CHANGED v3: reframed per R2B O10 -->
6. **`runState.CurrentActIndex` semantics** — 0-based or 1-based?
7. **Boss-portrait asset paths** — extension handling.
8. **`EncounterModel.Title.GetFormattedText()` plain text vs BBCode**.
9. **`CanvasLayer.Layer = 100` collision check**.
10. **`_isRelicCollectionOpen` interaction with `OnProceedButtonPressed`** — does vanilla's Proceed handle the overlay-open case? Smoke I covers it empirically, but a quick decompile pass during the spike avoids the round trip. <!-- CHANGED v3: new spike item — R2A O7 -->

## Acceptance gate

- `dotnet test` is green (including new `BossCandidateSamplerTests`, `BossVoteSeedTests`, `BossVoteResolverTests`).
- All Smoke A–I operator-validation steps pass on a fresh `./build.ps1` + `./install.ps1`. <!-- CHANGED v3: smoke range extended -->
- Log inspection: enumerated `[SlayTheStreamer2][boss-vote]` lines appear for each tested smoke; no exceptions on the boss-swap path.
- Runtime startup hash matches `git log -1 --format=%H` post-merge.
- Acceptance results documented in [`notes/06-followups-and-deferred.md`](../../../notes/06-followups-and-deferred.md).
- Tag `plan-b-3-complete` once green.

## Justification: deferred extraction of suspend-and-resume base class

With B.3, n=3 patches share the suspend-and-resume shape. Rule of Three has technically fired. **Extraction deferred** because B.3's resume path is structurally different (calls `ApplyBossSwap` *first*, then synthetically re-clicks Proceed; other two patches just re-call the original method). A shared base would need a "pre-resume action" hook. Better to ship n=3 and extract with all three visible.

## Cross-references

- [`notes/10-boss-vote-feasibility.md`](../../../notes/10-boss-vote-feasibility.md)
- [`notes/06-followups-and-deferred.md`](../../../notes/06-followups-and-deferred.md)
- [`notes/09-settings-and-tunable-knobs.md`](../../../notes/09-settings-and-tunable-knobs.md)
- [`docs/superpowers/specs/2026-05-10-plan-b-2-1-card-reward-vote-design-v4.md`](2026-05-10-plan-b-2-1-card-reward-vote-design-v4.md)
- [`docs/superpowers/specs/2026-05-13-plan-b-2-2-ancient-vote-design.md`](2026-05-13-plan-b-2-2-ancient-vote-design.md)
- [`docs/superpowers/specs/META-REVIEW-2026-05-13-plan-b-3-boss-vote-design.md`](META-REVIEW-2026-05-13-plan-b-3-boss-vote-design.md) — round-1 meta-review (8 reviewers of v1 → v2).
- [`docs/superpowers/specs/META-REVIEW-round2-2026-05-13-plan-b-3-boss-vote-design.md`](META-REVIEW-round2-2026-05-13-plan-b-3-boss-vote-design.md) — round-2 meta-review (2 reviewers of v2 → v3).
- [`MapCmd.SetBossEncounter`](../../../decompiled/sts2/MegaCrit/sts2/Core/Commands/MapCmd.cs)
- [`ActModel.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/Models/ActModel.cs)
- [`EncounterModel.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/Models/EncounterModel.cs)
- [`TreasureRoom.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/Rooms/TreasureRoom.cs) + [`NTreasureRoom.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Rooms/NTreasureRoom.cs)
- [`CardRewardVotePatch.cs`](../../../src/Game/DecisionVotes/CardRewardVotePatch.cs)
- [`AncientVotePatch.cs`](../../../src/Game/DecisionVotes/AncientVotePatch.cs)
- [`VoteTallyLabel.cs`](../../../src/Ti/Ui/VoteTallyLabel.cs)
- [`VoteSession.cs:267-277`](../../../src/Ti/Voting/VoteSession.cs#L267-L277) — tie-break / 0-vote semantics (inherited by B.3).

---

## Round-2 Optional Enhancements

The round-2 reviewers raised no additional Consider-tier items; all their findings landed as Must-do or Should-do and are applied above. The round-1 Optional Enhancements table (items 1, 2, 6, 7, 8, 9, 10 not-applied; 3, 4, 5 applied) carries forward unchanged.
