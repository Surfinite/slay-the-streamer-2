# Plan B.2.1 — card reward vote (design v3)

**Date**: 2026-05-10
**Status**: Draft v3 — post Optional-Enhancements pick (final pre-implementation)
**Predecessor**: [`2026-05-10-plan-b-2-1-card-reward-vote-design-v2.md`](./2026-05-10-plan-b-2-1-card-reward-vote-design-v2.md). Meta-review at [`META-REVIEW-2026-05-10-plan-b-2-1-card-reward-vote-design.md`](./META-REVIEW-2026-05-10-plan-b-2-1-card-reward-vote-design.md).
**Scope**: Second sub-plan of Plan B. Adds the **card reward** vote — the next "click 1-of-N option-button screen" decision after Neow. Same suspend-and-resume pattern as B.1, copy-paste-modified into a new patch class. Adds two new pieces of Game-side machinery: a **Proceed-skip gate** that prevents the streamer from bypassing chat by clicking through rewards unclaimed (with per-act skip budget), and an in-game **skip-counter label** so the streamer can see how many skips they have left.

The remaining three v0.1 votes (boss relic, map path, act-boss) and the in-game settings panel are explicit non-goals — they belong to B.2.2, B.2.3, B.2.4, and B.3.

> **Architectural hard constraint** (carried forward from B.1): every Harmony prefix that triggers a vote MUST use the **suspend-and-resume** pattern. Prefix returns immediately (`false` to skip original, after firing `_ = HandleVoteAsync(...)` as fire-and-forget). The async handler runs the vote, then re-invokes the chosen game-state mutation via `dispatcher.Post(...)`. **No blocking the Godot main thread on `AwaitWinnerAsync().GetAwaiter().GetResult()`, ever.**

## Author's note on v3 changes

v2 incorporated all 14 Must-do + 15 Should-do meta-review items. v3 applies Surfinite's Optional-Enhancements picks plus one important design clarification:

**Applied (5 of 10)**:
1. **#1: Drop `cardSkipsPerRun`** — clarification: original brainstorming intent was one-or-the-other mode-choice (per-act OR per-run), not both stacked. v2 had them stacked AND'd. v3 simplifies to single `cardSkipsPerAct` knob; per-run is removed entirely. ~30 LOC reduction; simpler label, simpler receipt, simpler tests.
2. **#3: `SkipBudgetTracker` extraction** — pure logic class for testability. Skip-gate postfix becomes a thin shim. NOT the deferred suspend/resume helper — different concern (pure budget arithmetic).
3. **#5: `RichTextLabel` for `CardSkipCounterLabel` from day one** — leaves design space open for colour without a node-type swap later.
4. **#9: notes/06 entry listing reflected sts2.dll members** — single update point when MegaCrit ships breaking patches.
5. **#10: TiLog `[SlayTheStreamer2]` prefix convention** — every log line tagged for greppability. Applied retroactively to B.1 patches as part of B.2.1 work.

**Declined (1 of 10)**:
- **#2: Extend `VoteTallyLabel` instead of separate label** — Surfinite's viewer-readability priority and stated canvas-position requirement override the engineering simplicity. Separate label at proceed-button position stands.

