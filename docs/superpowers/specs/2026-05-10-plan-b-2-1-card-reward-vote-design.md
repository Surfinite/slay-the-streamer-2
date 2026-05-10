# Plan B.2.1 — card reward vote (design)

**Date**: 2026-05-10
**Status**: Draft v1 — pre-meta-review
**Predecessor**: [`2026-05-09-plan-b-1-vertical-slice-design-v3.md`](./2026-05-09-plan-b-1-vertical-slice-design-v3.md). B.1 shipped 2026-05-10 (`plan-b-1-complete` tag); the suspend-and-resume Harmony pattern is now production-validated.
**Scope**: Second sub-plan of Plan B. Adds the **card reward** vote — the next "click 1-of-N option-button screen" decision after Neow. Same suspend-and-resume pattern as B.1, copy-paste-modified into a new patch class. Adds two new pieces of Game-side machinery: a **Proceed-skip gate** that prevents the streamer from bypassing chat by clicking through rewards unclaimed (with per-act / per-run skip budget), and an in-game **skip-counter label** so the streamer can see how many skips they have left.

The remaining three v0.1 votes (boss relic, map path, act-boss) and the in-game settings panel are explicit non-goals — they belong to B.2.2, B.2.3, B.2.4, and B.3.

> **Architectural hard constraint** (carried forward from B.1): every Harmony prefix that triggers a vote MUST use the **suspend-and-resume** pattern. Prefix returns immediately (`false` to skip original, after firing `_ = HandleVoteAsync(...)` as fire-and-forget). The async handler runs the vote, then re-invokes the chosen game-state mutation via `dispatcher.Post(...)`. **No blocking the Godot main thread on `AwaitWinnerAsync().GetAwaiter().GetResult()`, ever.**

## Author's note on B.2.1 design intent

Three things shape this spec beyond "do what B.1 did, but for cards":

1. **Chat-vs-streamer asymmetry as an explicit design constraint.** Surfinite framed it during brainstorming: chat wants the streamer to lose; the streamer wants entertainment + a fair-feeling fight. Letting chat vote on "skip card reward" is a runaway chaos vector — chat would skip every card, streamer auto-loses. So **skip is never a chat-vote option**. Streamer-skip is gated behind a settings-tunable budget (default `1 per act, unlimited per run`), and the streamer can see their remaining budget on-screen.
2. **Don't extract a shared helper / base class yet.** The Rule of Three says wait for n=3 before abstracting. We have n=1 working in production (Neow); B.2.1 makes n=2; B.2.2 (boss relic) will make n=3 — *that* is when the actually-shared structure becomes obvious. Copy-paste-modify `NeowBlessingVotePatch` into `CardRewardVotePatch` and live with the duplication for one more slice. ~200 LOC duplicated is cheaper than a wrong abstraction.
3. **Lean on vanilla mechanics where they exist.** `NRewardsScreen.DisallowSkipping()` is a public method that disables the Skip-mode Proceed button until rewards are claimed. We piggyback on it instead of patching `OnProceedButtonPressed` directly. Smaller surface area, less Harmony footprint.

## Goals

1. **Ship the card reward vote end-to-end** — chat votes on which of the (typically 3) cards the streamer adds; suspend-and-resume copy of B.1's pattern; receipts and tally label work identically; Twitch backlog-on-JOIN behaviour from B.1 is reused untouched.
2. **Prevent streamer-side bypass via Proceed.** With default settings, every card reward must be either claimed (chat picks) or counted against a finite skip budget. The streamer cannot silently skip every card to escape chat agency.
3. **Make the skip budget visible** — a small in-game label near the Proceed button shows `Card skips: <act-remaining>/<act-limit> act, <run-remaining>/<run-limit> run`. Streamer always knows where they stand.
4. **Fail soft on every new failure mode** added by B.2.1 — bad settings keys, missing rewards-screen UI nodes, run/act detection edge cases, reroll-mid-vote, run-abandon-mid-vote. Mod stays loaded; game keeps running; vote silently absorbs when resume target is gone.
5. **De-risk B.2.2 boss relic and B.2.3 map path** by making the second working example of suspend-and-resume real. After B.2.1 ships, B.2.2's plan will see what's actually shared (likely: ~80% of the per-patch boilerplate) and a helper extraction can be planned with confidence.

## Non-goals

