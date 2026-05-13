# 2026-05-13 — Plan B.3: Boss Vote (design)

**Date**: 2026-05-13
**Status**: Design approved 2026-05-13. Implementation pending.
**Slice**: B.3 (chat-picks-the-next-act-boss vote) — fifth voting slice after B.1 (Neow), B.2.1 (card reward), v0.2 yt-chat, and B.2.2 (Ancients, implementation-complete on `main` at time of writing — operator-validation gate pending, not yet tagged complete; B.3 does not depend on B.2.2's tag).
**Scope**: Add a chest-room-exit vote where chat picks the next act's boss from 3 randomly sampled candidates. Triggered on `NTreasureRoom.OnProceedButtonPressed` via suspend-and-resume; resolved via `MapCmd.SetBossEncounter`. New Godot `CanvasLayer`-rooted popup with 3 portrait columns and per-column tally labels.

## TL;DR

Feasibility was settled in [`notes/10-boss-vote-feasibility.md`](../../../notes/10-boss-vote-feasibility.md). Vanilla exposes `MapCmd.SetBossEncounter(IRunState, EncounterModel)` — a public static one-liner that swaps the act's boss, refreshes the top-bar boss icon, and re-renders the map preserving streamer drawings. We hook `NTreasureRoom.OnProceedButtonPressed` with a Harmony prefix using the existing B.1 / B.2.1 suspend-and-resume pattern: prefix returns `false`, kicks off a 30s vote async, resumes the Proceed click on the main thread after `MapCmd.SetBossEncounter` is applied. The popup is a self-owned `CanvasLayer` + semi-transparent `ColorRect` backdrop + 3-column Control (PNG portrait + boss title + tally label per column), with no interaction with vanilla's overlay-stack. Vote fires on every act (1, 2, 3). On A10+ Ascension's `DoubleBoss` runs, only the primary Act 3 boss is voted on; vanilla picks the second from the remainder. Net change: ~1 new patch class, ~1 new Control class, modest receipt additions, no new settings knobs.

## Goals

- Chat votes on the upcoming act boss every time the streamer exits a chest room. Three random candidates from `runState.Act.AllBossEncounters`, 30s vote window, latest-wins semantics, 0-indexed labels (`#0 / #1 / #2`).
- Visual popup overlays the chest scene (matches the original Tempus mod's screenshot reference) with three portrait columns showing each candidate's boss icon, title, and live tally bar.
- Winner is applied via `MapCmd.SetBossEncounter` before the streamer transitions to the map screen, so the new boss is in place when the boss room is later entered.
- Reuse B.1 / B.2.1 infrastructure verbatim: `VoteCoordinator`, `VoteSession`, suspend-and-resume Harmony pattern, `EnglishReceipts`, periodic-tally cadence, multi-platform aggregation, run-id guards, cancellation handling.
- Singleplayer-only v1 — bail to vanilla in multiplayer (matches existing slices).

## Non-goals

- **Per-vote settings toggle (`voteOnBoss: bool`)** — not wired in v1. Matches the current state of Neow and Card Reward votes (also no toggle yet per [`notes/09`](../../../notes/09-settings-and-tunable-knobs.md)). Will be addressed in a future cross-cutting "B.2 surface toggles" slice rather than per-slice.
- **Per-decision vote-duration override** — 30s hardcoded to match Neow / Card Reward. Surfinite has explicitly noted that durations will eventually become globally-configurable (one knob, not per-vote); deferred to a future settings slice.
- **Silhouette / spoiler-safe mode** (`showBossNames: false`) — target audience is unlocked-everything streamers per [`notes/10`](../../../notes/10-boss-vote-feasibility.md); deferred to future polish.
- **A10+ DoubleBoss second-boss handling** — vote on primary only; vanilla picks the second boss from the remaining pool. Pair-vote and chained-vote variants documented as future polish.
- **Multiplayer sync** — `MapCmd.SetBossEncounter` mutates `runState.Act` locally and has no network propagation in vanilla. We bail to vanilla in multiplayer; a sync message becomes a separate slice if/when multiplayer support is added across all votes.
- **Spine-animated boss portraits** — PNG fallback only (`BossNodePath + ".png"` / `_outline.png`). Spine animation is polish.
- **"Keep current boss" explicit option** — sample is 3 fresh from the full pool, no current-boss preservation logic.
- **Achievement-gate verification** — `ActModel.DefeatedAllEnemiesAchievement` looks per-act not per-encounter, so chat-swapping the boss should not break first-defeat achievements. Worth confirming during operator validation but not engineering around.

## Architecture

### Trigger surface

Harmony **prefix** on `NTreasureRoom.OnProceedButtonPressed` (Option B in the feasibility doc). Prefix returns `false` to suspend the original click, then resumes it on the main thread after the vote closes via `dispatcher.Post`.

Activation guards in the prefix, all checked before any side effect:

1. `_voteInProgress` re-entrancy flag is false (rejects double-click).
2. Multiplayer bail: `runState.Players.Count <= 1`.
3. Chat is readable: `chatService.State` is `ConnectedReadOnly` or `ConnectedReadWrite`.
4. `runState.Act.AllBossEncounters.Count() >= 2` (pool sanity).
5. Run-id guard (same `RunIdGuardEnabled` static / `Prepare` soft-check pattern as B.1 / B.2.1).

If any guard fails, prefix returns `true` (let vanilla handle the click).

### Vote flow

```
Streamer clicks Proceed in chest room
    │
    ▼
BossVotePatch.Prefix                              ─ main thread
    │  guard checks
    │  sample 3 distinct EncounterModels from runState.Act.AllBossEncounters
    │  build option labels ("#0 Pael", "#1 Tezcatara", ...)
    │  session = coordinator.Start("Act N boss vote", labels, 30s)
    │  popup = new BossVotePopup(options, session)  ── instantiates CanvasLayer, etc.
    │  popup.Show()
    │  _voteInProgress = true
    │  _ = HandleVoteAsync(__instance, session, options)        ── fire-and-forget
    │  return false                                              ── suspend original click
    │
    ▼
HandleVoteAsync                                   ─ background
    │  winnerIndex = await session.AwaitWinnerAsync()
    │
    ▼
dispatcher.Post(...)                              ─ resumes on main thread
    │  liveness checks (run-id drift, IsAbandoned, IsGameOver)
    │  MapCmd.SetBossEncounter(runState, options[winnerIndex])
    │     → ActModel._rooms.Boss mutated
    │     → top-bar boss icon refreshed
    │     → map re-rendered (clearDrawings: false)
    │  _voteInProgress = false
    │  ResumeProceedClick(treasureRoomNode)        ── synthetic re-call of OnProceedButtonPressed
    │  BossVotePopup is freed via session.Closed event subscription
    │
    ▼
Vanilla chest → map → boss room with swapped boss
```

### Candidate sampling

```csharp
var allBosses = runState.Act.AllBossEncounters.ToList();
if (allBosses.Count < 2) return /* bail to vanilla */;
var sample = allBosses.OrderBy(_ => rng.Next()).Take(3).ToList();
```

- No special handling for current boss. The sample may or may not include it; if chat votes for the current boss, `SetBossEncounter(currentBoss)` is effectively a no-op (idempotent except for redundant icon refresh).
- If the pool has only 2 entries, the vote runs with 2 options. If 0 or 1, bail to vanilla.
- Sampling RNG: a process-local `Random` instance (not `runState.Rng`, which is the run-deterministic generator and shouldn't be polluted by mod-side draws).

### Boss-swap API

```csharp
MapCmd.SetBossEncounter(runState, winnerEncounter);
```

Single call. Vanilla handles:
- `runState.Act._rooms.Boss = winnerEncounter`
- `NRun.Instance.GlobalUi.TopBar.BossIcon.RefreshBossIcon()` (top-bar)
- `NMapScreen.Instance?.SetMap(runState.Map, runState.Rng.Seed, clearDrawings: false)` (map re-render preserving streamer drawings)

No additional vanilla state writes from our patch.

### Popup architecture

**Root**: a new `CanvasLayer` at layer index 100, parented to `SceneTree.Root`. Owned by `BossVotePopup`. Frees itself on `session.Closed`.

**Children**:
- Backdrop: full-screen `ColorRect` with `Color(0, 0, 0, 0.6)`. Optional `Tween` modulate fade-in over ~150ms.
- Content `VBoxContainer`:
  - Title `Label`: `"ACT {n} BOSS VOTE"` (uses `runState.CurrentActIndex + 1` for the 1-based act number visible to viewers).
  - Timer `Label`: shows seconds remaining, updated from `_Process` reading `session.SecondsRemaining` (or equivalent — TBD on `VoteSession` API inspection during planning).
  - `HBoxContainer` with 3 column `VBoxContainer`s (or 2 if the pool is degenerate). Each column:
    - `TextureRect` loading `BossNodePath + ".png"` (filled icon). Optional second `TextureRect` for `_outline.png` overlay if visually warranted; otherwise just the filled icon.
    - `Label` with `#{i} {EncounterModel.Title.GetFormattedText()}`.
    - Tally `Label` updated on `session.TallyChanged` — bar made of `▮` characters + count, matching the existing chat-tally aesthetic.

**Layout**: three columns ~30% width with 5% gutters, normalized to whatever StS2's screen reference resolution is. Pixel-level dimensions left to the implementation phase per the feasibility doc's design-pass note.

**No interaction with vanilla's `NOverlayStack`** — we own everything we created.

### Reused verbatim (no change)

- `VoteCoordinator` + `VoteSession` (post-v0.2 ctor with `IReadOnlyList<string> configuredPlatforms`)
- `EnglishReceipts` for the receipt strings (new entries; see below)
- `VoteReceiptPolicy.Default` (announce-on-open, periodic tally at `max(7s, duration/5)`, announce-on-close)
- 0-indexed labels (`#0 / #1 / #2`)
- Multi-platform tally aggregation
- Run-id guards (`RunIdGuardEnabled` static, soft-check in `Prepare`)
- Suspend-and-resume Harmony pattern with `dispatcher.Post` for resume
- Cancellation receipt path (verified working in B.2.1)
- `[Collection("TiLog.Sink")]` isolation for any test class that triggers `TiLog.*`

## Code changes

**New files:**
- `src/Game/DecisionVotes/BossVotePatch.cs` — Harmony prefix on `NTreasureRoom.OnProceedButtonPressed` + `HandleVoteAsync` + resume on main thread. Modelled on `NeowBlessingVotePatch.cs` / `CardRewardVotePatch.cs`.
- `src/Game/Ui/BossVotePopup.cs` — Godot `CanvasLayer`-rooted Control owning backdrop + 3-column layout + tally labels. Subscribes to `session.TallyChanged` and `session.Closed`.
- `tests/Game/DecisionVotes/BossVotePatchTests.cs` — pure-logic tests using `VoteSessionTestBase` (FakeClock + FakeTimerScheduler + ImmediateDispatcher + seeded `Random`). Marked `[Collection("TiLog.Sink")]`.

**Modified files:**
- `src/Ti/Voting/EnglishReceipts.cs` — new receipt entries (boss vote open / close / cancellation). Either add `BossVoteOpened` / `BossVoteClosed` analogues, or reuse the generic decision-vote formatter if one already covers the shape — to be confirmed during implementation by reading the current `EnglishReceipts` surface.
- `src/ModEntry.cs` — register `BossVotePatch` with the Harmony instance during init (same pattern as the comment block around `ModEntry.cs:177` for B.2.1).
- `tests/slay_the_streamer_2.tests.csproj` — include the new test file path; exclude `BossVotePatch.cs` from `Compile` (it references `MegaCrit.Sts2.*` types the test project doesn't link, same pattern as B.1 / B.2.1 / B.2.2).
- `notes/06-followups-and-deferred.md` — add B.3 acceptance-gate results section at end after operator validation.
- `README.md` — status section bump after B.3 ships.

**Code-size estimate:** ~300 LOC across the patch + popup + tests. Smaller than B.2.1 (which had 5 patch targets and skip-budget infrastructure); larger than B.2.2 (which is ~30 LOC of predicate widening on an existing patch).

## Receipt strings

Match the voice / shape of existing slices:

- **Open**: `"Act {n} boss vote opened — !vote #0 {Boss0}, #1 {Boss1}, #2 {Boss2} ({duration}s)"` (or 2-option variant if pool degenerate).
- **Periodic tally**: reuses `VoteReceiptPolicy.Default` (adaptive cadence). Format: `"Boss tally — #0 {a}, #1 {b}, #2 {c} ({remaining}s left)"` — periodic-tally dedup compares the structural tally state, never the formatted text (per CLAUDE.md's Tier-1 gotcha).
- **Close**: `"Chat picked #{winner} {WinnerBoss} ({votes} votes)"`.
- **Cancellation** (run abandoned mid-vote): reuses the existing cancellation receipt path verbatim from B.2.1; no new string needed.

## Edge cases & risks

- **Pool size < 3** — sample whatever the pool contains; if `< 2`, bail to vanilla. Worth a spike during planning to confirm vanilla acts always have `≥ 3` boss encounters (per feasibility doc Open Q 7).
- **Pool size < 3 due to unlock-state filtering** — `ActModel.AllBossEncounters` is documented (per feasibility doc) as NOT being unlock-filtered (unlike `GetUnlockedAncients`). Worth verifying during the planning spike. If it is unlock-filtered, document and rely on the unlocked-everything-streamer assumption.
- **Re-entrancy on double-Proceed-click** — static `_voteInProgress` flag set in the prefix, cleared on resume. Second prefix call while flag is true returns immediately and bails to vanilla (or re-suspends silently — TBD on whichever is safer; same trade-off as B.2.1's `_resumeInProgress`).
- **A10+ DoubleBoss second-boss handling** — vote only swaps the primary. Vanilla's existing second-boss roll happens at run start (`RunManager.cs:499-502`), independent of our vote. The second boss is picked from `AllBossEncounters` excluding the primary; after our swap, the second boss may or may not be one of our 3 candidates, which is fine — vanilla's exclusion logic still holds.
- **First-defeat achievement key** — `ActModel.DefeatedAllEnemiesAchievement` appears to key on the act, not the encounter, so chat-swapping the boss shouldn't break first-defeat achievements. Verify during operator validation (defeat a chat-picked boss in Standard Mode; check achievement registers).
- **Run-id drift during the vote** — covered by the existing run-id-guard pattern reused from B.1 / B.2.1. If the run ends or transitions mid-vote, `ResumeOnMainThread` bails before calling `MapCmd.SetBossEncounter`.
- **`MapCmd.SetBossEncounter` in `TestMode.IsOn`** — vanilla's body branches on `TestMode.IsOff` for the UI refresh side effects. In test mode the boss is swapped but the UI doesn't refresh. We don't run in test mode at runtime; flagged as informational only.
- **Asset load failure** — `BossNodePath + ".png"` is loaded as a `Texture2D`. If the resource is missing (modded encounter without standard assets, future MegaCrit change), the `TextureRect` shows an empty box. Vote still works; only visual fidelity degrades. Acceptable for v1.
- **TiLog test isolation** — any test class that touches `BossVotePatch` paths which call `TiLog.*` MUST be marked `[Collection("TiLog.Sink")]` per CLAUDE.md. Mandatory for any `VoteSession`-adjacent tests.
- **Periodic-tally dedup** — must compare tally STATE, not rendered text, per CLAUDE.md Tier-1 gotcha. The existing `VoteReceiptPolicy` already does this correctly; ensure no regressions when adding boss-vote receipt formatters.

## Testing strategy

### Unit tests (`tests/Game/DecisionVotes/BossVotePatchTests.cs`)

Built on `VoteSessionTestBase` per CLAUDE.md (FakeClock + FakeTimerScheduler + ImmediateDispatcher + seeded `Random`). Marked `[Collection("TiLog.Sink")]`.

Coverage targets:

1. **Sampling determinism** — same seed → same 3 candidates from the same pool.
2. **Pool size handling** — pool of 5 → 3 sampled, pool of 2 → 2-option vote, pool of 1 → vote does not open, pool of 0 → vote does not open.
3. **Vote winner → boss swap** — winning index N triggers a call (mocked or via test seam) with `options[N]` as the new boss.
4. **Re-entrancy guard** — second invocation while `_voteInProgress` is true does not open a second vote.
5. **Multiplayer bail** — `players.Count > 1` → vote does not open.
6. **Chat-state bail** — `Disconnected` / `AuthenticationFailed` / `JoinFailed` → vote does not open.
7. **Cancellation path** — run-id drift mid-vote → no `SetBossEncounter` call on resume; popup is freed.

The patch will need a thin abstraction over `MapCmd.SetBossEncounter` (an `IBossSwapper` interface or equivalent test seam) to make assertion 3 possible — same pattern B.2.1 used for `NRewardsScreen` mutations. Implementation detail; finalize during planning.

The Harmony patch file itself (`BossVotePatch.cs`) is excluded from `Compile` in the test csproj (same constraint that applied to B.1, B.2.1, B.2.2). The pure-logic pieces — sampling, guards, label construction — live in helper classes that ARE compiled into the test project. This is the same factoring B.2.1 used for `SkipBudgetTracker`.

### Operator-validation gate

Manual smoke tests required before tagging `plan-b-3-complete`:

1. **Smoke A — Act 1 happy path**: standard run, exit Act 1 chest, vote popup appears, 3 portraits render, 30s timer counts down, chat votes via `!vote #N`, popup closes, top-bar boss icon updates to the chosen boss, walk to Act 1 boss → expected fight starts.
2. **Smoke B — Act 2 + Act 3 coverage**: similar smoke on Acts 2 and 3.
3. **Smoke C — A10+ Act 3 DoubleBoss**: A10 run, Act 3 chest, vote on primary boss, confirm both bosses fight in sequence (vanilla picks the second from the remainder).
4. **Smoke D — run abandoned mid-vote**: open chest-room vote, abandon run → cancellation receipt fires in chat, no orphaned `CanvasLayer` left in the scene tree.
5. **Smoke E — chat disabled**: disable chat in settings → vote does not open, Proceed click works vanilla.
6. **Smoke F — multiplayer bail**: 2-player run, exit chest → vote does not open, Proceed click works vanilla.
7. **Smoke G — first-defeat achievement check**: in Standard Mode with a not-yet-defeated boss, vote it in, defeat it → achievement registers.

Pool-size verification spike (item 1 below) can be run as part of Smoke A.

Results recorded in [`notes/06-followups-and-deferred.md`](../../../notes/06-followups-and-deferred.md) matching the B.1 / B.2.1 / yt-chat pattern.

## Pre-implementation verifications (spike items)

To be resolved during the first task of the plan (similar to B.2.1's Task 1 spike):

1. **`ActModel.AllBossEncounters` returns ≥ 3 per vanilla act** — read the decompile, confirm each act's `GenerateAllEncounters` produces at least 3 `RoomType.Boss` entries.
2. **`ActModel.AllBossEncounters` is NOT unlock-filtered** — per feasibility doc claim; verify against `decompiled/sts2/MegaCrit/sts2/Core/Models/ActModel.cs`.
3. **`MapCmd.SetBossEncounter` is the only API needed** — confirm no other vanilla call sites need to be invoked after the boss swap (e.g., `BossDiscoveryOrder` updates, achievement tracking initialization).
4. **`NTreasureRoom.OnProceedButtonPressed` resume mechanism** — confirm a synthetic re-call from `dispatcher.Post` works the same way the B.2.1 Proceed resume works, and that the re-call doesn't re-fire our prefix infinitely (the `_voteInProgress` flag must be cleared in the right order).
5. **Boss-portrait asset paths** — confirm `BossNodePath + ".png"` resolves for all bosses in vanilla acts (sanity-check during the spike; missing assets fall back to empty `TextureRect` gracefully).

## Acceptance gate

- `dotnet test` is green (including new `BossVotePatchTests.cs`).
- All Smoke A–G operator-validation steps pass on a fresh `./build.ps1` + `./install.ps1`.
- Log inspection: `[SlayTheStreamer2][boss-vote]` lines appear for each tested smoke; no exceptions on the boss-swap path.
- Runtime startup hash matches `git log -1 --format=%H` post-merge (per CLAUDE.md's "stale dist" gotcha — version in `godot.log` is git HEAD at build time, not install time).
- Acceptance results documented in [`notes/06-followups-and-deferred.md`](../../../notes/06-followups-and-deferred.md).
- Tag `plan-b-3-complete` once green.

## Cross-references

- [`notes/10-boss-vote-feasibility.md`](../../../notes/10-boss-vote-feasibility.md) — feasibility findings + the 8 open questions resolved during this brainstorm.
- [`notes/06-followups-and-deferred.md`](../../../notes/06-followups-and-deferred.md) — running follow-ups; acceptance-gate results land here.
- [`notes/09-settings-and-tunable-knobs.md`](../../../notes/09-settings-and-tunable-knobs.md) — `voteOnBoss` toggle (planned, not in v1).
- [`docs/superpowers/specs/2026-05-10-plan-b-2-1-card-reward-vote-design-v4.md`](2026-05-10-plan-b-2-1-card-reward-vote-design-v4.md) — architectural twin: suspend-and-resume Harmony pattern, test factoring, ModSettings integration.
- [`docs/superpowers/specs/2026-05-13-plan-b-2-2-ancient-vote-design.md`](2026-05-13-plan-b-2-2-ancient-vote-design.md) — sibling slice (Ancients), implementation-complete on `main` at time of writing (operator-validation pending); B.3 does not depend on B.2.2's tag.
- [`MapCmd.SetBossEncounter`](../../../decompiled/sts2/MegaCrit/sts2/Core/Commands/MapCmd.cs) — the entire boss-swap API.
- [`ActModel.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/Models/ActModel.cs) — boss encounter generation, second-boss handling, `AllBossEncounters`.
- [`EncounterModel.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/Models/EncounterModel.cs) — `MapNodeAssetPaths`, `BossNodePath`, `Title`.
- [`TreasureRoom.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/Rooms/TreasureRoom.cs) + [`NTreasureRoom.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Rooms/NTreasureRoom.cs) — trigger surface.
- Original Tempus mod screenshot: visual reference for the three-boss popup layout, linked from [`notes/10`](../../../notes/10-boss-vote-feasibility.md).
