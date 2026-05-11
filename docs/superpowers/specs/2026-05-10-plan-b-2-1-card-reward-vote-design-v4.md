# Plan B.2.1 — card reward vote (design v4)

**Date**: 2026-05-10
**Status**: Draft v4 — post-GPT5.5-follow-up-review on v3 (single reviewer; same conversation thread as one of the original 7). Spec-of-record; implementation-ready.
**Predecessor**: [`2026-05-10-plan-b-2-1-card-reward-vote-design-v3.md`](./2026-05-10-plan-b-2-1-card-reward-vote-design-v3.md). Review at `2026-05-10-plan-b-2-1-card-reward-vote-design_REVIEWS/GPT5.5_v3_review.txt`.
**Scope**: Second sub-plan of Plan B. Adds the **card reward** vote — the next "click 1-of-N option-button screen" decision after Neow. Same suspend-and-resume pattern as B.1, copy-paste-modified into a new patch class. Adds two new pieces of Game-side machinery: a **Proceed-skip gate** that prevents the streamer from bypassing chat by clicking through rewards unclaimed (with per-act skip budget), and an in-game **skip-counter label** so the streamer can see how many skips they have left.

> **Architectural hard constraint** (carried forward from B.1): every Harmony prefix that triggers a vote MUST use the **suspend-and-resume** pattern. Prefix returns immediately (`false` to skip original, after firing `_ = HandleVoteAsync(...)` as fire-and-forget). The async handler runs the vote, then re-invokes the chosen game-state mutation via `dispatcher.Post(...)`. **No blocking the Godot main thread on `AwaitWinnerAsync().GetAwaiter().GetResult()`, ever.**

## Author's note on v4 changes

GPT5.5's follow-up review on v3 surfaced 14 actionable items — 5 contradictions/correctness fixes, 5 spec-quality cleanups, 4 minor consistency nits. All folded in. Headline shifts:

1. **Run-ID `Prepare` checks split into hard vs soft tiers** — Decision 6 in v3 said "log Warn and disable run-ID check on shape mismatch", but the reflected-members list put `RunManager.Instance` / `DebugOnlyGetState()` / `RunState.Id` under "If ANY check fails: log Error, return false from `Prepare`". v4 splits explicitly: hard checks (vote target shape) abort patch registration; soft checks (run-id accessor) just disable the guard.
2. **Skip-gate activation gate added** — v3 fixed the missing-settings case but skip gate would still enforce when (a) `CardRewardVotePatch.Prepare` failed, (b) `Voter.Default` is null, (c) settings parsed but oauth/channel are malformed. v4: skip gate enforces only when `ShouldEnforceSkipGate()` returns true — checks `SettingsResult.Success` AND `CardRewardVotePatch.PreparedSuccessfully` AND `Voter.Default != null`. Otherwise vanilla skip behaviour.
3. **Reroll receipt → generic "card selection changed".** v3 claimed "streamer rerolled" but the holder-snapshot mismatch could equally be reroll, screen close, run abandon, alternate path, or game update. v4: receipt is `Vote result ignored — card selection changed before apply` (per nit #14 + must-fix #3 combined).
4. **`SkipBudgetTracker` decoupled from `Guid`.** v3 typed `_lastSeenRunId` as `Guid?`, but the spec also says the actual type is uncertain. v4: tracker uses `string?` and accepts pre-normalized run-id strings. The Harmony layer does `runState.Id.ToString()` (or whatever the actual accessor is). Type-independent and test-friendly.
5. **v4 is self-contained for `CardRewardVotePatch`.** v3 said "see v2 spec for the full patch sketch" — bad for an implementation-ready spec. v4 inlines the behavioural contract.
6. **`_ExitTree` patchability added to Task 1 verification.** v3 only verified `_Ready`. v4 verifies both.
7. **`RewardSkippedFrom` checks settings BEFORE recording skip.** v3 mutated tracker even in degraded mode. v4 uses `ShouldEnforceSkipGate()` helper as the gate.
8. **Step 1 wording refined** — "label remains visible and unchanged after card claim when no skip was used" (avoids implying card claim triggers label refresh).
9. **`Voter.Default.Chat.SendMessageAsync(...)` access path verified explicitly** — `coordinator.Chat` is the existing accessor used by NeowBlessingVotePatch (see [src/Game/DecisionVotes/NeowBlessingVotePatch.cs:64](../../../src/Game/DecisionVotes/NeowBlessingVotePatch.cs#L64)); `SendMessageAsync` routes through `OutgoingMessageQueue` (see [src/Ti/Chat/TwitchIrcChatService.cs:285-292](../../../src/Ti/Chat/TwitchIrcChatService.cs#L285-L292)). v4 spec states this verification rather than hand-waving.
10. **TiLog prefix scope clarified** — Decision 20 in v3 said "all log calls"; v4 specifies "all `Game/` and `ModEntry` log call sites; `Ti/` unchanged."
11. **Current-act detection pinning escalated** — v4 explicitly requires Task 1 to pin the current-act access pattern AND record it in notes/06 BEFORE the implementation proceeds past the spike.
12. **Failure mode #8 refined** — guard-was-degraded-at-start case no longer aborts resume on null current state. Explicit rule: guard fires only if start-state was non-null AND current-state mismatches.
13. **Mode B acceptance step made conditional** — Step 6's "look + back out" sub-step is conditional on vanilla actually supporting back-out from `NCardRewardSelectionScreen`. Task 1 verifies the UI path; if no back-out exists, sub-step is recorded as "Mode B is theoretical until vanilla provides such a path".
14. **Cancellation receipt timing acknowledged** — chat hears `Vote result ignored` 30s after their vote (because we can't externally cancel the session). v4 notes this is acceptable for v0.1; if it confuses chat in operator validation, v0.2 can add a "card selection changed mid-vote" early signal.

## Goals

1. **Ship the card reward vote end-to-end** — chat votes on which of the (typically 3) cards the streamer adds; suspend-and-resume copy of B.1's pattern; receipts and tally label work identically.
2. **Prevent streamer-side bypass via Proceed.** With default settings AND a working chat-vote path, every card reward must be either claimed (chat picks) or counted against a finite per-act skip budget. <!-- CHANGED v4: condition on chat-vote path -->
3. **Make the skip budget visible** — a small in-game label near the Proceed button shows `Card skips: <remaining>/<limit> act`.
4. **Fail soft on every new failure mode** — bad settings keys, missing rewards-screen UI nodes, run/act detection edge cases, card-selection changes mid-vote, run-abandon-mid-vote. Mod stays loaded; game keeps running.
5. **De-risk B.2.2 boss relic and B.2.3 map path** by making the second working example of suspend-and-resume real.
6. **Apply the run-ID guard to `NeowBlessingVotePatch` too** so B.2.2 inherits a consistent template.

## Non-goals

- B.2.2 boss relic, B.2.3 map path, B.2.4 in-game settings UI, B.3 act-boss.
- Helper / base-class extraction for suspend-and-resume.
- Per-run skip budget (`cardSkipsPerRun`).
- Mode A (looking forfeits skip).
- Patching reroll or non-`SelectCard` buttons.
- Patching `NRewardsScreen.OnProceedButtonPressed` directly.
- Per-relic curation.
- Settings-driven vote duration.
- BBCode stripping in receipts.
- Multiplayer co-op support.
- Localised receipts.
- In-game error toasts.
- Persisting skip counters across save/quit/reload.
- Skip-without-looking detection.
- `AllowSkipping()` re-enable after card claim.
- Streamer-configurable per-vote receipts.
- **Externally cancellable vote sessions.** When card selection changes mid-vote, the session still runs to its 30s timer; chat sees the `Vote result ignored` receipt only at session close. v0.2 may add early-signal cancellation. <!-- NEW v4 -->

## Decisions

| # | Decision | Rationale |
|---|---|---|
| 1 | **B.2.1 covers card reward only.** | Single-vote vertical slice. |
| 2 | **Patch target for vote: `NCardRewardSelectionScreen.SelectCard(NCardHolder)` Prefix.** | Verified from decompiled source; AutoSlay handler confirms call path. |
| 3 | **Patch target for skip gate: `NRewardsScreen._Ready` Postfix.** | **Implementation note**: Task 1 MUST verify Harmony patches `_Ready` correctly **AND `_ExitTree`** (used for `_activeLabel` cleanup). <!-- CHANGED v4: also _ExitTree --> Write a no-op postfix on each that just logs; confirm both fire on real rewards screens. Fall back to `_EnterTree` or `_Notification` for `_Ready`; rely on `IsInstanceValid` checks alone if `_ExitTree` is unreliable. |
| 4 | **Patch target for skip-detect: `NRewardsScreen.RewardSkippedFrom(Control)` Postfix.** | Detects card-reward skips, **checks `ShouldEnforceSkipGate()` BEFORE recording skip in tracker** <!-- CHANGED v4: order fix -->, decrements counter, sends chat receipt, refreshes label, and re-evaluates gate (multi-card-reward case). |
| 5 | **Suspend-and-resume pattern reused verbatim from B.1.** | Same two-flag re-entry guard, same post-Start fallback, same `IsInstanceValid` resume check, same `dispatcher.Post(...)` resume invocation. Plus `VoteTallyLabel.AttachTo(session)` explicitly posted at vote start. |
| 6 | **Run-ID guard with hard/soft `Prepare` tiers.** <!-- CHANGED v4 --> | **Hard checks** (failure → `Prepare` returns false → patch silently skips registration → vanilla flow): `NCardRewardSelectionScreen` exists; `SelectCard(NCardHolder)` exists; `_options` field exists; card-holder collection accessible. **Soft checks** (failure → log Warn → patch registers with run-ID guard disabled → vote works without guard): `RunManager.Instance` reachable; `DebugOnlyGetState()` returns non-null with stable Id property; `Players` accessor reachable. **Run-ID mismatch at resume: log Warn, drop resume.** Apply the same hard/soft split to `NeowBlessingVotePatch`. |
| 7 | **Skip is never a chat-vote option.** | Chat-vs-streamer asymmetry. Vote options = current cards on screen, dynamic count (1 to N). |
| 8 | **Skip budget: single per-act cap.** | `cardSkipsPerAct` (default `1`). Skip allowed iff `cardSkipsPerAct < 0 OR _actSkipsUsed < cardSkipsPerAct`. Default = 1 skip per act. Strict = `0`. Permissive = `-1`. |
| 9 | **In-game "skips remaining" label parented under `NRewardsScreen` near `_proceedButton`.** | New `CardSkipCounterLabel` Godot `RichTextLabel`. Hidden when `cardSkipsPerAct == -1`. `_activeLabel` static reference nulled in `_ExitTree` postfix; consumer sites guard with `Godot.GodotObject.IsInstanceValid(_activeLabel)`. |
| 10 | **Random fallback (zero votes received): random card, never skip.** | "Play the game" semantics. |
| 11 | **Receipt format: name-only.** | `Vote: #0 Strike, #1 Defend, #2 Bash — 30s, type #N or N`. |
| 12 | **Reroll, alternates, and other non-`SelectCard` buttons not patched.** | Streamer uses them freely. **If holder-snapshot mismatch detected at resume (could be reroll, screen close, run abandon, alternate path, etc.), chat receives `Vote result ignored — card selection changed before apply` receipt.** <!-- CHANGED v4: generic wording, no false claim of cause --> Receipt fires when the 30s vote closes and resume validation fails (cannot externally cancel session). |
| 13 | **No helper / base class extraction in B.2.1.** | Rule of Three. |
| 14 | **Use vanilla DevConsole for dev iteration, no custom debug patches.** | Auto-unlocks when `ModManager.IsRunningModded()`. |
| 15 | **`ModEntry.Settings` static accessor for patches.** | `internal static SettingsResult? Settings { get; private set; }` on `ModEntry.cs`. |
| 16 | **Skip receipt formatting inlined in `CardRewardSkipGatePatch`.** | Game-domain knowledge stays in Game/. |
| 17 | **Skip counters use plain `++`, not `Interlocked`.** | Main-thread-only. |
| 18 | ~~**Mode B (skip allowed regardless of look) chosen over Mode A (looking forfeits skip).**~~ **SUPERSEDED 2026-05-11 — see Decision 18 amendment below.** Original v4 wording: Deliberate deviation from original brainstorming intent — see v3 author's note. Acceptance Step 6's Mode B verification is conditional on vanilla supporting back-out from `NCardRewardSelectionScreen` to `NRewardsScreen` without selecting; Task 1 verifies this UI path exists. | Original chose Mode B because looking-then-backing-out was viewed as a soft UX exploration, not a commitment. **Surfinite reversed this on 2026-05-11 after operator validation Steps 0–3 surfaced UX friction with skip-without-looking** (see "Plan B.2.1 design pivot — RESOLVED 2026-05-11" in notes/06). |
| 19 | **Pure `SkipBudgetTracker` class for testability.** | **Tracker uses `string?` for run-id, not `Guid?`** <!-- CHANGED v4: type-independent --> — Harmony layer normalizes run-id to a string before passing in (`runState.Id.ToString()` or equivalent). Decouples tracker from the actual run-id field type, which is uncertain. |
| 20 | **TiLog `[SlayTheStreamer2]` prefix on all `Game/` and `ModEntry` log calls.** <!-- CHANGED v4: scope clarified --> | `Ti/` log call sites are NOT touched (matches "Ti/ unchanged" in architecture). If global tagging becomes desirable later, prefix once in the sink rather than editing call sites. |
| 21 | **Skip gate enforces only when card-vote infrastructure is fully available.** <!-- NEW v4; AMENDED 2026-05-11 --> | New `ShouldEnforceSkipGate()` helper returns true iff: (a) `ModEntry.Settings is SettingsResult.Success`; (b) `CardRewardVotePatch.PreparedSuccessfully == true`; (c) `Voter.Default != null`; **(d) `Voter.Default.Chat.State` is NOT one of the terminal failure states `AuthenticationFailed` / `JoinFailed` / `Disposed`** (2026-05-11 amendment — see "Decision 21 amendment" below). **Temporary Twitch disconnect mid-run does NOT disable the gate** (`Reconnecting` / `Connecting` are transient; reconnect+backlog handles them). Permanent no-settings / vote-patch-not-prepared / no-Voter / terminal-chat-fail degrades to vanilla. |

### Decision 18 amendment 2026-05-11 — Mandatory-look + Model 2 (transactional commit-on-Proceed) replaces Mode B

**New rule** — two principles:
1. **Mandatory-look**: the streamer must open the card sub-screen for every pending card-reward button on a rewards screen before the parent's Proceed/Skip click is allowed. Skipping-without-looking is impossible.
2. **Transactional commit**: sub-screen Skip AND Escape→Resume back-out are both *tentative* states — vanilla puts the button in `_skippedRewardButtons` but our budget logic deliberately ignores that list. Until the streamer clicks the parent's Proceed/Skip button, they can re-open the same sub-screen and claim via vote to undo any tentative skip. Budget is charged only when the parent click actually tears down the screen (observed in `AfterOverlayClosed` prefix). Per-card-button budget enforcement: if pending card count > remaining budget, the parent click is blocked — so under default `cardSkipsPerAct: 1`, skipping two cards on the same screen is impossible (would cost 2 skips, budget allows 1).

**Additional rule**: once a card vote countdown is running, neither sub-screen Skip (blocked by `OnAlternateRewardSelected` prefix, pre-existing) nor parent Proceed/Skip (blocked by `VoteInProgress` guard in the OnProceedButtonPressed prefix) can fire. Streamer cannot read chat's trending vote and bail.

**Why amended (two iterations)**:
- **First iteration (Model 1)** — operator validation Steps 0–3 (and the withdrawn-vanilla-bug investigation, commit `9053e17`) surfaced UX friction with skip-without-looking. Surfinite's intent shifted to "force engagement before any skip is allowed". Mode B's looking-for-free property remained useful; the asymmetry (looking free, skipping costs, skipping-without-looking impossible) became the new design. Implemented as immediate-charge on each sub-screen close-without-pick (back-out suppressed via `_suppressNextCardSkip`; sub-screen Skip charged via `RewardSkippedFrom_Postfix`; parent-Skip charged via `AfterOverlayClosed_BudgetPrefix`). Committed in `plan-b-2-1/20.x`.
- **Second iteration (Model 2)** — Model 1 smoke-test surfaced a UX problem: Surfinite clicked sub-screen Skip as an exploratory "what does this button do?" action with the 3 cards visible. Vanilla's sub-screen Skip is a *commit-to-skip* path (calls `OnAlternateRewardSelected(DismissScreenAndRemoveReward)`), so under Model 1 the budget was charged. He then went back, re-opened the sub-screen, picked a card via vote — but the budget remained charged. The Model 2 refinement defers the charge to the parent's Proceed/Skip click; tentative states (sub-screen Skip and back-out, indistinguishable on vanilla's side) cost nothing if the streamer ultimately claims. Surfinite's principle was "Proceed = confirmation that the skip tally isn't going to change anymore" — Model 2 implements that directly. Committed in `plan-b-2-1/21.x`.

**Implementation surface (Model 2)** — see `src/Game/DecisionVotes/CardRewardSkipGatePatch.cs`:
- Static `HashSet<ulong> _openedCardRewardButtonIds` — tracks NRewardButton instances whose card sub-screen was opened. Cleared in `SetRewards` postfix (fresh screen) and `AfterOverlayClosed` postfix (defensive).
- `NRewardButton_OnRelease_Prefix` — records `__instance.GetInstanceId()` when `__instance.Reward is CardReward or SpecialCardReward`. Vanilla's sync click handler at [NRewardButton.cs:214](../../../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Rewards/NRewardButton.cs#L214), runs before the async `GetReward()` opens the sub-screen.
- `NRewardsScreen_OnProceedButtonPressed_Prefix` — the gate. Three block reasons: (1) `VoteInProgress` (no bailing on chat's trend); (2) `Unopened > 0` (mandatory-look); (3) `_actSkipsUsed + pending > limit` (budget). On pass, lets vanilla through; `AfterOverlayClosed` then charges.
- `NRewardsScreen_AfterOverlayClosed_BudgetPrefix` — counts pending card rewards (alive in `_rewardButtons` — deliberately ignores `_skippedRewardButtons`) and charges budget + sends one receipt per pending. Single charge point under Model 2.
- `NRewardsScreen_SetRewards_Postfix` — observes run/act change for budget reset; attaches the counter label. Does NOT call `DisallowSkipping` (Model 2 keeps Proceed vanilla-enabled and relies on the prefix block — `_skipDisallowed` is sticky on the vanilla side and would persist incorrectly when the streamer claims mid-screen).

**Patches removed in the Model 1→Model 2 transition**:
- `NRewardsScreen_RewardSkippedFrom_Postfix` — vanilla's `RewardSkipped` signal fires for both back-out and sub-screen Skip, but Model 2 doesn't charge until Proceed commits, so no postfix needed.
- `NCardRewardSelectionScreen_ExitTree_Prefix` (the `_suppressNextCardSkip` back-out detector) — Model 2 treats back-out and sub-screen Skip as tentative-equivalent, no need to distinguish them.

**Vanilla quirk identified during design**: the parent's Proceed-as-Skip click does NOT emit `NRewardButton.RewardSkipped` for the buttons that get skipped — vanilla's `AfterOverlayClosed` iterates `_rewardsContainer` children directly and calls `Reward.OnSkipped()` (the model-level method) on each. So the `RewardSkippedFrom` signal handler only catches the sub-screen-derived path. Under Model 2 this asymmetry doesn't matter (charge is on `AfterOverlayClosed` regardless of how the skip got triggered), but it's worth remembering for B.2.2 / boss-relic / future patches.

**Acceptance gate impact**:
- Step 5 (multi-reward-type screen) now also exercises mandatory-look — multi-reward screens with both a card and a non-card reward require opening the card sub-screen before parent Proceed/Skip is allowed.
- Step 6 gains a new sub-step **"Model 2 transactional verification"**: with default settings, (a) click parent Skip without opening sub-screen — verify blocked; (b) open sub-screen, sub-screen Skip → close → verify no chat receipt fires yet AND budget counter unchanged; (c) re-open sub-screen, pick a card, vote completes, card claimed — verify counter still unchanged; (d) click parent Proceed — verify screen closes with no chat receipt (nothing skipped). Confirms tentative-skips can be undone via claim.
- Step 6 also gains **"Mandatory-look verification"**: (a) click parent Skip with unopened card on screen — blocked; (b) open sub-screen, Escape→Resume back out, click parent Skip — verify it succeeds, chat receives `Streamer skipped a card reward (1/1 act)`, counter updates.
- Step 6's old "Mode B verification (look + back out)" sub-step is subsumed by the two above.
- Step 6 gains **"no-skip-during-vote verification"**: start a vote (click a card on the sub-screen), then immediately Escape→Resume to parent, click parent Skip — verify blocked with `parent Skip blocked: vote in progress` log.

**Known v0.1 unresolved**: vanilla's go-back arrow on the map screen ([Screenshot 2026-05-11 150905.png](../../../../Pictures/Screenshots/Screenshot%202026-05-11%20150905.png)) re-opens the prior rewards screen, potentially after Model 2 has already charged budget. Going back and (re-)skipping a card could double-charge; going back and claiming previously-skipped could leave a stale charge. v0.2 polish: either block the back-arrow under our gate or implement refund-on-claim. Tracked in [notes/06](../../../notes/06-followups-and-deferred.md).

**Spec drift acknowledgment**: this amendment changes a Decision, not an architecture or non-goal. Spec stays at v4. If a v5 is ever needed (e.g., for B.2.2), this amendment will be folded in as a Decision-19-or-later renumber.

### Decision 21 amendment 2026-05-11 — terminal chat-failure also degrades gate

**Original Decision 21 wording**: skip gate degrades to vanilla only on permanent no-settings / vote-patch-not-prepared / no-Voter. Terminal chat-auth failure (`AuthenticationFailed` from bad oauth, `JoinFailed` from banned/wrong channel) was NOT in the list, so the gate would stay active even though no chat vote could ever run.

**Operator-validation Step 7 surfaced this**: malformed-settings test (`schemaVersion: 99`) degraded gate cleanly as expected. But Surfinite separately observed during testing that a *valid-settings, bad-oauth* configuration leaves the streamer in the worst-of-both-worlds state — chat never connects so vote falls back to vanilla per-click, but the skip-gate is still mandatory-look + budget gated against a chat that doesn't exist.

**New rule (additive)**: `ShouldEnforceSkipGate()` also returns false when `Voter.Default.Chat?.State` is one of the terminal-failure values `AuthenticationFailed`, `JoinFailed`, or `Disposed`. These match the `ChatConnectionState` doc comments labeled "terminal -- no retry" / "banned / channel doesn't exist -- no retry". Transient states (`Connecting`, `Reconnecting`) still keep the gate active per the original Decision 21 — temporary Twitch hiccups don't change gameplay.

**Why this is safe**: the existing operator validation Step 7 covers the malformed-settings path. The new branch only adds further degradation paths; it can never make a previously-vanilla case suddenly enforce. No new edge cases on the enforce-side to worry about.

## Architecture

```
src/
├── Ti/                                          ✅ unchanged from B.1; NO modifications in B.2.1 (Decision 20)
├── Game/                                        ✏️  extended in B.2.1
│   ├── Bootstrap/
│   │   └── ModSettings.cs                       ✏️  add `cardSkipsPerAct` key
│   ├── DecisionVotes/
│   │   ├── NeowBlessingVotePatch.cs             ✏️  add run-ID guard (hard/soft Prepare) + TiLog prefix tag
│   │   ├── CardRewardVotePatch.cs               🆕 B.2.1 — Harmony Prefix on NCardRewardSelectionScreen.SelectCard
│   │   ├── CardRewardSkipGatePatch.cs           🆕 B.2.1 — Postfix on NRewardsScreen._Ready + RewardSkippedFrom + _ExitTree
│   │   └── SkipBudgetTracker.cs                 🆕 B.2.1 — pure logic class (string-typed run-id)
│   └── Ui/                                      🆕 B.2.1 — new sub-namespace; StS2-coupled UI
│       └── CardSkipCounterLabel.cs              🆕 B.2.1 — Godot RichTextLabel parented under NRewardsScreen near proceed button
└── ModEntry.cs                                  ✏️  add static Settings accessor + retro-apply TiLog prefix to ModEntry's call sites

tests/
├── Bootstrap/
│   └── ModSettingsTests.cs                      ✏️  extend with ~5 tests for `cardSkipsPerAct`
└── Game/
    └── DecisionVotes/
        ├── SkipBudgetTrackerTests.cs            🆕 B.2.1 — pure budget logic (~10 tests, no Godot/Harmony)
        └── CardRewardSkipGateTests.cs           🆕 B.2.1 — Harmony shim integration (~5 tests)
```

**Net new code estimate**: `CardRewardVotePatch` ~230 LOC; `CardRewardSkipGatePatch` ~150 LOC (slight bump for `ShouldEnforceSkipGate` helper); `SkipBudgetTracker` ~80 LOC; `CardSkipCounterLabel` ~80 LOC; `NeowBlessingVotePatch` ~10 LOC additions; `ModSettings` additions ~20 LOC + ~30 LOC tests; `SkipBudgetTrackerTests` ~150 LOC; `CardRewardSkipGateTests` ~80 LOC; `ModEntry` additions ~10 LOC + retro-TiLog-prefix ~5 LOC. Total ~580 LOC of source, ~260 LOC of tests.

## `CardRewardVotePatch` (the vote — self-contained spec) <!-- CHANGED v4: full sketch inline, not v2 reference -->

Copy-paste-modified from [`src/Game/DecisionVotes/NeowBlessingVotePatch.cs`](../../../src/Game/DecisionVotes/NeowBlessingVotePatch.cs). Same five sections, same flags, same try/catch shape.

### Reflected members verified by `Prepare`

**Hard checks** (failure → `Prepare` returns false → patch silently skips → vanilla card flow):

- Type `MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardSelectionScreen` exists.
- Method `SelectCard(NCardHolder)` exists with exact signature.
- Field `_options` exists, type assignable to `IReadOnlyList<MegaCrit.Sts2.Core.Models.CardCreationResult>`.
- Card-holder collection field exists (Task 1 pins exact accessor — likely `_cardRow`'s children).

**Soft checks** (failure → log Warn → patch registers WITHOUT run-ID guard):

- `MegaCrit.Sts2.Core.Runs.RunManager.Instance` reachable.
- `RunManager.DebugOnlyGetState()` exists; result has `Id` property (Task 1 pins type).
- `RunState.Players.Count` reachable.

If hard check fails: `TiLog.Error("[SlayTheStreamer2][card-vote] Prepare failed: ...", null);` → return false. Vote degrades to vanilla.

If soft check fails: `TiLog.Warn("[SlayTheStreamer2][card-vote] run-ID guard degraded — ...", null);` → patch still registers; vote works without guard.

### Patch shape (behavioural contract)

```csharp
[HarmonyPatch(typeof(NCardRewardSelectionScreen), nameof(NCardRewardSelectionScreen.SelectCard))]
internal static class CardRewardVotePatch
{
    private static int _voteInProgress;       // Interlocked: cross-thread (vote async / postfix sync)
    private static int _resumeInProgress;     // Interlocked: same
    private static int _multiplayerWarnFired; // Interlocked: same; one-time warning per process

    internal static bool PreparedSuccessfully { get; private set; }   // NEW v4: skip gate reads this
    internal static bool RunIdGuardEnabled { get; private set; }      // NEW v4: false if soft check failed
    internal static bool VoteInProgress => _voteInProgress == 1;       // exposed for skip gate cross-check

    static bool Prepare(MethodBase? original) {
        // — hard checks → return false on any miss; PreparedSuccessfully stays false —
        // — soft checks → log Warn on miss; RunIdGuardEnabled = result of soft checks —
        // — on success: PreparedSuccessfully = true; return true —
    }
}
```

### Prefix guard order (top-to-bottom)

1. **Re-entry guard**: if `_resumeInProgress == 1`, return true (let our own re-call run vanilla).
2. **Options snapshot**: extract `_options`; if null/empty, return true (vanilla flow).
3. **MP bail**: if `Players.Count > 1`, log one-time Warn (`[SlayTheStreamer2][card-vote] multiplayer detected`), return true.
4. **Chat-readiness gate**: `Voter.Default?.Chat?.State == ChatConnectionState.ConnectedReadWrite`; if not, return true.
5. **Capture run-id at start**: `runIdAtStart = (RunIdGuardEnabled ? runState?.Id?.ToString() : null)`. If `RunIdGuardEnabled` but `runState` is null at start time, log Warn, set `runIdAtStart = null`, continue (degraded for this vote).
6. **Atomic vote-in-progress flip**: `Interlocked.CompareExchange(ref _voteInProgress, 1, 0)`. If already 1, log Debug `repeat click during open vote — suppressed`, return false (suppress this click; first-click fallback unchanged).
7. **Player-click index**: find `cardHolder` in current holder collection; this is the fallback if vote times out / fails.
8. **Snapshot option labels**: `_options[i].Card.Name.GetText()` for chat receipts.
9. **Snapshot holder signature**: capture identity / count of holder collection (Task 1 picks shape — list of `WeakReference<NCardHolder>` or count + Godot-instance-ID list). Used at resume to detect reroll / screen rebuild.
10. **Vote start with try/catch fallback**: `coordinator.Start("Card Reward", labels, TimeSpan.FromSeconds(30))`; on throw, log Error, reset flags, return true (vanilla fallback).
11. **Fire-and-forget**: `_ = HandleVoteAsync(coordinator, screen, session, optionsSnapshot, holderSignature, playerClickIndex, runIdAtStart);`.
12. **Return false** (suspend original).

### `HandleVoteAsync` (background async)

```csharp
private static async Task HandleVoteAsync(VoteCoordinator coordinator,
    NCardRewardSelectionScreen screen, VoteSession session,
    IReadOnlyList<CardCreationResult> snapshot,
    HolderSignature holderSignature, int playerClickIndex, string? runIdAtStart)
{
    try {
        coordinator.Dispatcher.Post(() => VoteTallyLabel.AttachTo(session));   // explicit per v2 — prevents copy-paste oversight

        int winnerIndex;
        try {
            winnerIndex = await session.AwaitWinnerAsync();
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][card-vote] AwaitWinnerAsync threw; falling back to player click", ex);
            winnerIndex = playerClickIndex;
        }
        if (winnerIndex < 0 || winnerIndex >= snapshot.Count) {
            TiLog.Warn($"[SlayTheStreamer2][card-vote] winnerIndex {winnerIndex} out of range; using player click", null);
            winnerIndex = playerClickIndex;
        }
        coordinator.Dispatcher.Post(() => ResumeOnMainThread(screen, winnerIndex, playerClickIndex,
            runIdAtStart, holderSignature));
    } catch (Exception ex) {
        TiLog.Error("[SlayTheStreamer2][card-vote] HandleVoteAsync threw; attempting fallback resume", ex);
        try {
            coordinator.Dispatcher.Post(() => ResumeOnMainThread(screen, playerClickIndex, playerClickIndex,
                runIdAtStart, holderSignature));
        } catch (Exception postEx) {
            TiLog.Error("[SlayTheStreamer2][card-vote] fallback resume Post itself threw; resetting flags", postEx);
            Interlocked.Exchange(ref _resumeInProgress, 0);
            Interlocked.Exchange(ref _voteInProgress, 0);
        }
    }
}
```

### `ResumeOnMainThread` (main thread, called via dispatcher.Post)

```csharp
private static void ResumeOnMainThread(NCardRewardSelectionScreen screen,
    int preferredIndex, int playerClickIndex, string? runIdAtStart, HolderSignature snapshotSig)
{
    Interlocked.Exchange(ref _resumeInProgress, 1);
    try {
        // 1. IsInstanceValid check — drop if screen freed
        if (!Godot.GodotObject.IsInstanceValid(screen)) {
            TiLog.Warn("[SlayTheStreamer2][card-vote] resume: screen no longer valid; dropping", null);
            return;
        }

        // 2. Run-ID guard (only if guard was enabled AND we captured a non-null start id)
        if (runIdAtStart != null) {
            var currentRunId = RunManager.Instance.DebugOnlyGetState()?.Id?.ToString();
            // If start was captured but current is null (run torn down), abort fail-safe.
            // If start was null (guard degraded at start), we wouldn't be here.   // CHANGED v4: per nit #13
            if (currentRunId != runIdAtStart) {
                TiLog.Warn("[SlayTheStreamer2][card-vote] Resume aborted: run changed during vote", null);
                SendCancellationReceipt();   // generic "card selection changed" receipt
                return;
            }
        }

        // 3. Holder signature check — detects reroll / screen rebuild / alternate path / any other mutation
        var currentHolders = GetCurrentHolders(screen);
        if (currentHolders == null || !HolderSignature.Equal(snapshotSig, currentHolders)) {
            TiLog.Warn("[SlayTheStreamer2][card-vote] Resume aborted: card selection changed before apply", null);
            SendCancellationReceipt();   // CHANGED v4: generic, no false-cause claim
            return;
        }

        // 4. Bounds check; fall back to playerClickIndex if winner index invalid
        int applyIndex = preferredIndex;
        if (applyIndex < 0 || applyIndex >= currentHolders.Count) {
            TiLog.Warn($"[SlayTheStreamer2][card-vote] preferred index {applyIndex} out of range; falling back to player click", null);
            applyIndex = playerClickIndex;
        }
        if (applyIndex < 0 || applyIndex >= currentHolders.Count) {
            TiLog.Warn("[SlayTheStreamer2][card-vote] neither preferred nor player index valid; dropping", null);
            return;
        }

        // 5. Re-derive holder from current screen state (NOT captured ref) and apply
        var winnerHolder = currentHolders[applyIndex];
        screen.SelectCard(winnerHolder);
    } catch (Exception ex) {
        TiLog.Error("[SlayTheStreamer2][card-vote] resume threw", ex);
    } finally {
        Interlocked.Exchange(ref _resumeInProgress, 0);
        Interlocked.Exchange(ref _voteInProgress, 0);
    }
}

private static void SendCancellationReceipt() {
    var coordinator = Voter.Default;
    if (coordinator?.Chat?.State != ChatConnectionState.ConnectedReadWrite) return;
    _ = coordinator.Chat.SendMessageAsync(
        "Vote result ignored — card selection changed before apply",   // CHANGED v4
        OutgoingMessagePriority.Normal);
}
```

### Lifecycle behaviours

- **Streamer dies / abandons mid-vote**: run-ID guard fires → Warn log → cancellation receipt → no resume.
- **Streamer triggers reroll mid-vote**: holder signature mismatch → Warn log → cancellation receipt → streamer must click new card → new vote.
- **Streamer escape (menu) mid-vote**: vote runs to normal close in background; `IsInstanceValid` check drops resume; no crash. (No cancellation receipt — chat sees normal close receipt with random fallback if no votes received.)
- **Rapid card clicks**: only first triggers vote; subsequent swallowed via `_voteInProgress` guard; first-click `playerClickIndex` is the fallback.
- **AutoSlay running**: `EmitSignal(Pressed)` reaches `SelectCard` → vote fires (acceptable; AutoSlay off in production).

## `CardRewardSkipGatePatch` (the gate)

### Activation gate <!-- NEW v4 -->

Both `_Ready` and `RewardSkippedFrom` postfixes call:

```csharp
private static bool ShouldEnforceSkipGate() {
    if (ModEntry.Settings is not SettingsResult.Success) return false;
    if (!CardRewardVotePatch.PreparedSuccessfully) return false;
    if (Voter.Default == null) return false;
    return true;
}
```

If false: postfix early-returns; no `DisallowSkipping`, no tracker mutation, no chat receipt. Skip gate degrades to vanilla.

Note: this does NOT check `Voter.Default.Chat.State`. Temporary Twitch disconnects during a run still leave the gate active (chat reconnects + delivers backlog; that path is well-trodden in B.1).

### Reflected members verified by `Prepare`

**Hard checks** (failure → patch silently skips):

- Type `NRewardsScreen` exists.
- `_rewardButtons` field, `_skippedRewardButtons` field, `_proceedButton` field exist.
- `DisallowSkipping()` method exists, public, parameterless.
- `RewardCollectedFrom(Control)` semantics verified: button removed from `_rewardButtons`. (If unverifiable, fall back to excluding via `_skippedRewardButtons`.)
- Type `NRewardButton` exists; has `Reward` accessor (Task 1 pins property vs field).
- Type `MegaCrit.Sts2.Core.Rewards.CardReward` exists.

**Soft checks** (failure → patch registers; gate degrades to permissive):

- `RunState.Acts` reachable; current-act access pattern pins-able.
- `RunState.Id` reachable for run-change detection.

(Soft tier matches the vote patch's run-id soft tier; if act/run change detection fails, gate still works but `_actSkipsUsed` only resets on process restart.)

### State

```csharp
internal static class CardRewardSkipGatePatch
{
    private static readonly SkipBudgetTracker _tracker = new();
    private static CardSkipCounterLabel? _activeLabel;
}
```

### `_Ready` Postfix

```csharp
public static void Postfix(NRewardsScreen __instance)
{
    try {
        // — Players.Count > 1 bail —
        if (!ShouldEnforceSkipGate()) return;   // NEW v4: activation gate

        var runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null) return;

        _tracker.ObserveRunAndAct(runState.Id?.ToString(), GetCurrentActIndex(runState));   // CHANGED v4: string

        if (!HasUnclaimedCardReward(__instance)) return;

        var settings = ((SettingsResult.Success)ModEntry.Settings!).Settings;
        if (!_tracker.IsSkipAllowed(settings.CardSkipsPerAct)) {
            __instance.DisallowSkipping();
        }

        AttachOrUpdateLabel(__instance, settings.CardSkipsPerAct);
    } catch (Exception ex) {
        TiLog.Error("[SlayTheStreamer2][card-skip-gate] _Ready postfix failed", ex);
    }
}
```

### `RewardSkippedFrom` Postfix (settings-check before record) <!-- CHANGED v4 -->

```csharp
public static void Postfix(NRewardsScreen __instance, Control button)
{
    try {
        if (!IsCardRewardButton(button)) return;
        if (!ShouldEnforceSkipGate()) return;   // CHANGED v4: check BEFORE recording

        _tracker.RecordSkip();

        var settings = ((SettingsResult.Success)ModEntry.Settings!).Settings;
        SendSkipReceipt(settings.CardSkipsPerAct);

        // Multi-card-reward gate re-evaluation
        if (!_tracker.IsSkipAllowed(settings.CardSkipsPerAct) && HasUnclaimedCardReward(__instance)) {
            __instance.DisallowSkipping();
        }

        if (_activeLabel != null && Godot.GodotObject.IsInstanceValid(_activeLabel)) {
            _activeLabel.UpdateText(_tracker.Snapshot(settings.CardSkipsPerAct));
        }
    } catch (Exception ex) {
        TiLog.Error("[SlayTheStreamer2][card-skip-gate] RewardSkippedFrom postfix failed", ex);
    }
}
```

### `_ExitTree` Postfix

```csharp
public static void Postfix(NRewardsScreen __instance) {
    _activeLabel = null;   // belt-and-suspenders for dangling-reference bug
}
```

(Task 1 verifies `_ExitTree` patches reliably; if not, fall back to `IsInstanceValid` checks alone.)

### Skip receipt formatter (single per-act knob, send via verified accessor)

```csharp
private static string FormatSkipReceipt(int actUsed, int actLimit) {
    string limitPart = actLimit < 0 ? "unlimited act" : $"{actUsed}/{actLimit} act";
    return $"Streamer skipped a card reward ({limitPart})";
}

private static void SendSkipReceipt(int actLimit) {
    var coordinator = Voter.Default;
    // Voter.Default.Chat is the existing accessor used by NeowBlessingVotePatch
    // (NeowBlessingVotePatch.cs:64: `coordinator.Chat.State`).
    // SendMessageAsync routes through OutgoingMessageQueue (TwitchIrcChatService.cs:285-292).
    if (coordinator?.Chat?.State != ChatConnectionState.ConnectedReadWrite) return;

    string text = FormatSkipReceipt(_tracker.ActSkipsUsed, actLimit);
    _ = coordinator.Chat.SendMessageAsync(text, OutgoingMessagePriority.Normal);
}
```

Receipt examples:
- Default: `Streamer skipped a card reward (1/1 act)`
- `cardSkipsPerAct: 3`: `(1/3 act)` ... `(2/3 act)` ... `(3/3 act)`
- `cardSkipsPerAct: -1`: `(unlimited act)`

## `SkipBudgetTracker` (pure logic, type-independent run-id) <!-- CHANGED v4 -->

```csharp
namespace SlayTheStreamer2.Game.DecisionVotes;

internal sealed class SkipBudgetTracker
{
    // Main-thread-only; plain state, no Interlocked.
    private int _actSkipsUsed;
    private int? _lastSeenActIndex;
    private string? _lastSeenRunId;       // CHANGED v4: string?, not Guid?

    public int ActSkipsUsed => _actSkipsUsed;

    public void ObserveRunAndAct(string? runId, int? actIndex) {
        if (runId != null && runId != _lastSeenRunId) {
            _actSkipsUsed = 0;
            _lastSeenRunId = runId;
            _lastSeenActIndex = actIndex;
            return;
        }
        if (actIndex.HasValue && actIndex != _lastSeenActIndex) {
            _actSkipsUsed = 0;
            _lastSeenActIndex = actIndex;
        }
    }

    public bool IsSkipAllowed(int actLimit) {
        if (actLimit < 0) return true;
        return _actSkipsUsed < actLimit;
    }

    public void RecordSkip() => _actSkipsUsed++;

    public SkipBudgetSnapshot Snapshot(int actLimit) => new(
        UsedThisAct: _actSkipsUsed,
        LimitThisAct: actLimit,
        RemainingThisAct: actLimit < 0 ? int.MaxValue : Math.Max(0, actLimit - _actSkipsUsed));

    internal void ResetForTests() {
        _actSkipsUsed = 0;
        _lastSeenActIndex = null;
        _lastSeenRunId = null;
    }
}

internal readonly record struct SkipBudgetSnapshot(int UsedThisAct, int LimitThisAct, int RemainingThisAct);
```

### Tests (unchanged from v3 except run-id type)

~10 tests covering strict / unlimited / positive limits, RecordSkip, ObserveRunAndAct semantics (run change resets, act change resets, identical no-op, null run-id no-op), Snapshot output. All Godot-free.

## `CardSkipCounterLabel` (the UI)

(Unchanged from v3.) Godot `RichTextLabel`. Position anchored relative to `_proceedButton` with fallback. Hidden if `cardSkipsPerAct == -1`. Text: `Card skips: <remaining>/<limit> act`. Static `_activeLabel` nulled in `_ExitTree`; consumer sites guard with `IsInstanceValid`.

## `ModEntry` extensions

```csharp
internal static class ModEntry {
    internal static SettingsResult? Settings { get; private set; }
    // After ModSettings.Load(...): Settings = result;
}
```

Plus retro-apply TiLog `[SlayTheStreamer2]` prefix to all existing TiLog calls **in `ModEntry.cs` only**. <!-- CHANGED v4: scope -->

## `NeowBlessingVotePatch` extensions

Apply the run-ID guard with hard/soft Prepare tiers (Decision 6 mirrored from card vote). ~10 LOC. Plus retro-apply TiLog `[SlayTheStreamer2]` prefix to all existing TiLog calls **in this file only**.

## `ModSettings` extensions

```jsonc
{
  "schemaVersion": 1,
  "channel": "...",
  "username": "...",
  "oauthToken": "...",
  "cardSkipsPerAct": 1
}
```

### Tests (unchanged from v3)

5 tests covering missing key, invalid value, negative-other-than-`-1` clamp, zero (strict), positive value.

## Failure modes & degradation

| # | Failure mode | Behaviour |
|---|---|---|
| 1 | `CardRewardVotePatch.Prepare` hard checks fail | Vote patch silently skips registration. **Skip gate's `ShouldEnforceSkipGate()` returns false → skip gate also degrades to vanilla.** <!-- CHANGED v4 --> |
| 2 | `CardRewardVotePatch.Prepare` soft checks fail | Vote patch registers; `RunIdGuardEnabled = false`; vote works without run-ID guard. |
| 3 | `CardRewardSkipGatePatch.Prepare` hard checks fail | Skip gate skips registration. Card vote still works; vanilla skip semantics. |
| 4 | `_Ready` postfix throws | Logs Error, vanilla `_Ready` already completed. No gate this round. |
| 5 | `RewardSkippedFrom` postfix throws | Logs Error. Tracker may not be incremented → future gates may be too permissive (fail-open). |
| 6 | `_ExitTree` postfix throws or doesn't fire | Logs Error if throws; if fails to fire, `_activeLabel` may dangle until next `_Ready` overwrites it; `IsInstanceValid` guard catches it. |
| 7 | Reflection failure on field | Logs Warn, returns "no card reward" / "can't determine cards" → fail-open. |
| 8 | `DebugOnlyGetState()` returns null at vote start | Logs Warn ("run-ID guard degraded — null state at start"). Vote proceeds without guard for that vote. |
| 9 | `DebugOnlyGetState()` returns null at resume time AND start was non-null | Treated as run change → resume aborts → cancellation receipt. |
| 10 | `DebugOnlyGetState()` returns null at resume time AND start was null | Guard was already disabled for this vote → resume proceeds without guard check. <!-- CHANGED v4: per nit #13 --> |
| 11 | Run-ID guard fires (run abandoned mid-vote) | Logs Warn, cancellation receipt, no resume. |
| 12 | Holder signature mismatch at resume (reroll, screen rebuild, alternate path, etc.) | Logs Warn, cancellation receipt `Vote result ignored — card selection changed before apply`, no resume. |
| 13 | Settings file missing/malformed | `ShouldEnforceSkipGate()` returns false → skip gate degrades to vanilla; vote patch also bails (no chat-readiness). |
| 14 | Save/quit/reload mid-run | Static counters reset on process restart. Documented as known v0.1 limit. |
| 15 | AutoSlay running | Vote fires (acceptable). |
| 16 | Multiplayer (Players.Count > 1) | Both patches bail. |
| 17 | Twitch disconnected mid-run | Vote patch bails on next click (chat-readiness gate); skip gate stays active (per Decision 21 — temp disconnect doesn't disable gate). |

## Acceptance gate

7-step gate. Each is a manual playthrough.

- **Step 0 — Pure regression check (B.1 features only).** Settings present with B.1 keys only (no `cardSkipsPerAct`). Mod loads cleanly. Run starts; Neow vote works (chat votes, winner applies); chat connect-once receipt fires. **No card-reward path exercised.** Confirms new patches don't regress B.1.

- **Step 1 — Card vote happy path (3 successful runs).**
  - chat votes for a card via `#0`/`#1`/`#2`, winning card claimed via dispatcher.Post resume
  - latest-wins on multi-vote-from-one-user
  - both `#N` and bare `N` accepted
  - close receipt fires with correct card name
  - VoteTallyLabel shows tally during vote
  - skip-counter label visible near Proceed button, format `Card skips: 1/1 act`
  - **skip-counter label remains visible and unchanged after card claim when no skip was used** <!-- CHANGED v4: per nit #9 -->

- **Step 2 — Skip used.** With `cardSkipsPerAct: 1`: rewards screen → click Proceed without claiming card → skip allowed → chat receipt `Streamer skipped a card reward (1/1 act)` → counter label updates to `0/1 act` → next combat: rewards screen opens with Proceed disabled → click card → vote runs → claim → Proceed enabled.

- **Step 3 — Skip blocked.** With `cardSkipsPerAct: 0`: rewards screen opens, Proceed visibly disabled. Streamer must click card → vote runs → claim → Proceed enabled.

- **Step 4 — Counter resets.** `act 2` console command → next rewards screen: counter resets to `1/1 act`. Same for new run (verify run-id mismatch resets counter).

- **Step 5 — Multi-reward-type screen.** Find or trigger rewards screen with card AND another reward (gold / potion / boss relic). With `cardSkipsPerAct: 0`: claim card via vote → verify Proceed becomes enabled (vanilla `_skipDisallowed` becomes irrelevant when button transitions to non-Skip mode) → claim or skip the other reward as normal. **If the other reward is locked from skipping after card claim**, document and add to v0.2 follow-up.

- **Step 6 — Edge cases.**
  - **Mid-vote run abandon** — start a card vote, immediately open menu and click Abandon Run, wait 30s for vote timer to expire. Verify run-ID guard fires (`Warn` log: `[SlayTheStreamer2][card-vote] Resume aborted: run changed during vote`). Verify cancellation receipt fires. No crash.
  - **Mid-vote reroll** if a relic enables it — start vote, click reroll on sub-screen, wait for vote timer. Verify chat receipt `Vote result ignored — card selection changed before apply` fires. Streamer clicks new card → new vote.
  - **Streamer escape** (via menu) mid-vote — vote runs to normal close in background; resume drops via `IsInstanceValid` check; no crash; normal close receipt (with random fallback if no votes received).
  - **Rapid card clicks** — only first triggers vote; subsequent clicks no-op via `_voteInProgress` guard.
  - **Mode B verification (look + back out)** — **Conditional on Task 1 confirming vanilla supports back-out from `NCardRewardSelectionScreen` to `NRewardsScreen` without selecting.** <!-- CHANGED v4 --> If supported: open card sub-screen, see cards, return to rewards screen WITHOUT picking, click Proceed; with `cardSkipsPerAct: 1`, skip is allowed (counter decrements). Confirms Decision 18. **If no back-out path exists in vanilla**: record as "Mode B verification N/A — no vanilla path to look-then-back-out without selecting; Mode B is theoretical for v0.1".

- **Step 7 — Activation-gate verification.** <!-- NEW v4 --> Set settings to a malformed state (e.g., wrong `oauthToken` or missing `channel`). Mod loads but `Settings` is `Malformed`. Trigger a rewards screen with a card. Verify: skip gate does NOT enforce (Proceed remains in vanilla state); skip-counter label NOT visible; clicking Proceed proceeds vanilla; no chat receipt fires. Confirms Decision 21.

## Open questions

None blocking. Two soft questions for the implementation phase:

1. **`DisallowSkipping()` lifecycle in multi-reward screens** — does vanilla's `TryEnableProceedButton` self-correct after card claim? Step 5 of acceptance gate is the validation point.
2. **Vanilla back-out path from `NCardRewardSelectionScreen`** — does it exist? Task 1 verifies; affects Step 6 Mode B sub-step.

## Notes/06 entry to add

(Same as v3 — see [v3 spec](./2026-05-10-plan-b-2-1-card-reward-vote-design-v3.md#notes06-entry-to-add-per-optional-enhancement-9-) for the full template.)

**Plus**: a "current-act detection — pinned in Task 1" subsection. <!-- CHANGED v4 --> Once Task 1 identifies the exact accessor (e.g., `runState.Acts.Count - 1` vs `runState.CurrentActIndex` vs derived from `runState.CurrentRoom`), the answer is recorded in notes/06 BEFORE the implementation proceeds past the spike. This is a hard prerequisite — without it, soft Prepare checks return false and the run-ID/act-detection guard stays disabled, weakening the safety feature.

## Cross-references

- [`docs/superpowers/specs/2026-05-09-plan-b-1-vertical-slice-design-v3.md`](./2026-05-09-plan-b-1-vertical-slice-design-v3.md) — B.1 spec.
- [`docs/superpowers/specs/META-REVIEW-2026-05-10-plan-b-2-1-card-reward-vote-design.md`](./META-REVIEW-2026-05-10-plan-b-2-1-card-reward-vote-design.md) — original 7-reviewer meta-review.
- [`docs/superpowers/specs/2026-05-10-plan-b-2-1-card-reward-vote-design-v3.md`](./2026-05-10-plan-b-2-1-card-reward-vote-design-v3.md) — v3 (post-Optional-Enhancements pick) for diff context.
- [`docs/superpowers/specs/2026-05-10-plan-b-2-1-card-reward-vote-design_REVIEWS/GPT5.5_v3_review.txt`](./2026-05-10-plan-b-2-1-card-reward-vote-design_REVIEWS/GPT5.5_v3_review.txt) — v3 follow-up review.
- [`notes/06-followups-and-deferred.md`](../../../notes/06-followups-and-deferred.md) — B.1 findings, run-ID guard origin, **B.2.1 reflected sts2.dll members + current-act pin**.
- [`src/Game/DecisionVotes/NeowBlessingVotePatch.cs`](../../../src/Game/DecisionVotes/NeowBlessingVotePatch.cs) — copy-paste source for `CardRewardVotePatch`; receives run-ID guard in B.2.1.
- [`src/Ti/Chat/TwitchIrcChatService.cs`](../../../src/Ti/Chat/TwitchIrcChatService.cs) — `SendMessageAsync` routes through OutgoingMessageQueue (lines 285-292).

---

**Final pre-implementation status**: v4 is the spec-of-record. All 14 GPT5.5-review items applied. Ready for `/superpowers:writing-plans` to produce the implementation plan.