- B.2.2 boss relic, B.2.3 map path, B.2.4 in-game settings UI, B.3 act-boss. All separate sub-plans.
- **Helper / base-class extraction.** Deliberately deferred to B.2.2 — see Author's note #2.
- **Patching reroll**, alternates, or any other non-`SelectCard` button on `NCardRewardSelectionScreen`. Streamer uses reroll freely; vote starts when streamer clicks an actual card.
- **Patching `NRewardsScreen.OnProceedButtonPressed` directly.** We use vanilla's `DisallowSkipping()` instead — smaller surface, less risk of breaking unrelated reward types.
- **Per-relic curation** (chat-strong / streamer-strong relic blacklist). Surfinite raised this during brainstorming; deferred to v0.2 polish. Documented in notes/06.
- **Settings-driven vote duration.** B.1's `NeowBlessingVotePatch` hardcodes `TimeSpan.FromSeconds(30)`; B.2.1 keeps the same hardcoded value in `CardRewardVotePatch`. Adding a `voteDuration` settings key is B.2.4 territory (in-game settings UI).
- **BBCode stripping in receipts.** B.1's note still applies — if card names contain BBCode (unconfirmed), receipts will show literal markup. Add a stripper later if it actually surfaces.
- **In-game indicator that vote is "in progress" beyond the existing `VoteTallyLabel`.** B.1's top-right multi-line label is reused as-is.
- **Localised receipts.** English-only via Plan A's `EnglishReceipts`.
- **Multiplayer co-op support.** B.1's `Players.Count > 1` bail applies to all B.2.x patches.
- **Streamer-configurable per-vote receipts** (e.g., "no chat receipt for skip"). Default is "always chat receipt"; settings-driven receipt policy is a Plan A v2.3 follow-up.

## Decisions

