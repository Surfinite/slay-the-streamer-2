# 2026-05-13 — Plan B.3: Boss Vote (design v2)

**Date**: 2026-05-13
**Status**: Design v2 — post-meta-review. Implementation pending.
**Slice**: B.3 (chat-picks-the-next-act-boss vote) — fifth voting slice after B.1 (Neow), B.2.1 (card reward), v0.2 yt-chat, and B.2.2 (Ancients, implementation-complete on `main` at time of writing — operator-validation gate pending, not yet tagged complete; B.3 does not depend on B.2.2's tag).
**Scope**: Add a chest-room-exit vote where chat picks the next act's boss from up to 3 randomly sampled candidates. Triggered on `NTreasureRoom.OnProceedButtonPressed` via suspend-and-resume; resolved via `MapCmd.SetBossEncounter`. New Godot `CanvasLayer`-rooted popup with portrait columns and per-column tally labels.

<!-- CHANGED v2: header restructured to be post-meta-review; minor wording on "up to 3" candidates to make pool-size policy unambiguous — Reviewers 2, 4, 5, 6, 7 -->

## TL;DR

Feasibility was settled in [`notes/10-boss-vote-feasibility.md`](../../../notes/10-boss-vote-feasibility.md). Vanilla exposes `MapCmd.SetBossEncounter(IRunState, EncounterModel)` — a public static one-liner that swaps the act's boss, refreshes the top-bar boss icon, and re-renders the map preserving streamer drawings. We hook `NTreasureRoom.OnProceedButtonPressed` with a Harmony prefix using the existing two-flag suspend-and-resume pattern (`_voteInProgress` + `_resumeInProgress`) verbatim from `CardRewardVotePatch.cs`: prefix returns `false`, kicks off a 30s vote async, resumes the Proceed click on the main thread after `MapCmd.SetBossEncounter` is applied. The popup is a self-owned `CanvasLayer` + opaque-input `ColorRect` backdrop + N-column Control (PNG portrait + boss title + tally label per column), with no interaction with vanilla's overlay-stack. Vote fires on every act (1, 2, 3). On A10+ Ascension's `DoubleBoss` runs (Act 3 only), the chat-picked primary boss is excluded from colliding with the pre-rolled second boss by removing `SecondBossEncounter.Id` from the candidate pool before sampling. Net change: ~1 new patch class, ~1 new Control class, ~1 cancellation-receipt helper, modest receipt additions, no new settings knobs.

<!-- CHANGED v2: TL;DR adds explicit two-flag mention, DoubleBoss exclusion fix, and "opaque-input" backdrop term to compress key clarifications — Reviewers 1-8 -->

## Goals

- Chat votes on the upcoming act boss every time the streamer exits a chest room. Up to 3 random candidates from `runState.Act.AllBossEncounters` (excluding the pre-rolled SecondBossEncounter on A10+ Act 3), 30s vote window, latest-wins semantics, 0-indexed labels (`#0 / #1 / #2`).
- Visual popup overlays the chest scene (matches the original Tempus mod's screenshot reference) with portrait columns showing each candidate's boss icon, title, and live tally bar. Backdrop blocks input.
- Winner is applied via `MapCmd.SetBossEncounter` before the streamer transitions to the map screen, so the new boss is in place when the boss room is later entered.
- Reuse B.1 / B.2.1 / B.2.2 infrastructure verbatim: `VoteCoordinator`, `VoteSession`, two-flag suspend-and-resume Harmony pattern, `EnglishReceipts`, periodic-tally cadence, multi-platform aggregation, run-id guards, cancellation receipt path.
- Singleplayer-only v1 — bail to vanilla in multiplayer (matches existing slices).

## Non-goals

- **Per-vote settings toggle (`voteOnBoss: bool`)** — not wired in v1. Matches the current state of Neow and Card Reward votes (also no toggle yet per [`notes/09`](../../../notes/09-settings-and-tunable-knobs.md)). Will be addressed in a future cross-cutting "B.2 surface toggles" slice rather than per-slice.
- **Per-decision vote-duration override** — 30s hardcoded to match Neow / Card Reward. Project decision: durations will eventually become globally-configurable (one knob, not per-vote); deferred to a future settings slice. <!-- CHANGED v2: replaced "Surfinite has explicitly noted that" — Reviewers 1, 2 -->
- **Silhouette / spoiler-safe mode** (`showBossNames: false`) — target audience is unlocked-everything streamers per [`notes/10`](../../../notes/10-boss-vote-feasibility.md); deferred to future polish.
- **A10+ DoubleBoss pair-vote / chained second-boss vote** — vote on primary only; vanilla's pre-rolled second boss is preserved (and excluded from the sample to avoid duplicate-boss collision). Pair-vote and chained-vote variants documented as future polish.
- **Multiplayer sync** — `MapCmd.SetBossEncounter` mutates `runState.Act` locally and has no network propagation in vanilla. We bail to vanilla in multiplayer; a sync message becomes a separate slice if/when multiplayer support is added across all votes.
- **Spine-animated boss portraits** — PNG path only (no `_outline.png` overlay in v1). Spine animation is polish. <!-- CHANGED v2: dropped optional outline TextureRect — Reviewer 8 -->
- **"Keep current boss" explicit option** — sample is fresh from the full pool (current boss may or may not be in the sample by chance); no current-boss preservation logic.
- **No special achievement compensation logic** — `ActModel.DefeatedAllEnemiesAchievement` appears to key on the act, not the encounter, so chat-swapping the boss should not break first-defeat achievements. Smoke G verifies this empirically at acceptance time; no code-side compensation is attempted. <!-- CHANGED v2: rephrased from "Achievement-gate verification" non-goal — Reviewers 1, 2, 4, 6 -->

## Architecture

### Trigger surface

Harmony **prefix** on `NTreasureRoom.OnProceedButtonPressed` (Option B in the feasibility doc). Prefix returns `false` to suspend the original click, then resumes it on the main thread after the vote closes via `coordinator.Dispatcher.Post`.

**Two-flag re-entry guard** (copied verbatim from `CardRewardVotePatch.cs` / `AncientVotePatch.cs`): <!-- CHANGED v2: explicit two-flag spec replaces TBD — Reviewers 1, 2, 3, 4, 5, 6, 7, 8 -->

```csharp
private static int _voteInProgress;
private static int _resumeInProgress;
```

**Prefix ordering** (matches `CardRewardVotePatch.Prefix` shape — also corrects the v1 flow diagram which had `coordinator.Start` BEFORE the atomic acquire): <!-- CHANGED v2: ordering corrected per Reviewer 2 code-validation -->

1. `if (_resumeInProgress == 1) return true;` — this is the synthetic resume re-call; let vanilla through unimpeded.
2. `if (!GodotObject.IsInstanceValid(__instance)) return true;` — defensive node-validity check.
3. Multiplayer bail: `if (TryGetPlayerCount() > 1) return true;` (with one-shot warn log, debug-on-repeat).
4. Chat-readable bail: `if (Voter.Default?.Chat.State is not (ConnectedReadOnly or ConnectedReadWrite)) return true;`. <!-- CHANGED v2: corrected from "chatService.State" — actual API is `coordinator.Chat.State` per code -->
5. `if (Interlocked.CompareExchange(ref _voteInProgress, 1, 0) != 0) return false;` — atomic acquire; rejects streamer double-click silently.
6. Snapshot the candidate pool (see Candidate sampling below). If pool size ≤ 1, **release `_voteInProgress` and return `true`** (bail to vanilla — the "single-option vote is degenerate" pattern from `CardRewardVotePatch.cs:207`). <!-- CHANGED v2: explicit single-option degenerate handling — Reviewers 2, 6, 7 -->
7. Capture run-id soft guard (same `RunIdGuardEnabled` static / `Rng.StringSeed` snapshot pattern as `CardRewardVotePatch.cs:222-230`).
8. `coordinator.Start("Act N boss vote", labels, TimeSpan.FromSeconds(30))` inside try/catch. On throw: release `_voteInProgress`, return `true`.
9. Instantiate `BossVotePopup` inside try/catch. On throw: cancel the session via `session.Cancel()`, release `_voteInProgress`, return `true`. <!-- CHANGED v2: popup-construction try/catch added — Reviewer 1 -->
10. `_ = HandleVoteAsync(coordinator, __instance, session, sample, runIdAtStart)` — fire-and-forget.
11. `return false` — suspend original click.

**Resume ordering** (`ResumeOnMainThread` on the main thread via `coordinator.Dispatcher.Post`):

```csharp
Interlocked.Exchange(ref _resumeInProgress, 1);
try {
    // validity + liveness checks (room valid, RunManager not abandoned, IsGameOver false,
    //   run-id drift check, SecondBossEncounter still present if A10+)
    // if any check fails: SendCancellationReceipt(); return;
    var winnerEncounter = sample[winnerIndex];
    ApplyBossSwap(runState, winnerEncounter);   // delegate seam, defaults to MapCmd.SetBossEncounter
    treasureRoom.OnProceedButtonPressed();       // synthetic re-call; _resumeInProgress lets prefix pass through
} catch (Exception ex) {
    TiLog.Error("[SlayTheStreamer2][boss-vote] resume threw", ex);
} finally {
    Interlocked.Exchange(ref _resumeInProgress, 0);
    Interlocked.Exchange(ref _voteInProgress, 0);    // cleared LAST, after _resumeInProgress
}
```

<!-- CHANGED v2: explicit resume ordering with both flags + finally-clear order — Reviewers 1, 2, 3, 4, 5, 6, 7, 8 -->

### Vote flow (corrected)

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
    │  ⑤ coordinator.Start("Act N boss vote", ...)         (try/catch → release+bail on throw)
    │  ⑥ new BossVotePopup(...)                            (try/catch → cancel+release+bail on throw)
    │  ⑦ _ = HandleVoteAsync(coordinator, __instance, session, sample, runIdAtStart)
    │  ⑧ return false                                      (suspend original click)
    │
    ▼
HandleVoteAsync                                   ─ background
    │  coordinator.Dispatcher.Post(() => VoteTallyLabel.AttachTo(session))   ← consistency with B.1/B.2.1
    │  winnerIndex = await session.AwaitWinnerAsync()   (catch → falls back to playerClickIndex)
    │  coordinator.Dispatcher.Post(() => ResumeOnMainThread(...))
    │     outer try/catch posts fallback ResumeOnMainThread with playerClickIndex on async failure
    │     (mirrors CardRewardVotePatch.cs:273-282 "no lost click" promise)
    │
    ▼
ResumeOnMainThread(...)                           ─ main thread
    │  Interlocked.Exchange(ref _resumeInProgress, 1)
    │  try:
    │    liveness + validity + run-id-drift checks
    │      → on any fail: SendCancellationReceipt(); return
    │    ApplyBossSwap(runState, sample[winnerIndex])      (delegate; defaults to MapCmd.SetBossEncounter)
    │    treasureRoom.OnProceedButtonPressed()             (synthetic re-call; prefix passes through via _resumeInProgress)
    │  catch: TiLog.Error
    │  finally:
    │    Interlocked.Exchange(ref _resumeInProgress, 0)
    │    Interlocked.Exchange(ref _voteInProgress, 0)      (vote-flag cleared LAST)
    │
    ▼
Vanilla chest → map → boss room with swapped boss
    │  BossVotePopup freed via session.Closed / session.Cancelled subscription
    │  (subscribes to events but cleanup goes through SafeQueueFree — Godot's deferred-free is thread-safe)
```

<!-- CHANGED v2: full flow diagram rewritten to match actual code order — Reviewer 2 -->

### Candidate sampling

```csharp
List<EncounterModel> pool = runState.Act.AllBossEncounters.ToList();     // materialize once
if (runState.Act.HasSecondBoss) {
    var secondId = runState.Act.SecondBossEncounter.Id;
    pool.RemoveAll(e => e.Id == secondId);
}
if (pool.Count < 3) {
    // Future-proofing against MegaCrit content updates: a sub-3 pool is unexpected
    // for vanilla acts per the spike. Warn-log without bailing — 2-option vote still
    // runs; 0/1 still bails to vanilla below.
    TiLog.Warn($"[SlayTheStreamer2][boss-vote] only {pool.Count} bosses available for Act {runState.CurrentActIndex + 1} — possible content change?");
}
if (pool.Count <= 1) {
    // degenerate — release _voteInProgress and bail to vanilla
    return /* degenerate path */;
}

// Deterministic seeded RNG: same run + same act → same candidates on save-reload.
// Does NOT consume runState.Rng (run-deterministic generator stays untouched).
var seed = HashCode.Combine(runState.Rng.StringSeed, runState.CurrentActIndex);
var rng = new Random(seed);
var sample = BossCandidateSampler.SampleDistinct(pool, count: 3, rng);    // pure helper; partial Fisher-Yates
```

<!-- APPLIED v2.1: enhancement #4 (runtime pool-size warning) — Reviewer 4 -->

<!-- CHANGED v2: SecondBoss exclusion under DoubleBoss + materialize-once + HashCode.Combine seed + pure-helper extraction — Reviewers 2, 3, 4, 5, 6, 7, 8 -->

Policy summary:
- **Pool ≥ 3**: sample 3 → 3-option vote.
- **Pool == 2**: sample 2 → 2-option vote (the existing codebase supports degenerate-but-non-trivial votes; see `CardRewardVotePatch.cs:207` for the parallel pattern).
- **Pool ≤ 1**: release flag, bail to vanilla.
- **Current boss is NOT specifically excluded**. The sample may include it by chance; if chat votes for it, `MapCmd.SetBossEncounter(currentBoss)` is idempotent (only a redundant icon refresh).
- **A10+ Act 3**: `SecondBossEncounter` is excluded from the pool before sampling, so chat cannot vote for the boss the streamer is already scheduled to fight second.

### Boss-swap API and test seam

```csharp
internal static Action<IRunState, EncounterModel> ApplyBossSwap { get; set; }
    = (rs, boss) => MapCmd.SetBossEncounter(rs, boss);
```

<!-- CHANGED v2: delegate seam instead of IBossSwapper interface — Reviewers 3, 5, 8 -->

Production hits the default `MapCmd.SetBossEncounter`. Tests overwrite `ApplyBossSwap` with a mock to assert the index→encounter mapping without dragging `MegaCrit.Sts2.*` into the test project. Single-method seam; delegate is strictly lighter than introducing an interface and matches what the test-factoring shape of B.2.1 implicitly does (Lazy reflection of private methods → unit-tested pure helpers).

`MapCmd.SetBossEncounter` side effects (from decompile):
- `runState.Act._rooms.Boss = winnerEncounter` (via `ActModel.SetBossEncounter`)
- `NRun.Instance.GlobalUi.TopBar.BossIcon.RefreshBossIcon()` (top-bar update; skipped under `TestMode.IsOn`)
- `NMapScreen.Instance?.SetMap(runState.Map, runState.Rng.Seed, clearDrawings: false)` (map re-render preserving streamer drawings; skipped under `TestMode.IsOn`)

**Critical**: `MapCmd.SetBossEncounter` does NOT re-validate or re-roll `_rooms.SecondBoss`. The DoubleBoss exclusion in candidate sampling above is required because `ActModel.cs:287-291` rolls the SecondBoss once at run start against the original primary, never re-validating after a primary swap.

<!-- CHANGED v2: explicit "does NOT re-validate SecondBoss" callout — Reviewers 2, 3, 5, 6 (and verified against ActModel.cs:287-291) -->

### Popup architecture

**Pattern**: matches the established `VoteTallyLabel` pattern verbatim where applicable — parented under `tree.Root`, `BbcodeEnabled = true` on text rendering, lifecycle via `session.Closed` / `session.Cancelled` event subscriptions calling an idempotent `SafeQueueFree()` (handles off-main-thread cleanup safely since Godot's `QueueFree` is deferred-and-thread-safe). Live tally + timer updates via `_Process` polling `session.Snapshot()` — NOT subscribed to `TallyChanged` (which can fire on the chat parser's thread per `VoteSession.cs:196`).

<!-- CHANGED v2: explicit "poll via _Process; don't subscribe to TallyChanged" — Reviewers 2, 3, 5, 6, 8 (code-validated against VoteSession.cs:196 + VoteTallyLabel.cs:48-96) -->

**Root**: a new `CanvasLayer` parented to `(Engine.GetMainLoop() as SceneTree).Root`. Owned by `BossVotePopup`. Layer index exposed as `BossVotePopup.LAYER_INDEX = 100` const (spike item: verify no vanilla `CanvasLayer` uses 100 — adjust if it does). `ProcessMode = ProcessModeEnum.Always` so the timer label keeps updating if the streamer pauses mid-vote (vote timer is real-time, not game-time).

<!-- CHANGED v2: named const for layer index + ProcessMode.Always — Reviewers 7, 8 -->

**Children**:
- **Backdrop**: full-screen `ColorRect` with `Color(0, 0, 0, 0.6)` and `MouseFilter = MouseFilterEnum.Stop` so clicks do not reach the chest-room UI beneath it. Controller/gamepad input is NOT explicitly captured for v1 (acceptable gap for keyboard+mouse target audience; documented as v0.2 polish).
- **Content `VBoxContainer`**:
  - **Title `RichTextLabel`** with `BbcodeEnabled = true` (matching `VoteTallyLabel.cs:27`), rendering `"ACT {n} BOSS VOTE"` where `{n}` is `runState.CurrentActIndex + 1` (spike item: verify `CurrentActIndex` is 0-based per existing patches' usage).
  - **Timer `RichTextLabel`** with `BbcodeEnabled = true`, updated from `_Process` polling `session.TimeRemaining.TotalSeconds` (the actual `VoteSession` API per `VoteSession.cs:51`, NOT `SecondsRemaining`).
  - **`HBoxContainer`** with up to 3 column `VBoxContainer`s (2 if degenerate pool). Each column:
    - `TextureRect` loading the boss portrait. Path resolution: `BossNodePath + ".png"` if the path does not already end in `.png`; otherwise use `BossNodePath` directly (spike item: verify the exact extension convention against `EncounterModel.MapNodeAssetPaths`). **Defensive load**: check `string.IsNullOrEmpty(BossNodePath)` first and wrap the `ResourceLoader.Load<Texture2D>(path)` call in try/catch; on null/empty/throw/null-result, leave `TextureRect.Texture = null` (empty box). Vote still works regardless. <!-- APPLIED v2.1: enhancement #5 (defensive BossNodePath null/empty/throw guard) — Reviewer 3 -->
    - `RichTextLabel` with `BbcodeEnabled = true` rendering `#{i} {EncounterModel.Title.GetFormattedText()}` (matches existing `AncientVotePatch.cs:123` use of `Title.GetFormattedText()` for labels).
    - Tally `RichTextLabel` updated from `_Process` polling `session.Tallies[i]` — bar made of `▮` characters + count, matching the existing chat-tally aesthetic.

<!-- CHANGED v2: RichTextLabel + BbcodeEnabled + corrected to session.TimeRemaining + .png conditional concat + dropped _outline.png — Reviewers 7, 8 -->

**Layout**: ≥3 candidates → 3 columns ~30% width with 5% gutters; 2 candidates → 2 columns ~45% width with 5% center gutter. Normalized to whatever StS2's screen reference resolution is. Anchor-based `Control` layout, NOT pixel-positioned. Pixel-level dimensions left to operator validation polish.

<!-- CHANGED v2: explicit anchor-based + 2-column layout commitment — Reviewer 1 -->

**Lifecycle**:
- `session.Closed += handler` and `session.Cancelled += handler`; both handlers call `SafeQueueFree()` (idempotent wrapper checking `GodotObject.IsInstanceValid` + `!IsQueuedForDeletion` before calling `QueueFree`, matching `VoteTallyLabel.cs:133-137`).
- `_ExitTree` unsubscribes from both events to prevent dangling delegate handlers.
- **No Tween fade-in for v1** — instant popup; the previously-mentioned ~150ms fade-in is dropped to avoid races between Tween completion and `QueueFree` during scene transitions. <!-- CHANGED v2: dropped Tween fade-in — Reviewers 7, 8 -->

**Coexistence with `VoteTallyLabel`**: B.3 also calls `coordinator.Dispatcher.Post(() => VoteTallyLabel.AttachTo(session))` matching the existing pattern in `CardRewardVotePatch.cs:256` and `AncientVotePatch.cs:159`. The corner `VoteTallyLabel` and the modal `BossVotePopup` don't visually overlap (corner vs center). Consistency wins over mild redundancy. Re-evaluate during operator validation if it's actually a viewer-side problem.

<!-- CHANGED v2: explicit VoteTallyLabel attach decision — Reviewers 2, 5 -->

**No interaction with vanilla's `NOverlayStack`** — we own everything we created. Rejected: piggybacking on `NOverlayStack` (Reviewer 4's recommendation) would couple to vanilla's `IOverlayScreen` interface contract, which can change between MegaCrit builds. The self-owned pattern is what `VoteTallyLabel` uses successfully across all 4 prior slices; B.3 stays consistent.

<!-- CHANGED v2: explicit rejection rationale for NOverlayStack — Reviewer 4 (rejected with reason per meta-review) -->

### Reused verbatim (no change)

- `VoteCoordinator` + `VoteSession` (post-v0.2 ctor with `IReadOnlyList<string> configuredPlatforms`)
- `EnglishReceipts` for the receipt strings (new entries; see below)
- `VoteReceiptPolicy.Default` (announce-on-open, periodic tally at `max(7s, duration/5)`, announce-on-close)
- 0-indexed labels (`#0 / #1 / #2`)
- Multi-platform tally aggregation
- Run-id guards (`RunIdGuardEnabled` static, soft-check in `Prepare`)
- Two-flag suspend-and-resume Harmony pattern with `coordinator.Dispatcher.Post` for resume
- Cancellation receipt path — B.3 has its own `SendCancellationReceipt()` private helper modeled on `CardRewardVotePatch.cs:397-408`. Triggered when resume's liveness checks fail (run abandoned, game over, run-id drift). <!-- CHANGED v2: explicit own-cancellation-receipt helper — Reviewer 1 (and code-validated against CardRewardVotePatch.cs:397-408) -->
- `[Collection("TiLog.Sink")]` isolation for any test class that triggers `TiLog.*`

## Code changes

**New files:**
- `src/Game/DecisionVotes/BossVotePatch.cs` — Harmony prefix on `NTreasureRoom.OnProceedButtonPressed` + `HandleVoteAsync` + `ResumeOnMainThread` + `SendCancellationReceipt` + `ApplyBossSwap` delegate seam. Modelled on `CardRewardVotePatch.cs`.
- `src/Game/DecisionVotes/BossCandidateSampler.cs` — pure helper. `SampleDistinct<T>(IReadOnlyList<T> pool, int count, Random rng)`. Partial Fisher-Yates; no game references; compiles into the test project. Unit-tested. <!-- CHANGED v2: pure-helper extraction named — Reviewers 2, 5, 7 -->
- `src/Game/Ui/BossVotePopup.cs` — Godot `CanvasLayer`-rooted Control owning backdrop + N-column layout + tally labels. Subscribes to `session.Closed` and `session.Cancelled` for lifecycle (NOT `TallyChanged`); polls `session.Snapshot()` from `_Process` for live updates.
- `tests/Game/DecisionVotes/BossCandidateSamplerTests.cs` — pure-logic tests using seeded `Random`. Tests: sampling determinism, pool-size handling (3+, 2, 1, 0), no-duplicates, A10+ SecondBoss exclusion. Marked `[Collection("TiLog.Sink")]`. <!-- CHANGED v2: test file named after the helper not the patch — Reviewer 5 -->

**Modified files:**
- `src/Ti/Voting/EnglishReceipts.cs` — new receipt entries (boss vote open / close / cancellation). Either add `BossVoteOpened` / `BossVoteClosed` analogues, or reuse the generic decision-vote formatter if one already covers the shape — to be confirmed during implementation by reading the current `EnglishReceipts` surface.
- `src/ModEntry.cs` — register `BossVotePatch` with the Harmony instance during init (same pattern as the comment block around `ModEntry.cs:177` for B.2.1).
- `tests/slay_the_streamer_2.tests.csproj` — include the new test file paths; exclude `BossVotePatch.cs` from `Compile` via `<Compile Remove="..\src\Game\DecisionVotes\BossVotePatch.cs" />` (same pattern as B.1 / B.2.1 / B.2.2). `BossCandidateSampler.cs` and `BossVotePopup.cs` are NOT excluded — they don't reference `MegaCrit.Sts2.*` types beyond what's safe via interface. <!-- CHANGED v2: csproj remove-line is explicit — Reviewer 5 -->
- `notes/06-followups-and-deferred.md` — add B.3 acceptance-gate results section at end after operator validation.
- `README.md` — status section bump after B.3 ships.

**Code-size estimate:** 350–500 LOC across the patch + popup + helper + tests. Larger than the initial v1 estimate of ~300; the popup lifecycle, cancellation receipt helper, and BossCandidateSampler add real lines. Still smaller than B.2.1 (which had 5 patch targets + skip-budget infrastructure totaling ~600+ LOC); comparable to a single B.2.1-style decision-vote slice. <!-- CHANGED v2: estimate bumped per Reviewers 2, 6 -->

## Receipt strings

Match the voice / shape of existing slices. `{BossN}` resolves to `EncounterModel.Title.GetFormattedText()` (same plain-text rendering used in popup column labels). <!-- CHANGED v2: replaced Ancient names in examples with abstract Boss0/Boss1/Boss2; explicit Boss-name source — Reviewers 2, 8 -->

- **Open**: `"Act {n} boss vote opened — !vote #0 {Boss0}, #1 {Boss1}, #2 {Boss2} ({duration}s)"` (2-option variant drops `#2`).
- **Periodic tally**: reuses `VoteReceiptPolicy.Default` (adaptive cadence). Format: `"Boss tally — #0 {a}, #1 {b}, #2 {c} ({remaining}s left)"` — periodic-tally dedup compares the structural tally state, never the formatted text (per CLAUDE.md's Tier-1 gotcha + `VoteSession.cs:320-326`).
- **Close**: `"Chat picked #{winner} {WinnerBoss} ({votes} votes)"`.
- **Cancellation** (resume liveness check fails — run abandoned, IsGameOver, run-id drift): `SendCancellationReceipt()` private helper in `BossVotePatch.cs` modeled on `CardRewardVotePatch.cs:397-408`. Receipt text: `"Vote result ignored — run abandoned during boss vote"`.

## Edge cases & risks

- **Pool size ≤ 1** — release `_voteInProgress`, bail to vanilla. Logged at `Info`: `"[boss-vote] degenerate pool (count=N); skipping vote"`.
- **A10+ DoubleBoss duplicate-boss collision** — **Resolved by exclusion**: B.3 removes `SecondBossEncounter.Id` from the candidate pool before sampling when `runState.Act.HasSecondBoss` is true. From `ActModel.cs:287-291`, the second boss is rolled once at run start excluding the original primary; `MapCmd.SetBossEncounter` does NOT re-validate it on swap. Without our exclusion, chat could vote for a boss already scheduled as the second boss, and the streamer would fight the same encounter twice. Smoke C verifies primary ≠ second after the swap. <!-- CHANGED v2: full fix instead of "still holds" claim — Reviewers 2, 3, 5, 6; code-validated against ActModel.cs:287-291 -->
- **Re-entrancy on double-Proceed-click vs synthetic resume click** — Three-way prefix branch: `_resumeInProgress == 1` returns `true` (synthetic resume — let vanilla through); `_voteInProgress == 1` returns `false` (silently reject streamer double-click); neither set → acquire `_voteInProgress` atomically and start vote. Resume clears both flags in `finally` with `_resumeInProgress` cleared first, then `_voteInProgress`. <!-- CHANGED v2: TBD resolved — Reviewers 1-8 -->
- **Run-id drift / abandonment / IsGameOver during the vote** — covered by the existing run-id-guard pattern reused from `CardRewardVotePatch.cs:311-346` and `AncientVotePatch.cs:200-236`. `SendCancellationReceipt()` fires; resume does not call `ApplyBossSwap`; both flags cleared in `finally`.
- **First-defeat achievement key** — Smoke G verifies empirically. No code-side compensation per Non-goals.
- **`MapCmd.SetBossEncounter` in `TestMode.IsOn`** — vanilla's body branches on `TestMode.IsOff` for UI refreshes. In test mode the boss is swapped but the UI doesn't refresh. We don't run in test mode at runtime; flagged as informational only.
- **Asset load failure** — `BossNodePath + ".png"` (or `BossNodePath` directly if it already ends in `.png`) is loaded as `Texture2D`. The popup defensively checks `string.IsNullOrEmpty(BossNodePath)` first and wraps the `ResourceLoader.Load<Texture2D>` call in try/catch; on null path / load throw / null result, `TextureRect.Texture` stays null (empty box). Vote still works; only visual fidelity degrades. Acceptable for v1. <!-- APPLIED v2.1: enhancement #5 wording updated to reflect defensive load — Reviewer 3 -->
- **Force-close (Alt+F4) mid-vote** — `CanvasLayer` is in-memory and dies with the process; no `MapCmd.SetBossEncounter` ever fires; vanilla's saved game retains the original boss. Non-issue. <!-- CHANGED v2: force-close acknowledgment added — Reviewer 8 -->
- **Save-load mid-vote** — vote and popup are ephemeral process state; not persisted. On reload, the streamer is back in the chest room with the original boss. Vote did not apply. Sampling RNG seed is derived from `(runState.Rng.StringSeed, CurrentActIndex)` so when the streamer re-clicks Proceed after reload, **the same 3 candidates appear** — consistent UX across save-load cycles. <!-- CHANGED v2: save-load behavior explicit — Reviewers 5, 6, 7 -->
- **Pause mid-vote** — `CanvasLayer.ProcessMode = Always` keeps `_Process` running so the timer label continues updating. Vote timer is real-time (`VoteSession._clock`), not game-time — this matches the chat's perception of remaining time. <!-- CHANGED v2: pause behavior explicit — Reviewer 8 -->
- **TiLog test isolation** — any test class that touches `BossVotePatch` paths which call `TiLog.*` MUST be marked `[Collection("TiLog.Sink")]` per CLAUDE.md. Mandatory for any `VoteSession`-adjacent tests.
- **Periodic-tally dedup** — comparing tally STATE, not rendered text, per CLAUDE.md Tier-1 gotcha + `VoteSession.cs:320-326`. No new code needed; reusing the existing policy correctly.

## Testing strategy

### Unit tests

**`tests/Game/DecisionVotes/BossCandidateSamplerTests.cs`** — pure-logic tests using seeded `Random` (no `VoteSession` triad needed for sampling itself). Marked `[Collection("TiLog.Sink")]`.

Coverage targets:

1. **Sampling determinism** — same seed → same N candidates from the same pool.
2. **No duplicates** — sampled candidates are all distinct.
3. **Pool size 3+** → exactly 3 sampled.
4. **Pool size 2** → exactly 2 sampled (degenerate-but-valid path).
5. **Pool size 1** → 1 sampled (caller will treat as bail-to-vanilla).
6. **Pool size 0** → 0 sampled.
7. **A10+ SecondBoss exclusion** — when caller passes a pre-filtered pool excluding `SecondBossEncounter.Id`, the sample never contains that ID. (Implemented in the patch's calling code; the sampler itself is type-agnostic.)

The Harmony patch file itself (`BossVotePatch.cs`) is excluded from `Compile` in the test csproj (same constraint that applied to B.1, B.2.1, B.2.2). The pure-logic pieces — sampling, guards, label construction — live in helper classes that ARE compiled into the test project. The `ApplyBossSwap` delegate seam lets tests verify the index→encounter mapping without invoking the real `MapCmd.SetBossEncounter`.

### Operator-validation gate

Manual smoke tests required before tagging `plan-b-3-complete`:

1. **Smoke A — Act 1 happy path**: standard run, exit Act 1 chest, vote popup appears, 3 portraits render, 30s timer counts down (even if streamer pauses), chat votes via `!vote #N`, popup closes, top-bar boss icon updates to the chosen boss, walk to Act 1 boss → expected fight starts. **Visual check**: no orphaned `CanvasLayer` after the chest→map transition. <!-- CHANGED v2: pause-during-vote check + orphan check — Reviewers 2, 5, 8 -->
2. **Smoke B — Act 2 + Act 3 coverage**: similar smoke on Acts 2 and 3 (non-DoubleBoss).
3. **Smoke C — A10+ DoubleBoss**: A10 run, Act 3 chest. **Verification**: confirm `runState.Act.SecondBossEncounter.Id` is excluded from the 3 candidates shown in the popup. Vote on primary boss. After the swap, **assert primary ≠ second**. Confirm both bosses fight in sequence with no duplication. <!-- CHANGED v2: explicit duplicate-check assertion — Reviewers 2, 3, 5, 6 -->
4. **Smoke D — run abandoned mid-vote**: open chest-room vote, abandon run → cancellation receipt fires in chat (`"Vote result ignored — run abandoned during boss vote"`), **no orphaned `CanvasLayer` left in the scene tree** (inspect `GetTree().Root.GetChildren()` or `godot.log` for leftover `BossVotePopup` instances).
5. **Smoke E — chat disabled**: disable chat in settings (`Voter.Default == null` or `Chat.State` not readable) → vote does not open, Proceed click works vanilla.
6. **Smoke F — multiplayer bail**: 2-player run, exit chest → vote does not open, Proceed click works vanilla.
7. **Smoke G — first-defeat achievement check**: in Standard Mode with a not-yet-defeated boss, vote it in, defeat it → achievement registers (verify in Steam achievement progress or in-game tracking).
8. **Smoke H — save & reload mid-vote**: open chest-room vote, note the 3 candidates shown in the popup, Save & Quit. Reload. **Verify**: chest room reappears in pre-Proceed state (no boss swap applied); re-click Proceed; **the same 3 candidates appear** (confirms the `HashCode.Combine(StringSeed, CurrentActIndex)` seeded RNG gives save-load determinism). Vote proceeds normally. <!-- APPLIED v2.1: enhancement #3 — Reviewer 1 -->

Pool-size verification spike (item 1 below) can be run as part of Smoke A.

Results recorded in [`notes/06-followups-and-deferred.md`](../../../notes/06-followups-and-deferred.md) matching the B.1 / B.2.1 / yt-chat pattern.

### Log points

Expected `[SlayTheStreamer2][boss-vote]` log lines for grep-based verification during operator validation: <!-- CHANGED v2: explicit log-point enumeration — Reviewer 8 -->

- `[boss-vote] target resolved: NTreasureRoom.OnProceedButtonPressed` (Prepare, once)
- `[boss-vote] degenerate pool (count=N); skipping vote` (pool ≤ 1 path)
- `[boss-vote] only N bosses available for Act X — possible content change?` (warn, pool < 3 at runtime — future-proofing against MegaCrit content updates) <!-- APPLIED v2.1: enhancement #4 log line — Reviewer 4 -->
- `[boss-vote] multiplayer detected ...` (one-shot warn, then debug)
- `[boss-vote] chat not readable (state=X); bailing to vanilla` (debug)
- `[boss-vote] opening vote for N options` (info)
- `[boss-vote] sampled candidates: #0=BossA(id), #1=BossB(id), #2=BossC(id)` (info — for cross-platform debugging of pool issues)
- `[boss-vote] resume: applying winner #N on main thread` (info)
- `[boss-vote] resume aborted: <reason>` (warn — run-abandon, IsGameOver, run-id drift, room invalid)
- `[boss-vote] cancellation receipt queued` (info)

## Pre-implementation verifications (spike items)

To be resolved during the first task of the plan (similar to B.2.1's Task 1 spike):

1. **`ActModel.AllBossEncounters` returns ≥ 3 per vanilla act** — read the decompile, confirm each act's `GenerateAllEncounters` produces at least 3 `RoomType.Boss` entries.
2. **`ActModel.AllBossEncounters` is NOT unlock-filtered** — per feasibility doc claim; verify against `decompiled/sts2/MegaCrit/sts2/Core/Models/ActModel.cs`.
3. **`ActModel.SecondBossEncounter` and `HasSecondBoss` are accessible at the prefix point** — already validated against `ActModel.cs:160-164` but confirm at runtime by logging during Smoke C.
4. **`NTreasureRoom.OnProceedButtonPressed` is parameterless** — confirm method signature; verify the synthetic re-call shape `treasureRoom.OnProceedButtonPressed()` compiles and dispatches correctly. <!-- CHANGED v2: signature verification spike — Reviewer 5 -->
5. **`NTreasureRoom` field surface for `Prepare`** — identify the right field to reflect (likely `_proceedButton` or equivalent) so the `Prepare` soft-check can verify vanilla shape. <!-- CHANGED v2: Prepare surface enumeration — Reviewers 1, 8 -->
6. **`runState.CurrentActIndex` semantics** — 0-based or 1-based? Existing `CardRewardVotePatch` and `AncientVotePatch` don't use it directly; verify before the title label shows wrong act number. <!-- CHANGED v2: 1-based vs 0-based spike — Reviewer 8 -->
7. **Boss-portrait asset paths** — confirm whether `BossNodePath` already ends in `.png` (in which case naive concat breaks) or not (in which case `+ ".png"` works). Use `MapNodeAssetPaths.PngPath` if such a property exists. <!-- CHANGED v2: extension-handling spike — Reviewer 8 -->
8. **`EncounterModel.Title.GetFormattedText()` returns plain text or BBCode?** — if BBCode, `RichTextLabel.BbcodeEnabled = true` (already specified) renders it correctly. If plain text, no harm done. Verify which during the spike. <!-- CHANGED v2: BBCode spike — Reviewer 8 -->
9. **`CanvasLayer.Layer = 100` collision check** — grep decompile for vanilla `CanvasLayer` usage; verify 100 is uncontested. If any vanilla layer is ≥ 100, raise `BossVotePopup.LAYER_INDEX` accordingly. <!-- CHANGED v2: layer collision spike — Reviewer 7 -->

## Acceptance gate

- `dotnet test` is green (including new `BossCandidateSamplerTests.cs`).
- All Smoke A–H operator-validation steps pass on a fresh `./build.ps1` + `./install.ps1`. <!-- APPLIED v2.1: smoke range updated for enhancement #3 — Reviewer 1 -->
- Log inspection: enumerated `[SlayTheStreamer2][boss-vote]` lines appear for each tested smoke; no exceptions on the boss-swap path.
- Runtime startup hash matches `git log -1 --format=%H` post-merge (per CLAUDE.md's "stale dist" gotcha).
- Acceptance results documented in [`notes/06-followups-and-deferred.md`](../../../notes/06-followups-and-deferred.md).
- Tag `plan-b-3-complete` once green.

## Justification: deferred extraction of suspend-and-resume base class

With B.3, n=3 patches share the suspend-and-resume shape (`AncientVotePatch`, `CardRewardVotePatch`, `BossVotePatch`). Rule of Three has technically fired. **Extraction is deferred to a post-B.3 refactor slice** because B.3's resume path is structurally different: it calls `ApplyBossSwap(runState, encounter)` *first*, then synthetically re-clicks Proceed. The other two patches just re-call the original method with the chat-chosen argument. A shared base class would need a "pre-resume action" hook, complicating the abstraction. Better to ship n=3 in their current shape and extract with all three visible. <!-- CHANGED v2: deferred-extraction justification — Reviewers 1, 6, 7 -->

## Cross-references

- [`notes/10-boss-vote-feasibility.md`](../../../notes/10-boss-vote-feasibility.md) — feasibility findings + the 8 open questions resolved during this brainstorm.
- [`notes/06-followups-and-deferred.md`](../../../notes/06-followups-and-deferred.md) — running follow-ups; acceptance-gate results land here.
- [`notes/09-settings-and-tunable-knobs.md`](../../../notes/09-settings-and-tunable-knobs.md) — `voteOnBoss` toggle (planned, not in v1).
- [`docs/superpowers/specs/2026-05-10-plan-b-2-1-card-reward-vote-design-v4.md`](2026-05-10-plan-b-2-1-card-reward-vote-design-v4.md) — architectural twin: suspend-and-resume Harmony pattern, test factoring, ModSettings integration.
- [`docs/superpowers/specs/2026-05-13-plan-b-2-2-ancient-vote-design.md`](2026-05-13-plan-b-2-2-ancient-vote-design.md) — sibling slice (Ancients), implementation-complete on `main` at time of writing (operator-validation pending); B.3 does not depend on B.2.2's tag.
- [`docs/superpowers/specs/META-REVIEW-2026-05-13-plan-b-3-boss-vote-design.md`](META-REVIEW-2026-05-13-plan-b-3-boss-vote-design.md) — synthesized 8-reviewer meta-review that produced this v2.
- [`MapCmd.SetBossEncounter`](../../../decompiled/sts2/MegaCrit/sts2/Core/Commands/MapCmd.cs) — the entire boss-swap API.
- [`ActModel.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/Models/ActModel.cs) — boss encounter generation, second-boss handling (`SecondBossEncounter`, `HasSecondBoss`, `SetSecondBossEncounter`), `AllBossEncounters`.
- [`EncounterModel.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/Models/EncounterModel.cs) — `MapNodeAssetPaths`, `BossNodePath`, `Title`.
- [`TreasureRoom.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/Rooms/TreasureRoom.cs) + [`NTreasureRoom.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Rooms/NTreasureRoom.cs) — trigger surface.
- [`CardRewardVotePatch.cs`](../../../src/Game/DecisionVotes/CardRewardVotePatch.cs) — B.3's primary architectural twin (the two-flag pattern, the cancellation-receipt helper, the run-id guard, the try/catch fallback shape).
- [`AncientVotePatch.cs`](../../../src/Game/DecisionVotes/AncientVotePatch.cs) — B.2.2's predicate-widened patch sharing the same shape.
- [`VoteTallyLabel.cs`](../../../src/Ti/Ui/VoteTallyLabel.cs) — the `_Process`-polling lifecycle pattern that `BossVotePopup` mirrors.
- Original Tempus mod screenshot: visual reference for the three-boss popup layout, linked from [`notes/10`](../../../notes/10-boss-vote-feasibility.md).

---

## Optional Enhancements

The meta-review identified these as Consider-tier. **Items 3, 4, 5 were selected and have been applied inline (see `<!-- APPLIED v2.1: ... -->` markers).** The remainder are recorded as deliberate not-applied:

| # | Description | Reviewer | Decision |
|---|---|---|---|
| 1 | Pre-vote 2-3 second countdown for lagged-Twitch viewers | R1 | Not applied (lean-no; better as v0.2 polish) |
| 2 | Spike gatesheet output format | R7 | Not applied (neutral; current prose pattern in notes/06 stands) |
| 3 | **Smoke H — save & reload mid-vote** | R1 | ✅ Applied — see Operator-validation gate Smoke H |
| 4 | **Runtime pool-size warnings (`Warn` when pool < 3)** | R4 | ✅ Applied — see Candidate sampling + log points |
| 5 | **Defensive `BossNodePath` null/empty + try/catch on texture load** | R3 | ✅ Applied — see Popup architecture Children + Edge cases |
| 6 | `CanvasLayer.Layer` deeper collision investigation | R7 | Not applied (named-const + spike item already sufficient) |
| 7 | Streamer dismissal mechanism for the modal | R6 | Not applied (conflicts with chat-vs-streamer asymmetry) |
| 8 | Achievement-preserving swap via reflection | R4 | Not applied (speculative; Smoke G sufficient) |
| 9 | Cosmetic Tween fade-out before QueueFree | R4 | Not applied (same race concern that killed fade-in) |
| 10 | Timer label update throttling to 250ms | R8 | Not applied (over-engineering; VoteTallyLabel pattern is fine) |