**Other Optional Enhancements not picked** (#4, #6, #7, #8): deferred. #6 (composite run-id fingerprint as fallback) is conditional — implement only if Step 0 reveals `DebugOnlyGetState` returns null in production.

**One important design clarification (Mode B)**:

Surfinite's original brainstorming framing was: *"if they open up the reward to look, then chat should definitely get to pick, they can only skip without looking, and only X number of times per floor."* That implies **Mode A: looking forfeits skip** — once the streamer opens the card sub-screen, the budget no longer applies and chat must pick.

The v2 spec (carried forward to v3) actually implements **Mode B: skip allowed within budget regardless of whether streamer looked**. The streamer can preview cards, back out, then click Proceed-as-skip and have it count.

This is a **deliberate deviation** from the original intent, picked by Surfinite during the v3 review with the trade-off explicit. Rationale: Mode A adds a 4th Harmony patch (`NCardRewardSelectionScreen._Ready` postfix → calls parent's `DisallowSkipping()`) and an extra acceptance step. Mode B is simpler and the budget already bounds the bypass quantity. **Recorded as Decision 18** so future readers (and future-Surfinite) understand the choice was knowing.

## Goals

1. **Ship the card reward vote end-to-end** — chat votes on which of the (typically 3) cards the streamer adds; suspend-and-resume copy of B.1's pattern; receipts and tally label work identically; Twitch backlog-on-JOIN behaviour from B.1 is reused untouched.
2. **Prevent streamer-side bypass via Proceed.** With default settings, every card reward must be either claimed (chat picks) or counted against a finite per-act skip budget. The streamer cannot silently skip every card to escape chat agency. <!-- CHANGED v3: dropped per-run mention -->
3. **Make the skip budget visible** — a small in-game label near the Proceed button shows `Card skips: <remaining>/<limit> act`. Streamer always knows where they stand. <!-- CHANGED v3: simplified format -->
4. **Fail soft on every new failure mode** added by B.2.1 — bad settings keys, missing rewards-screen UI nodes, run/act detection edge cases, reroll-mid-vote, run-abandon-mid-vote. Mod stays loaded; game keeps running; vote silently absorbs when resume target is gone.
5. **De-risk B.2.2 boss relic and B.2.3 map path** by making the second working example of suspend-and-resume real. After B.2.1 ships, B.2.2's plan will see what's actually shared (likely: ~80% of the per-patch boilerplate) and a helper extraction can be planned with confidence.
6. **Apply the run-ID guard to `NeowBlessingVotePatch` too** so B.2.2 inherits a consistent template.

## Non-goals

- B.2.2 boss relic, B.2.3 map path, B.2.4 in-game settings UI, B.3 act-boss.
- **Helper / base-class extraction** for suspend-and-resume.
- **Per-run skip budget** (`cardSkipsPerRun`). v3 simplifies to per-act only. <!-- CHANGED v3 -->
- **Mode A (looking forfeits skip)**. v3 explicitly chooses Mode B (see Decision 18 + Author's note). <!-- NEW v3 -->
- **Patching reroll** or non-`SelectCard` buttons on `NCardRewardSelectionScreen`. Reroll-mid-vote sends a chat receipt; no patch on reroll itself.
- **Patching `NRewardsScreen.OnProceedButtonPressed` directly.** We use `DisallowSkipping()` instead.
- **Per-relic curation.** Deferred to v0.2.
- **Settings-driven vote duration.** B.1's hardcoded 30s preserved in B.2.1; B.2.4 territory.
- **BBCode stripping in receipts.**
- **Multiplayer co-op support** — both vote patch and skip gate bail on `Players.Count > 1`.
- **Localised receipts.**
- **In-game error toasts.**
- **Persisting skip counters across save/quit/reload.** v0.1 known limit.
- **Skip-without-looking detection** (Mode A precision). Not implemented; see Decision 18.
- **`AllowSkipping()` re-enable after card claim.** Vanilla self-corrects in most cases; operator validation Step 5 confirms.
- **Streamer-configurable per-vote receipts.**

## Decisions

| # | Decision | Rationale |
|---|---|---|
| 1 | **B.2.1 covers card reward only.** | Single-vote vertical slice. |
| 2 | **Patch target for vote: `NCardRewardSelectionScreen.SelectCard(NCardHolder)` Prefix.** | Verified from decompiled source; AutoSlay handler confirms call path. |
| 3 | **Patch target for skip gate: `NRewardsScreen._Ready` Postfix.** | After vanilla builds the rewards container. **Implementation note**: `_Ready` is a Godot lifecycle method invoked via native-to-managed interop. Implementation MUST verify Harmony patches it correctly (Task 1: write no-op `_Ready` postfix that just logs; confirm it fires; fall back to `_EnterTree` or `_Notification` postfix if `_Ready` patch is unreliable). |
| 4 | **Patch target for skip-detect: `NRewardsScreen.RewardSkippedFrom(Control)` Postfix.** | Vanilla calls this when a reward button is skipped via Proceed. Postfix detects card-reward skips, decrements counter, sends chat receipt, refreshes label, **and re-evaluates gate** if THIS skip exhausted budget while other unclaimed card rewards remain on the same screen. |
| 5 | **Suspend-and-resume pattern reused verbatim from B.1.** | Same two-flag re-entry guard, same post-Start fallback, same `IsInstanceValid` resume check, same `dispatcher.Post(...)` resume invocation. Plus `VoteTallyLabel.AttachTo(session)` explicitly posted at vote start. |
| 6 | **Run-ID guard added to resume path of BOTH vote patches** (Card AND Neow). | Capture `RunManager.Instance.DebugOnlyGetState()?.Id` (or equivalent — pin in `Prepare`) at vote start, compare at resume, skip resume if changed. **Implementation MUST verify `DebugOnlyGetState()` returns a non-null state object with a stable Id property in modded production builds**. Pin exact field name + type in `Prepare`; if shape mismatch, log Warn and disable the run-ID check (vote still works without guard — fail-open). Run-ID mismatch at resume logged at **Warn**. |
| 7 | **Skip is never a chat-vote option.** | Chat-vs-streamer asymmetry. Vote options = current cards on screen, dynamic count (1 to N). |
| 8 | **Skip budget: single per-act cap.** <!-- CHANGED v3 from dual to single --> | `cardSkipsPerAct` (default `1`). Skip allowed iff `cardSkipsPerAct < 0 OR _actSkipsUsed < cardSkipsPerAct` (treat `-1` as ∞). Default = chaos-by-default-mild (1 skip per act). Strict mode = `cardSkipsPerAct: 0`. Permissive = `cardSkipsPerAct: -1` (unlimited). |
| 9 | **In-game "skips remaining" label parented under `NRewardsScreen` near `_proceedButton`.** | New `CardSkipCounterLabel` Godot **`RichTextLabel`** <!-- CHANGED v3: was Label, now RichTextLabel for future colour --> attached during the skip-gate postfix. Hidden when `cardSkipsPerAct == -1`. Cleans up when screen frees (Godot lifecycle), AND the static `_activeLabel` reference is nulled in an `_ExitTree` postfix on `NRewardsScreen` (belt-and-suspenders). All consumer sites guard with `Godot.GodotObject.IsInstanceValid(_activeLabel)` before use. |
| 10 | **Random fallback (zero votes received): random card, never skip.** | "Play the game" semantics. |
| 11 | **Receipt format: name-only.** | `Vote: #0 Strike, #1 Defend, #2 Bash — 30s, type #N or N`. |
| 12 | **Reroll, alternates, and other non-`SelectCard` buttons not patched.** | Streamer uses them freely. Vote starts when streamer clicks an actual card. If streamer clicks reroll mid-vote, chat receives a `vote cancelled — streamer rerolled` receipt. |
| 13 | **No helper / base class extraction in B.2.1.** | Rule of Three. Re-evaluate after B.2.2. |
| 14 | **Use vanilla DevConsole for dev iteration, no custom debug patches.** | DevConsole auto-unlocks when `ModManager.IsRunningModded()`. |
| 15 | **`ModEntry.Settings` static accessor for patches.** | `internal static SettingsResult? Settings { get; private set; }` on `ModEntry.cs`, set after successful `ModSettings.Load()`. |
| 16 | **Skip receipt formatting inlined in `CardRewardSkipGatePatch`.** | Game-domain knowledge stays in Game/. Do not modify `Ti/Voting/EnglishReceipts`. |
| 17 | **Skip counters use plain `++`, not `Interlocked`.** | All `NRewardsScreen` callbacks run on Godot's main thread. Vote patch's `Interlocked.CompareExchange` for `_voteInProgress` stays. |
| 18 | **Mode B (skip allowed regardless of look) chosen over Mode A (looking forfeits skip).** <!-- NEW v3 --> | Surfinite's original brainstorming intent was Mode A. v3 deliberately picks Mode B for: (a) simpler implementation (no 4th Harmony patch on `NCardRewardSelectionScreen._Ready`); (b) the per-act budget already bounds the bypass quantity; (c) UX consistency (one mental model: "I have N skips per act" regardless of whether the streamer previewed). Trade-off accepted: streamer can preview cards then back out and skip, which weakens but doesn't eliminate the chat-vs-streamer fairness contract. If operator-validation reveals streamers exploiting the preview-and-skip loop, B.2.2 polish can add Mode A as a settings option. |
| 19 | **Pure `SkipBudgetTracker` class for testability.** <!-- NEW v3 --> | Pull counter logic into a Godot-free pure-logic class with `ObserveRunAndAct(runId, actIndex)`, `IsSkipAllowed(int actLimit)`, `RecordSkip()`, `Snapshot(int actLimit)`. Skip-gate postfix becomes a thin Harmony shim. Tests target the tracker directly. NOT the deferred suspend/resume helper — different concern. |
| 20 | **TiLog `[SlayTheStreamer2]` prefix on all log calls.** <!-- NEW v3 --> | Every log line tagged for greppability. Applied retroactively to B.1 patches as part of B.2.1 work (~10 LOC of TiLog call updates). |

## Architecture

```
src/
├── Ti/                                          ✅ unchanged from B.1; NO modifications in B.2.1
├── Game/                                        ✏️  extended in B.2.1
│   ├── Bootstrap/
│   │   └── ModSettings.cs                       ✏️  add `cardSkipsPerAct` key  <!-- CHANGED v3: dropped per-run -->
│   ├── DecisionVotes/
│   │   ├── NeowBlessingVotePatch.cs             ✏️  add run-ID guard + TiLog prefix tag  <!-- CHANGED v3 -->
│   │   ├── CardRewardVotePatch.cs               🆕 B.2.1 — Harmony Prefix on NCardRewardSelectionScreen.SelectCard
│   │   ├── CardRewardSkipGatePatch.cs           🆕 B.2.1 — Postfix on NRewardsScreen._Ready + RewardSkippedFrom + _ExitTree
│   │   └── SkipBudgetTracker.cs                 🆕 B.2.1 — pure logic class for budget arithmetic  <!-- NEW v3 -->
│   └── Ui/                                      🆕 B.2.1 — new sub-namespace; StS2-coupled UI
│       └── CardSkipCounterLabel.cs              🆕 B.2.1 — Godot RichTextLabel parented under NRewardsScreen near proceed button  <!-- CHANGED v3: RichTextLabel -->
└── ModEntry.cs                                  ✏️  add static Settings accessor + retro-apply TiLog prefix  <!-- CHANGED v3 -->

tests/
├── Bootstrap/
│   └── ModSettingsTests.cs                      ✏️  extend with ~5 tests for `cardSkipsPerAct`  <!-- CHANGED v3: simpler -->
└── Game/
    └── DecisionVotes/
        ├── SkipBudgetTrackerTests.cs            🆕 B.2.1 — pure budget logic (~10 tests)  <!-- NEW v3 -->
        └── CardRewardSkipGateTests.cs           🆕 B.2.1 — Harmony shim integration (~5 tests; mostly verification that postfix invokes tracker correctly)  <!-- CHANGED v3: shrunk -->
```

**Net new code estimate**: `CardRewardVotePatch` ~230 LOC; `CardRewardSkipGatePatch` ~140 LOC <!-- CHANGED v3: shrunk because tracker extracted -->; `SkipBudgetTracker` ~80 LOC <!-- NEW v3 -->; `CardSkipCounterLabel` ~80 LOC; `NeowBlessingVotePatch` ~10 LOC additions; `ModSettings` additions ~20 LOC + ~30 LOC tests; `SkipBudgetTrackerTests` ~150 LOC; `CardRewardSkipGateTests` ~80 LOC; `ModEntry` additions ~10 LOC + retro-TiLog-prefix updates ~5 LOC. Total ~570 LOC of source, ~260 LOC of tests.

## `CardRewardVotePatch` (the vote)

Copy-paste-modified from `NeowBlessingVotePatch.cs`. Same shape as v2 — see [v2 spec](./2026-05-10-plan-b-2-1-card-reward-vote-design-v2.md#cardrewardvotepatch-the-vote) for the full patch sketch. **No changes in v3 to this section** beyond TiLog prefix updates (every `TiLog.X(...)` call now starts with `[SlayTheStreamer2][card-vote]` or similar).

### Reflected members verified by `Prepare` (carried from v2)

- Type `NCardRewardSelectionScreen` exists; method `SelectCard(NCardHolder)` exists with exact signature.
- Field `_options` exists, type assignable to `IReadOnlyList<CardCreationResult>`.
- The card-holder collection field exists; pin exact accessor in `Prepare` and validate count matches `_options.Count`.
- `RunManager.Instance` reachable; `DebugOnlyGetState()` exists; result type has `Id` property of type `Guid` (or actual run-id type — pin in `Prepare`); also exposes `Players.Count`.

If ANY check fails: log Error, return false from `Prepare`, patch silently skips. Vote degrades to vanilla.

## `CardRewardSkipGatePatch` (the gate, refactored)

v3 thins this to a pure Harmony shim that delegates all budget logic to `SkipBudgetTracker`. The patch's responsibility shrinks to: detect rewards-screen lifecycle events, look up settings, call into the tracker, render UI updates.

### Reflected members verified by `Prepare`

(Unchanged from v2.)

- Type `NRewardsScreen` exists; `_rewardButtons` field; `_skippedRewardButtons` field; `_proceedButton` field; `DisallowSkipping()` method exists, public, parameterless.
- Vanilla `RewardCollectedFrom(Control)` removes button from `_rewardButtons` (verified from decompiled source).
- Type `NRewardButton` exists; has accessor for underlying `Reward` of type `CardReward`.
- `RunManager.Instance.DebugOnlyGetState()` returns object with `Players.Count` and act access.

### State (just the tracker reference + label) <!-- CHANGED v3 -->

```csharp
internal static class CardRewardSkipGatePatch
{
    private static readonly SkipBudgetTracker _tracker = new();
    private static CardSkipCounterLabel? _activeLabel;
}
```

All counter / run-id / act-id state lives inside `_tracker`.

### Postfix patches

#### `NRewardsScreen._Ready` Postfix

```csharp
public static void Postfix(NRewardsScreen __instance)
{
    try {
        // — Players.Count > 1 bail (no skip gate in MP) —

        var runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null) return;

        // — Tracker handles run/act change detection internally —
        _tracker.ObserveRunAndAct(runState.Id, GetCurrentActIndex(runState));

        if (!HasUnclaimedCardReward(__instance)) return;

        if (ModEntry.Settings is not SettingsResult.Success success) return;
        var settings = success.Settings;

        if (!_tracker.IsSkipAllowed(settings.CardSkipsPerAct)) {
            __instance.DisallowSkipping();
        }

        AttachOrUpdateLabel(__instance, settings.CardSkipsPerAct);
    } catch (Exception ex) {
        TiLog.Error("[SlayTheStreamer2][card-skip-gate] _Ready postfix failed", ex);   // CHANGED v3: prefix
    }
}
```

#### `NRewardsScreen.RewardSkippedFrom` Postfix

```csharp
public static void Postfix(NRewardsScreen __instance, Control button)
{
    try {
        if (!IsCardRewardButton(button)) return;

        _tracker.RecordSkip();

        if (ModEntry.Settings is SettingsResult.Success success) {
            var settings = success.Settings;
            SendSkipReceipt(settings.CardSkipsPerAct);

            // Multi-card-reward gate re-evaluation (per Reviewers 1, 6 in v2)
            if (!_tracker.IsSkipAllowed(settings.CardSkipsPerAct) &&
                HasUnclaimedCardReward(__instance)) {
                __instance.DisallowSkipping();
            }
        }

        if (_activeLabel != null && Godot.GodotObject.IsInstanceValid(_activeLabel)) {
            _activeLabel.UpdateText(_tracker.Snapshot(...));
        }
    } catch (Exception ex) {
        TiLog.Error("[SlayTheStreamer2][card-skip-gate] RewardSkippedFrom postfix failed", ex);   // CHANGED v3
    }
}
```

#### `NRewardsScreen._ExitTree` Postfix

```csharp
public static void Postfix(NRewardsScreen __instance) {
    _activeLabel = null;   // belt-and-suspenders for dangling-reference bug
}
```

### Skip receipt formatter (inline, simplified for single per-act knob) <!-- CHANGED v3 -->

```csharp
private static string FormatSkipReceipt(int actUsed, int actLimit)
{
    string limitPart = actLimit < 0 ? "unlimited act" : $"{actUsed}/{actLimit} act";
    return $"Streamer skipped a card reward ({limitPart})";
}

private static void SendSkipReceipt(int actLimit)
{
    var coordinator = Voter.Default;
    if (coordinator?.Chat?.State != ChatConnectionState.ConnectedReadWrite) return;

    string text = FormatSkipReceipt(_tracker.ActSkipsUsed, actLimit);
    _ = coordinator.Chat.SendMessageAsync(text, OutgoingMessagePriority.Normal);
}
```

Receipt examples:
- Default config: `Streamer skipped a card reward (1/1 act)`
- `cardSkipsPerAct: 3`: `Streamer skipped a card reward (1/3 act)` ... `(2/3 act)` ... `(3/3 act)`
- `cardSkipsPerAct: -1`: `Streamer skipped a card reward (unlimited act)`

### `HasUnclaimedCardReward` / `IsCardRewardButton`

(Unchanged from v2 — see v2 spec for the reflection details.)

## `SkipBudgetTracker` (new pure-logic class) <!-- NEW v3 -->

Pure C# class, no Godot or Harmony references. Owns all counter/run/act state. Tested in isolation.

```csharp
namespace SlayTheStreamer2.Game.DecisionVotes;

internal sealed class SkipBudgetTracker
{
    // Main-thread-only; plain state, no Interlocked.
    private int _actSkipsUsed;
    private int? _lastSeenActIndex;
    private Guid? _lastSeenRunId;       // type pinned in Prepare; assume Guid for v3 prose

    public int ActSkipsUsed => _actSkipsUsed;

    /// <summary>
    /// Called once per `NRewardsScreen._Ready`. Resets the act counter if the act
    /// or run changed since last observation. Tracks lazy detection — by the next
    /// rewards screen, the budget is fresh.
    /// </summary>
    public void ObserveRunAndAct(Guid? runId, int? actIndex)
    {
        if (runId.HasValue && runId != _lastSeenRunId) {
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

    /// <summary>
    /// True if the streamer can skip another card without exceeding the per-act limit.
    /// `actLimit` semantics: -1 = unlimited; 0 = strict (no skips); positive = cap.
    /// </summary>
    public bool IsSkipAllowed(int actLimit)
    {
        if (actLimit < 0) return true;
        return _actSkipsUsed < actLimit;
    }

    /// <summary>Increment skip counter. Caller is responsible for ensuring this is called once per actual skip.</summary>
    public void RecordSkip() => _actSkipsUsed++;

    /// <summary>Render a snapshot of remaining/limit suitable for label display.</summary>
    public SkipBudgetSnapshot Snapshot(int actLimit) => new(
        UsedThisAct: _actSkipsUsed,
        LimitThisAct: actLimit,
        RemainingThisAct: actLimit < 0 ? int.MaxValue : Math.Max(0, actLimit - _actSkipsUsed));

    // Test seam — only use from tests.
    internal void ResetForTests() {
        _actSkipsUsed = 0;
        _lastSeenActIndex = null;
        _lastSeenRunId = null;
    }
}

internal readonly record struct SkipBudgetSnapshot(int UsedThisAct, int LimitThisAct, int RemainingThisAct);
```

### Tests (`SkipBudgetTrackerTests.cs`) <!-- NEW v3 -->

~10 tests covering:
- `IsSkipAllowed` with `actLimit = 0` (always false; strict mode)
- `IsSkipAllowed` with `actLimit = -1` (always true; unlimited)
- `IsSkipAllowed` with positive limit, before / after skip count exhausts
- `RecordSkip` increments counter
- `ObserveRunAndAct` resets counter on run change
- `ObserveRunAndAct` resets counter on act change (same run)
- `ObserveRunAndAct` does NOT reset on identical run+act
- `ObserveRunAndAct` with null run-id (no reset; degraded run detection)
- `Snapshot` produces correct remaining count
- `Snapshot` with `actLimit = -1` returns `int.MaxValue` for remaining

These tests have NO Godot or Harmony dependencies. Pure xUnit.

## `CardSkipCounterLabel` (the UI, RichTextLabel) <!-- CHANGED v3 -->

Godot `RichTextLabel`. <!-- was Label in v2 --> Default rendering is plain text; future B.2.x polish can add BBCode formatting (e.g., red text when budget exhausted) without changing the node type.

### Lifecycle

- Created and parented in `CardRewardSkipGatePatch._Ready` postfix.
- Position: anchored relative to `_proceedButton`'s position. Fallback to fixed offset from rewards-screen root with `Warn` log if `_proceedButton` not found.
- Hidden if `cardSkipsPerAct == -1` (unlimited; nothing to display). <!-- CHANGED v3: simpler condition -->
- Updated text format: `Card skips: <remaining>/<limit> act` — render `-1` as `∞` (label only — receipts use `unlimited`). <!-- CHANGED v3: no run-half -->
- Cleaned up automatically when the rewards screen is freed.
- Static `_activeLabel` reference nulled in `_ExitTree` postfix.
- All consumer sites guard with `Godot.GodotObject.IsInstanceValid(_activeLabel)` before use.

### Failure modes

- Proceed button not found: fallback positioning + `Warn` log. Gate logic still works.
- Failed to attach label: `Error` log, gate logic continues. Label is non-essential UX.

## `ModEntry` extensions

```csharp
internal static class ModEntry {
    internal static SettingsResult? Settings { get; private set; }

    // After ModSettings.Load(...):
    Settings = result;
}
```

Plus retro-apply TiLog `[SlayTheStreamer2]` prefix to all existing TiLog calls in `ModEntry.cs` (~5 call sites). <!-- CHANGED v3 -->

## `NeowBlessingVotePatch` extensions

(Unchanged from v2.) Apply run-ID guard to Neow's resume path for template consistency with B.2.1's card vote. ~10 LOC. **Plus**: retro-apply TiLog `[SlayTheStreamer2]` prefix to all existing TiLog calls (~10 call sites in the patch). <!-- CHANGED v3 -->

## `ModSettings` extensions <!-- CHANGED v3: simpler -->

One new key:

```jsonc
{
  "schemaVersion": 1,
  "channel": "...",
  "username": "...",
  "oauthToken": "...",
  // — new in B.2.1 —
  "cardSkipsPerAct": 1    // default 1; -1 = unlimited; 0 = strict
}
```

### Parsing rules

- Missing key → use default (`1`).
- Non-integer value → warning + use default. Non-fatal.
- Value < -1 → warning, clamp to -1.

### Tests to add to `ModSettingsTests`

- `CardSkipsPerActMissingUsesDefault` (default = 1).
- `CardSkipsPerAct_InvalidValue_WarnsAndUsesDefault`.
- `CardSkipsPerAct_NegativeOtherThanMinusOne_ClampsToMinusOne` (e.g., `-5` → `-1`).
- `CardSkipsPerAct_Zero_IsStrict` (parses successfully, no warning; value = 0).
- `CardSkipsPerAct_PositiveValue_Parses` (e.g., 3).

## Failure modes & degradation

(Unchanged from v2 except simplifications driven by single-knob.)

| # | Failure mode | Behaviour |
|---|---|---|
| 1 | `CardRewardVotePatch.Prepare` fails | Vote patch silently skips registration. Card rewards play vanilla. Skip gate still works (separate patch). |
| 2 | `CardRewardSkipGatePatch.Prepare` fails | Skip gate skips registration. Card vote still works; streamer can bypass via Proceed (vanilla behaviour). |
| 3 | `_Ready` postfix throws | Logs Error, vanilla `_Ready` already completed. No gate this round. |
| 4 | `RewardSkippedFrom` postfix throws | Logs Error. Counter may not be incremented → future gates may be too permissive (fail-open). |
| 5 | `_ExitTree` postfix throws | Logs Error. `_activeLabel` may dangle until next `_Ready` overwrites it; `IsInstanceValid` guard catches it. |
| 6 | Reflection failure on `_options` / `_rewardButtons` field | Logs Warn, returns "no card reward" / "can't determine cards" → fail-open. |
| 7 | `DebugOnlyGetState()` returns null at vote start | Logs Warn ("run-ID guard degraded — null state"). Vote proceeds without run-ID guard. |
| 8 | `DebugOnlyGetState()` returns null at resume time | Null-safe comparison naturally aborts resume. No crash. |
| 9 | Run-ID guard fires (run abandoned mid-vote) | Logs Warn ("Resume aborted: run changed during vote"), no resume. |
| 10 | Reroll mid-vote (cards replaced before resume) | Holder snapshot signature mismatch → resume aborts → chat receipt `vote cancelled — streamer rerolled` → streamer clicks new card → new vote. |
| 11 | Settings file completely missing | Mod loads silently with no chat capability. Skip gate detects `Settings` is `Missing`/`Malformed` and degrades to vanilla. |
| 12 | Save/quit/reload mid-run | Static counters reset on process restart. Documented as known v0.1 limit; persistence deferred to v0.2. |
| 13 | AutoSlay running and triggers `SelectCard` programmatically | Vote fires (acceptable). AutoSlay is off in production. |
| 14 | Multiplayer (Players.Count > 1) | Both vote patch and skip gate bail. Vanilla card flow runs. |

## Acceptance gate

7-step gate. Each is a manual playthrough; mod is B.2.1-ready only when all green.

- **Step 0 — Pure regression check (B.1 features only).** Settings present with B.1 keys only (no `cardSkipsPerAct`). Mod loads cleanly. Run starts; Neow vote works (chat votes, winner applies); chat connect-once receipt fires. **No card-reward path exercised.** Confirms the new patches don't regress B.1 behaviour. (Run abandoned before first combat.)

- **Step 1 — Card vote happy path (3 successful runs).**
  - chat votes for a card via `#0`/`#1`/`#2`, winning card claimed via dispatcher.Post resume
  - latest-wins on multi-vote-from-one-user (test by changing vote `#0` → `#2` from same chatter)
  - both `#N` and bare `N` accepted
  - close receipt fires with correct card name
  - VoteTallyLabel (top-right) shows tally during vote
  - skip-counter label visible near Proceed button, format `Card skips: 1/1 act`
  - skip-counter label updates correctly when cards are claimed (no skip used; stays `1/1 act`)

- **Step 2 — Skip used.** With `cardSkipsPerAct: 1`: rewards screen → click Proceed without claiming card → skip allowed → chat receipt `Streamer skipped a card reward (1/1 act)` → counter label updates to `0/1 act` → next combat: rewards screen opens with Proceed disabled (must claim) → click card → vote runs → claim → Proceed enabled.

- **Step 3 — Skip blocked.** With `cardSkipsPerAct: 0` from start: rewards screen opens, Proceed visibly disabled. Streamer must click card → vote runs → claim → Proceed enabled. No way to bypass.

- **Step 4 — Counter resets.** Use `act 2` console command to jump acts → next rewards screen: counter label resets to `1/1 act` → skip usable again. Same for new run via menu (verify run-id mismatch resets counter).

- **Step 5 — Multi-reward-type screen.** Find or trigger a rewards screen with both card AND another reward type (gold / potion / boss relic). With `cardSkipsPerAct: 0`: claim card via vote → verify Proceed becomes enabled (vanilla `_skipDisallowed` becomes irrelevant when button transitions to non-Skip mode) → claim or skip the other reward as normal. **If the other reward is locked from skipping after card claim**, document and add to v0.2 follow-up.

- **Step 6 — Edge cases.**
  - **Mid-vote run abandon** — start a card vote, immediately open menu and click Abandon Run, wait 30s for vote timer to expire. Verify run-ID guard fires (`Warn` log: `[SlayTheStreamer2][card-vote] Resume aborted: run changed during vote`). No crash. <!-- CHANGED v3: prefix in expected log -->
  - **Mid-vote reroll** if a relic enables it — start vote, click reroll on sub-screen, wait for vote timer. Verify chat receipt `vote cancelled — streamer rerolled` fires. Streamer clicks new card → new vote.
  - **Streamer escape** (via menu) mid-vote — vote runs to normal close in background; resume drops via `IsInstanceValid` check; no crash.
  - **Rapid card clicks** — only first triggers vote; subsequent clicks no-op via `_voteInProgress` guard.
  - **Mode B verification (look + back out)** <!-- NEW v3 --> — open card sub-screen, see cards, return to rewards screen WITHOUT picking, click Proceed. With `cardSkipsPerAct: 1`: skip is allowed (counter decrements). Confirms Decision 18 — Mode B not Mode A.

## Open questions

None blocking. One soft question for the implementation phase:

1. **`DisallowSkipping()` lifecycle in multi-reward screens** — does vanilla's `TryEnableProceedButton` self-correct after card claim? Step 5 of the acceptance gate is the validation point. If self-correction fails, B.2.2 polish adds a `RewardCollectedFrom` postfix to re-evaluate.

## Cross-references

- [`docs/superpowers/specs/2026-05-09-plan-b-1-vertical-slice-design-v3.md`](./2026-05-09-plan-b-1-vertical-slice-design-v3.md) — B.1 spec; suspend-and-resume pattern source-of-truth.
- [`docs/superpowers/specs/META-REVIEW-2026-05-10-plan-b-2-1-card-reward-vote-design.md`](./META-REVIEW-2026-05-10-plan-b-2-1-card-reward-vote-design.md) — meta-review with reviewer details, consensus, conflicts.
- [`docs/superpowers/specs/2026-05-10-plan-b-2-1-card-reward-vote-design-v2.md`](./2026-05-10-plan-b-2-1-card-reward-vote-design-v2.md) — v2 (post-meta-review) for diff context.
- [`notes/06-followups-and-deferred.md`](../../../notes/06-followups-and-deferred.md) — B.1 completion findings; run-ID guard origin; relic curation deferral; Save/Load loophole follow-up; **NEW v3: reflected sts2.dll members listing for B.2.1 maintainability**.
- [`decompiled/sts2/...`](../../../decompiled/sts2/) — game source references (per v2 spec).

## Notes/06 entry to add (per Optional Enhancement #9) <!-- NEW v3 -->

Add a new section to `notes/06-followups-and-deferred.md` titled **"Reflected sts2.dll members — B.2.1 dependency surface"** listing every type, field, method, and signal name that B.2.1 patches depend on via reflection. Single update point when MegaCrit ships breaking patches. Format:

```markdown
### Reflected sts2.dll members — B.2.1 dependency surface

CardRewardVotePatch depends on:
- `MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardSelectionScreen` (type)
- `NCardRewardSelectionScreen.SelectCard(NCardHolder)` (method, public, void)
- `NCardRewardSelectionScreen._options` (field, IReadOnlyList<CardCreationResult>)
- `NCardRewardSelectionScreen._cardRow` (field — used to enumerate NCardHolder children; pin exact accessor)
- `MegaCrit.Sts2.Core.Models.CardCreationResult.Card.Name.GetText()` (call chain for receipt labels)
- `MegaCrit.Sts2.Core.Runs.RunManager.Instance` (singleton)
- `RunManager.DebugOnlyGetState()` (method — returns RunState; verify non-null in modded production)
- `RunState.Id` (field — pin exact type, assumed Guid)
- `RunState.Players.Count` (for MP bail)

CardRewardSkipGatePatch depends on:
- `MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen` (type)
- `NRewardsScreen._Ready()` (Godot lifecycle — verify Harmony patchability)
- `NRewardsScreen._ExitTree()` (Godot lifecycle)
- `NRewardsScreen.RewardSkippedFrom(Control)` (method — vanilla skip callback)
- `NRewardsScreen.DisallowSkipping()` (method, public, void)
- `NRewardsScreen._rewardButtons` (field, List<Control>)
- `NRewardsScreen._skippedRewardButtons` (field, List<Control>)
- `NRewardsScreen._proceedButton` (field, NProceedButton)
- `NRewardsScreen.RewardCollectedFrom(Control)` semantics (button removed from _rewardButtons)
- `MegaCrit.Sts2.Core.Nodes.NRewardButton` (type)
- `NRewardButton.Reward` (accessor — verify property vs field; pin in Prepare)
- `MegaCrit.Sts2.Core.Rewards.CardReward` (type — for CardReward identity check)
- `RunState.Acts` (field — for current-act detection)

NeowBlessingVotePatch (B.1, retro-touched in B.2.1 for run-ID guard):
- `MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom._event` (already in B.1)
- `RunManager.Instance.DebugOnlyGetState()?.Id` (NEW in B.2.1 for guard)

If any of these are renamed/removed in a future StS2 update, Prepare logs Error and the patch silently degrades to vanilla.
```

---

## Optional Enhancements — final disposition <!-- CHANGED v3: applied/declined status -->

| # | Change | Status |
|---|---|---|
| 1 | Drop `cardSkipsPerRun` for v0.1 | ✅ **Applied** — single per-act knob; intent clarified (one-or-the-other mode, not stacked) |
| 2 | Extend `VoteTallyLabel` instead of separate label | ❌ **Declined** — viewer-readability + canvas-position requirement |
| 3 | Pure `SkipBudgetTracker` extraction | ✅ **Applied** — Decision 19 |
| 4 | Patch `StartRun` / `TransitionToAct` for precise reset | ⏸️ **Deferred** — lazy detection acceptable |
| 5 | `RichTextLabel` for `CardSkipCounterLabel` from day one | ✅ **Applied** — Decision 9 |
| 6 | Composite run-id fingerprint as fallback to `DebugOnlyGetState()` | ⏸️ **Conditional** — implement only if Step 0 reveals null in production |
| 7 | Card name uniqueness disambiguation in receipts | ⏸️ **Deferred** — defer until observed |
| 8 | Mode A: skip-without-looking detection | ❌ **Declined** — Mode B chosen explicitly (Decision 18) |
| 9 | notes/06 entry listing reflected sts2.dll members | ✅ **Applied** — see "Notes/06 entry to add" section |
| 10 | TiLog `[SlayTheStreamer2]` prefix | ✅ **Applied** — Decision 20; retro-applied to B.1 patches |

---

**Final pre-implementation status**: v3 is the spec-of-record. All meta-review Must-do + Should-do applied (v2). All Optional Enhancements decided (v3). Ready for `/superpowers:writing-plans` to produce the implementation plan.