| # | Decision | Rationale |
|---|---|---|
| 1 | **B.2.1 covers card reward only.** | Single-vote vertical slice; matches B.1's discipline. Bounds blast radius if boss relic (B.2.2) or map path (B.2.3) surface a surprise. |
| 2 | **Patch target for vote: `NCardRewardSelectionScreen.SelectCard(NCardHolder)` Prefix.** | Verified from decompiled source: `SelectCard` is the click-handler for individual card holders; AutoSlay handler at [`CardRewardScreenHandler.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/AutoSlay/Handlers/Screens/CardRewardScreenHandler.cs) confirms the call path (`EmitSignal(NCardHolder.SignalName.Pressed, ...)` → `SelectCard`). Single intercept point, identical shape to B.1's `OptionButtonClicked`. |
| 3 | **Patch target for skip gate: `NRewardsScreen._Ready` Postfix.** | After vanilla builds the rewards container, postfix checks for unclaimed card reward + skip budget, calls vanilla's `DisallowSkipping()` if budget exhausted. Vanilla's existing `_skipDisallowed` + `TryEnableProceedButton` logic does the rest. |
| 4 | **Patch target for skip-detect: `NRewardsScreen.RewardSkippedFrom(Control)` Postfix.** | Vanilla calls this when a reward button is skipped via Proceed. Postfix detects card-reward skips, decrements counters, sends chat receipt, refreshes label. |
| 5 | **Suspend-and-resume pattern reused verbatim from B.1.** | Same two-flag re-entry guard, same post-Start fallback in `HandleVoteAsync`'s outer catch (resumes with the streamer's clicked card if vote logic itself throws), same `IsInstanceValid` resume check, same `dispatcher.Post(...)` resume invocation. |
| 6 | **Run-ID guard added to resume path.** | B.1's notes/06 flagged this as B.2 hardening: capture `RunManager.Instance.DebugOnlyGetState()?.Id` at vote start, compare at resume, skip resume if changed. ~5 lines per patch. Closes the resume-after-abandon race. |
| 7 | **Skip is never a chat-vote option.** | Chat-vs-streamer asymmetry: making skip votable creates a chaos auto-lose. Streamer-skip via Proceed is the only skip path. Vote options = current cards on screen, dynamic count (1 to N). |
| 8 | **Skip budget: dual cap, per-act + per-run, both enforced.** | `cardSkipsPerAct` (default `1`) and `cardSkipsPerRun` (default `-1` = unlimited). Skip allowed iff `actRemaining > 0 AND runRemaining > 0` (treating `-1` as ∞). Default = chaos-by-default-mild (1 skip per act). Strict mode = `cardSkipsPerAct: 0`. Surfinite's call. |
| 9 | **In-game "skips remaining" label parented under `NRewardsScreen` near `_proceedButton`.** | New `CardSkipCounterLabel` Godot Label, attached during the skip-gate postfix, hidden when both limits are unlimited. Streamer-visible spatial co-location with the action. Cleans up on `_ExitTree`. |
| 10 | **Random fallback (zero votes received): random card, never skip.** | Same "play the game" semantics as B.1's Neow random fallback. Skip is never selected by the fallback even when `cardSkipsPerAct > 0`. |
| 11 | **Receipt format: name-only.** | `Vote: #0 Strike, #1 Defend, #2 Bash — 30s, type #N or N`. Matches B.1's Neow option-title format. Rarity / cost / type deliberately omitted to keep receipts readable in chat at-a-glance. |
| 12 | **Reroll, alternates, and other non-`SelectCard` buttons not patched.** | Streamer uses them freely. Vote starts when streamer clicks an actual card on whatever the current 3-card set is (post-reroll if rerolled). Edge case (vote in progress + reroll clicked): IsInstanceValid fails at resume → vote silently absorbs → streamer must click new card → new vote. |
| 13 | **No helper / base class extraction in B.2.1.** | Rule of Three. Re-evaluate after B.2.2. Copy-paste-modify `NeowBlessingVotePatch.cs` into `CardRewardVotePatch.cs` (~200 LOC duplicated). |
| 14 | **Use vanilla DevConsole for dev iteration, no custom debug patches.** | StS2 ships a full DevConsole at [`NDevConsole.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Debug/NDevConsole.cs); debug commands auto-unlock when `ModManager.IsRunningModded()` (line 167). `win`, `travel`, `act <n>`, `relic add <id>` cover all B.2 testing needs. Open with backtick (`` ` ``). No `B.2.0 dev tooling` sub-plan needed. |

## Architecture

```
src/
├── Ti/                                          ✅ unchanged from B.1
├── Game/                                        ✏️  extended in B.2.1
│   ├── Bootstrap/
│   │   └── ModSettings.cs                       ✏️  add `cardSkipsPerAct`, `cardSkipsPerRun` keys
│   ├── DecisionVotes/
│   │   ├── NeowBlessingVotePatch.cs             ✅ B.1 — unchanged
│   │   ├── CardRewardVotePatch.cs               🆕 B.2.1 — Harmony Prefix on NCardRewardSelectionScreen.SelectCard
│   │   └── CardRewardSkipGatePatch.cs           🆕 B.2.1 — Postfix on NRewardsScreen._Ready + RewardSkippedFrom; owns counter state
│   └── Ui/                                      🆕 B.2.1 — new sub-namespace; StS2-coupled UI
│       └── CardSkipCounterLabel.cs              🆕 B.2.1 — Godot Label parented under NRewardsScreen near proceed button
└── ModEntry.cs                                  ✏️  no functional change; existing Harmony.PatchAll() picks up the two new patches automatically

tests/
├── Bootstrap/
│   └── ModSettingsTests.cs                      ✏️  extend with ~6 tests for new keys
└── Game/
    └── DecisionVotes/
        └── CardRewardSkipGateTests.cs           🆕 B.2.1 — skip-counter logic in isolation (~10 tests)
```

**Legend**: 🆕 = new file in B.2.1; ✅ = already-shipped (B.1 / Plan A); ✏️ = existing file extended.

**Net new code estimate**: `CardRewardVotePatch` ~210 LOC (B.1 Neow patch + ~10 LOC for run-ID guard); `CardRewardSkipGatePatch` ~180 LOC (counter state + dual postfix + label management); `CardSkipCounterLabel` ~70 LOC; `ModSettings` additions ~30 LOC + ~40 LOC tests; `CardRewardSkipGateTests` ~150 LOC. Total ~490 LOC of source, ~190 LOC of tests.

**No new dependencies.** All required interfaces (`IMainThreadDispatcher`, `Voter.Default`, `VoteCoordinator`, `IClock`, `ITimerScheduler`) are already wired by B.1's `ModEntry`.

## `CardRewardVotePatch` (the vote)

Copy-paste-modified from `NeowBlessingVotePatch.cs`. Same five sections, same flags, same try/catch shape. Only the per-vote details change.

### Patch shape

```csharp
[HarmonyPatch(typeof(NCardRewardSelectionScreen), nameof(NCardRewardSelectionScreen.SelectCard))]
internal static class CardRewardVotePatch
{
    // Match B.1's NeowBlessingVotePatch: int + Interlocked, not bool, for atomic transitions.
    private static int _voteInProgress;
    private static int _resumeInProgress;
    private static int _multiplayerWarnFired;

    static bool Prepare(MethodBase? original) { /* field+method shape checks */ }

    static bool Prefix(NCardRewardSelectionScreen __instance, NCardHolder cardHolder)
    {
        // — guard early returns —
        // — Players.Count > 1 bail —
        // — chat-readiness gate (chat.State == ConnectedReadWrite) —
        // — capture run ID —
        // — extract card list from screen —
        // — build vote options (name-only labels) —
        // — set _voteInProgress = true —
        // — fire-and-forget: _ = HandleVoteAsync(__instance, cardList, runIdAtStart, playerClickIndex) —
        // — return false (suspend) —
    }

    private static async Task HandleVoteAsync(...) { /* try-catch with post-Start fallback */ }
    private static void ResumeOnMainThread(...) { /* IsInstanceValid + run-ID + bounds checks */ }
}
```

### Differences from `NeowBlessingVotePatch`

1. **Patch target** is `SelectCard` (single-arg `NCardHolder`) instead of `OptionButtonClicked` (two-arg `EventOption, int`).
2. **`Prepare` validation** checks `NCardRewardSelectionScreen` field shape: `_options : IReadOnlyList<CardCreationResult>` exists, `SelectCard(NCardHolder)` method exists with correct signature. If shape changed, `Prepare` returns false → patch silently skips → mod degrades to vanilla card reward.
3. **Option enumeration** comes from the `_options` field on the screen via reflection (verified field name in `Prepare`). Each `CardCreationResult.Card.Name.GetText()` provides the chat-receipt label. Option count = `_options.Count` (typically 3, can be 1-N for special sources).
4. **`playerClickIndex` derivation**: find the clicked `cardHolder` in the screen's card-holder list (also reflected, verified in `Prepare`). Use the index for fallback resume.
5. **Resume action**: `dispatcher.Post(() => __instance.SelectCard(winningCardHolder))` — re-call the original method with the chat-chosen card holder. The `IsInstanceValid` check covers screen-dismissed; the run-ID check covers run-abandoned; the bounds check covers cards-replaced-by-reroll.
6. **`DisableEventOptions` analogue**: `NCardRewardSelectionScreen` doesn't expose an equivalent. Re-entry guard (`_voteInProgress`) suffices — repeated card clicks during the vote return false from the prefix and don't start a new vote. Documented as an intentional omission; if streamer rapidly clicks cards during vote, only the first click triggers the vote (consistent with Neow behaviour).
7. **Run-ID guard**: capture `RunManager.Instance.DebugOnlyGetState()?.Id` at vote-start; in `ResumeOnMainThread`, compare against current; skip resume if mismatch (logs at Info, not Warn). Closes the resume-after-abandon race.

### Lifecycle: streamer dies / abandons mid-vote

Same as B.1: vote runs to normal close in background (we can't cancel a `VoteSession` from outside Plan A's API). At resume time, run-ID guard fires → resume aborts → no crash, no spurious card-add into a dead run. Documented in notes/06.

### Lifecycle: streamer triggers reroll mid-vote

Streamer clicks card (vote starts) → streamer clicks reroll → vanilla discards old cards, generates new ones → screen still alive but `_options` list is replaced. At resume time: `IsInstanceValid` passes (screen alive); bounds check on `winningCardHolder` against current card-holder list fails (the holder reference is now a freed/orphaned object). Resume aborts. Streamer must click a card on the new set → new vote starts. **Documented edge case**; no special handling needed.

## `CardRewardSkipGatePatch` (the gate)

Owns the skip-counter state and the two postfix patches that maintain it.

### State

```csharp
internal static class CardRewardSkipGatePatch
{
    private static int _actSkipsUsed;
    private static int _runSkipsUsed;
    private static int? _lastSeenActIndex;       // null = no act seen yet
    private static Guid? _lastSeenRunId;         // null = no run seen yet (replace with actual run-id type if not Guid)
    private static CardSkipCounterLabel? _activeLabel;
    // ...
}
```

### Two postfix patches (same class, two `[HarmonyPatch]` static-method targets via separate inner classes or attributes)

#### `NRewardsScreen._Ready` Postfix

```csharp
public static void Postfix(NRewardsScreen __instance)
{
    try
    {
        // — Players.Count > 1 bail (no skip gate in MP) —

        var runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null) return;

        // — Run-change detection: if runState.Id != _lastSeenRunId, reset _runSkipsUsed = 0 + _actSkipsUsed = 0 —
        // — Act-change detection: if runState.CurrentActIndex != _lastSeenActIndex, reset _actSkipsUsed = 0 —
        // — Update _lastSeenRunId, _lastSeenActIndex —

        if (!HasUnclaimedCardReward(__instance)) return;

        var settings = ModEntryState.Settings; // injected via static accessor or constructor-equivalent
        int actLimit = settings.CardSkipsPerAct;
        int runLimit = settings.CardSkipsPerRun;
        bool actExhausted = actLimit >= 0 && _actSkipsUsed >= actLimit;
        bool runExhausted = runLimit >= 0 && _runSkipsUsed >= runLimit;

        if (actExhausted || runExhausted)
        {
            __instance.DisallowSkipping();
        }

        // — Attach CardSkipCounterLabel to __instance near _proceedButton —
        AttachOrUpdateLabel(__instance, actLimit, runLimit);
    }
    catch (Exception ex)
    {
        TiLog.Error("CardRewardSkipGatePatch._Ready postfix failed", ex);
        // — fail-open: don't break vanilla rewards if our gate logic throws —
    }
}
```

#### `NRewardsScreen.RewardSkippedFrom` Postfix

```csharp
public static void Postfix(NRewardsScreen __instance, Control button)
{
    try
    {
        if (!IsCardRewardButton(button)) return;

        var settings = ModEntryState.Settings;
        Interlocked.Increment(ref _actSkipsUsed);
        Interlocked.Increment(ref _runSkipsUsed);

        // — Send chat receipt via Voter.Default.Coordinator.Chat (or equivalent accessor) —
        SendSkipReceipt(settings.CardSkipsPerAct, settings.CardSkipsPerRun);

        // — Refresh label —
        if (_activeLabel != null) _activeLabel.UpdateText(...);
    }
    catch (Exception ex)
    {
        TiLog.Error("CardRewardSkipGatePatch.RewardSkippedFrom postfix failed", ex);
    }
}
```

### `HasUnclaimedCardReward` / `IsCardRewardButton`

Both inspect the rewards-screen's reward buttons via reflection on the `_rewardButtons` field (verified in a `Prepare` method). For each button, check whether it's an `NRewardButton` whose underlying `Reward` is a `CardReward` (verified type from [`decompiled/.../Rewards/CardReward.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/Rewards/CardReward.cs)). Reflection failure → log Warn → return false (assume no card reward → don't gate → fail-open).

### Act-change detection

`RunState` exposes the act list via `runState.Acts` (verified at [`ActConsoleCmd.Process`](../../../decompiled/sts2/MegaCrit/sts2/Core/DevConsole/ConsoleCommands/ActConsoleCmd.cs)). The implementer must identify the current-act index — likely candidates include `Acts.Count - 1`, a `CurrentAct` / `CurrentActIndex` property if one exists, or a derived value from `runState.CurrentRoom`'s parent act. **`Prepare` MUST verify the chosen access pattern and skip patch registration if the field/method shape doesn't match expectations.** On every `_Ready` postfix, compare the resolved current-act value against `_lastSeenActIndex`; if changed, reset `_actSkipsUsed = 0`. Implicit: act change is detected at the next rewards screen, not at the moment the act actually changes. Acceptable — by the next rewards screen the budget is fresh.

### Run-change detection

`RunState` has a stable run identifier (likely `Id`, but verify against the actual field in `Prepare`). Compare against `_lastSeenRunId`; on mismatch, reset both counters. Same "detected at next rewards screen" semantics.

### Receipt format note

The skip receipt is a new receipt type (not currently in `EnglishReceipts`). Implementer chooses: (a) add a `SkipReceipt(int actUsed, int actLimit, int runUsed, int runLimit)` static helper to `EnglishReceipts`, or (b) inline the format string in `CardRewardSkipGatePatch.SendSkipReceipt`. Option (a) preferred for consistency with how vote receipts are formatted. Format: `Streamer skipped a card reward (<actUsed>/<actLimit> act, <runUsed>/<runLimit> run)`, rendering `-1` as `∞`.

## `CardSkipCounterLabel` (the UI)

Godot `Label` (not `RichTextLabel` — no formatting needed; can upgrade to `RichTextLabel` if we want colour later).

### Lifecycle

- Created and parented in `CardRewardSkipGatePatch._Ready` postfix when the gate first sees the rewards screen.
- Position: anchored relative to `_proceedButton`'s position (offset above-and-left). Use Godot's anchor / layout system; if the proceed button moves between screens, the label follows.
- Hidden if `cardSkipsPerAct == -1 AND cardSkipsPerRun == -1` (no point showing infinity/infinity).
- Updated text: `Card skips: <actRemaining>/<actLimit> act, <runRemaining>/<runLimit> run` — render `-1` as `∞`.
- Cleaned up automatically when the rewards screen is freed (the label is parented under `NRewardsScreen` so it dies with the screen).

### Failure modes

- Proceed button not found (UI structure changed): label is parented under the rewards screen root instead at a fixed offset; logs Warn. Gate logic still works.
- Failed to attach label (Godot exception): logs Error, gate logic continues. Label is non-essential UX, gate is essential safety.

## `ModSettings` extensions

Two new keys with documented defaults and validation:

```jsonc
{
  "schemaVersion": 1,
  "channel": "...",
  "username": "...",
  "oauthToken": "...",
  // — new in B.2.1 —
  "cardSkipsPerAct": 1,    // default 1; -1 = unlimited; 0 = strict
  "cardSkipsPerRun": -1    // default -1 (unlimited); 0 = strict; positive = cap
}
```

(Existing B.1 keys preserved as-is. B.1 hardcodes vote duration at 30s in the Neow patch; B.2.1 follows suit.)

### Parsing rules

- Missing key → use default (matches B.1's missing-key behaviour for `voteDuration`).
- Non-integer value → warning + use default. Non-fatal.
- Value < -1 → warning, clamp to -1.
- Both keys are independent; no cross-validation.

### Tests to add to `ModSettingsTests`

- `CardSkipsPerActMissingUsesDefault` (default = 1).
- `CardSkipsPerRunMissingUsesDefault` (default = -1).
- `CardSkipsPerAct_InvalidValue_WarnsAndUsesDefault`.
- `CardSkipsPerRun_NegativeOtherThanMinusOne_ClampsToMinusOne`.
- `CardSkipsPerAct_Zero_IsStrict` (parses successfully, no warning; value = 0).
- `CardSkipsPerRun_PositiveValue_Parses` (e.g., 5).

## Failure modes & degradation

Inherits B.1's "fail soft, degrade to vanilla" stance. New failure modes specific to B.2.1:

| # | Failure mode | Behaviour |
|---|---|---|
| 1 | `CardRewardVotePatch.Prepare` fails (screen field/method shape changed) | Vote patch silently skips registration. Card rewards play vanilla (no chat vote, but no crash). Skip gate still works (it's a separate patch). |
| 2 | `CardRewardSkipGatePatch.Prepare` fails | Skip gate skips registration. Card vote still works; streamer can bypass via Proceed (vanilla behaviour, no skip budget enforced). |
| 3 | `_Ready` postfix throws | Logs Error, vanilla `_Ready` already completed → rewards screen still functional. No gate this round. |
| 4 | `RewardSkippedFrom` postfix throws | Logs Error, skip already counted by vanilla. Counter not decremented → next gate evaluation may be slightly stricter than intended. Acceptable. |
| 5 | Reflection failure on `_options` / `_rewardButtons` field | Logs Warn, returns "no card reward" / "can't determine cards" → fail-open (vote doesn't start, gate doesn't activate). Vanilla card flow runs. |
| 6 | Run-ID guard fires (run abandoned mid-vote) | Logs Info ("Resume aborted: run changed during vote"), no resume. No crash. |
| 7 | Reroll mid-vote (cards replaced before resume) | `IsInstanceValid` or bounds check fails → resume aborts → streamer clicks new card → new vote. No crash. |
| 8 | Settings file completely missing | B.1's behaviour applies — mod loads silently with no chat capability. Skip gate still attaches but with default settings (so `cardSkipsPerAct: 1, cardSkipsPerRun: -1`); since chat isn't connected, no vote starts → streamer can claim or skip cards normally; gate-disable behaviour is irrelevant without a vote to gate. |

## Acceptance gate (operator-validation, runs after unit tests pass)

Identical structure to B.1's. Each step is a manual playthrough; mod is considered B.2.1-ready only when all five are green.

- **Step 0 — Vanilla baseline.** Settings present but with valid B.1 keys only (no `cardSkipsPer*`): mod loads with defaults. Card rewards present: vote runs (Step 1 path); Proceed gate uses defaults (1/act, ∞/run). Skip-counter label visible. **No regressions in B.1 features** (Neow vote still works; chat connect-once receipt still fires).
- **Step 1 — Happy path vote (3 successful runs):**
  - chat votes for a card via `#0`/`#1`/`#2`, winning card claimed via dispatcher.Post resume
  - latest-wins on multi-vote-from-one-user
  - both `#N` and bare `N` accepted
  - close receipt fires with correct card name
  - VoteTallyLabel (top-right) shows tally during vote
  - Skip-counter label updates correctly when cards are claimed (no skip used)
- **Step 2 — Skip used.** With `cardSkipsPerAct: 1`: open rewards screen → click Proceed without claiming card → skip allowed → chat receipt fires `Streamer skipped a card reward (1/1 act, 0/∞ run)` → counter label updates → next combat: rewards screen opens with Proceed disabled (must claim) → click card → vote runs → claim → Proceed enabled.
- **Step 3 — Skip blocked.** With `cardSkipsPerAct: 0` from start: rewards screen opens, Proceed button visibly disabled (vanilla "Skip" button greyed). Hover shows vanilla "claim rewards" message. Streamer must click card → vote runs → claim → Proceed enabled. No way to bypass.
- **Step 4 — Counter resets.** Use `act 2` console command to jump acts → next rewards screen: counter label resets to `1/1 act` → skip usable again. Same with starting a new run via menu.
- **Step 5 — Edge cases.** Mid-vote run abandon (run-ID guard fires, vote silently absorbed). Mid-vote reroll if a relic enables it (IsInstanceValid or bounds check fails, vote silently absorbed, streamer clicks new card → new vote). Streamer escape (via menu) mid-vote (vote runs to normal close in background; resume drops; no crash). Streamer rapidly clicks multiple cards during vote (only first triggers vote; subsequent clicks no-op via `_voteInProgress` guard).

## Open questions

None blocking. Two soft questions for the meta-review pass:

1. **Should the skip-counter label show in multiplayer co-op?** Currently bailed out (`Players.Count > 1` bail in skip gate). MP support is a v0.2 concern overall; consistent with B.1's MP bail. Probably fine.
2. **Should reroll-mid-vote send a chat receipt?** Currently silent (vote just absorbs). Could send `(vote cancelled — streamer rerolled)` for transparency. Lean no for v0.1 — adds complexity for an edge case most streamers won't encounter.

## Cross-references

- [`docs/superpowers/specs/2026-05-09-plan-b-1-vertical-slice-design-v3.md`](./2026-05-09-plan-b-1-vertical-slice-design-v3.md) — B.1 spec; suspend-and-resume pattern source-of-truth.
- [`notes/06-followups-and-deferred.md`](../../../notes/06-followups-and-deferred.md) — B.1 completion findings; run-ID guard origin; relic curation deferral notes.
- [`decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/CardSelection/NCardRewardSelectionScreen.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/CardSelection/NCardRewardSelectionScreen.cs) — vote patch target.
- [`decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/NRewardsScreen.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/NRewardsScreen.cs) — skip gate target; `DisallowSkipping`/`RewardSkippedFrom`/`_skipDisallowed` mechanism source.
- [`decompiled/sts2/MegaCrit/sts2/Core/AutoSlay/Handlers/Screens/CardRewardScreenHandler.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/AutoSlay/Handlers/Screens/CardRewardScreenHandler.cs) — confirms the `EmitSignal(Pressed)` → `SelectCard` call path used by the vote patch.
- [`decompiled/sts2/MegaCrit/sts2/Core/Nodes/Debug/NDevConsole.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Debug/NDevConsole.cs) — vanilla DevConsole; `ModManager.IsRunningModded()` unlock at line 167.
