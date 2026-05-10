# Plan B.2.1 — card reward vote (design v2)

**Date**: 2026-05-10
**Status**: Draft v2 — post-meta-review (7 reviewers)
**Predecessor**: [`2026-05-10-plan-b-2-1-card-reward-vote-design.md`](./2026-05-10-plan-b-2-1-card-reward-vote-design.md) (v1). Meta-review at [`META-REVIEW-2026-05-10-plan-b-2-1-card-reward-vote-design.md`](./META-REVIEW-2026-05-10-plan-b-2-1-card-reward-vote-design.md).
**Scope**: Second sub-plan of Plan B. Adds the **card reward** vote — the next "click 1-of-N option-button screen" decision after Neow. Same suspend-and-resume pattern as B.1, copy-paste-modified into a new patch class. Adds two new pieces of Game-side machinery: a **Proceed-skip gate** that prevents the streamer from bypassing chat by clicking through rewards unclaimed (with per-act / per-run skip budget), and an in-game **skip-counter label** so the streamer can see how many skips they have left.

The remaining three v0.1 votes (boss relic, map path, act-boss) and the in-game settings panel are explicit non-goals — they belong to B.2.2, B.2.3, B.2.4, and B.3.

> **Architectural hard constraint** (carried forward from B.1): every Harmony prefix that triggers a vote MUST use the **suspend-and-resume** pattern. Prefix returns immediately (`false` to skip original, after firing `_ = HandleVoteAsync(...)` as fire-and-forget). The async handler runs the vote, then re-invokes the chosen game-state mutation via `dispatcher.Post(...)`. **No blocking the Godot main thread on `AwaitWinnerAsync().GetAwaiter().GetResult()`, ever.**

## Author's note on v2 changes

7 reviewers gave the v1 spec a thorough beating. Headline shifts in v2:

1. **EnglishReceipts seam violation eliminated.** v1's "option (a) preferred" added skip-domain helpers to `Ti/Voting/EnglishReceipts.cs`. v2 inlines the format string in `CardRewardSkipGatePatch` (Game side). Universal reviewer consensus.
2. **`ModEntryState.Settings` was fictional.** v2 defines an actual `ModEntry.Settings` static accessor; `ModEntry.cs` is now ✏️ in the architecture (no longer "no functional change").
3. **Run-ID guard tightened across the board.** Verify `DebugOnlyGetState()` returns non-null in modded production builds; pin exact field name + type in `Prepare`; fail-open with `Warn` log if guard cannot capture run-id; **applied to `NeowBlessingVotePatch` too** (template consistency for B.2.2).
4. **Static `_activeLabel` lifecycle fixed.** `IsInstanceValid` guard before every use, plus null-on-`_ExitTree` postfix as belt-and-suspenders.
5. **Multi-card-reward gate re-evaluation added.** `RewardSkippedFrom` postfix now re-checks budget and re-calls `DisallowSkipping()` if the just-counted skip exhausted it.
6. **`HasUnclaimedCardReward` precision specified.** `Prepare` verifies that vanilla removes claimed/skipped buttons from `_rewardButtons` (or excludes via `_skippedRewardButtons`).
7. **`Interlocked` dropped for skip counters.** Documented as main-thread-only. Vote patch's `Interlocked.CompareExchange` for `_voteInProgress` stays (genuinely cross-thread).
8. **Receipt counter semantics fixed.** Used/limit format consistently; suppress run-half when unlimited; never emit `0/∞`. Label uses remaining/limit.
9. **Skip receipts route through `OutgoingMessageQueue`.** Specified as `coordinator.Chat.SendMessageAsync(text, OutgoingMessagePriority.Normal)` — reality-checked to enqueue into the rate-limited queue.
10. **`VoteTallyLabel.AttachTo` explicit in `CardRewardVotePatch`.** Prevents copy-paste oversight.
11. **Card-holder re-derivation at resume.** Use `holders[winnerIndex]` from current screen, not captured ref.
12. **MP-bail added to vote patch.** v1 only mentioned it for the gate.
13. **Save/Load loophole acknowledged.** Static state means save-quit-reload may reset budget; documented as v0.1 known limit.
14. **Reroll-mid-vote: chat receipt added.** Compromise between R2's patch-reroll and R6's silent-absorb; chat sees `vote cancelled — streamer rerolled`.
15. **`Prepare` shape checks expanded.** Now verify `_proceedButton`, `_rewardButtons` element type, button-removal semantics; comprehensive "Reflected members" subsections per patch.
16. **Acceptance gate rewritten.** Step 0 split from Step 1; multi-reward-type case added; Abandon-Run-mid-vote case added; AutoSlay interaction documented.
17. **Glyph safety**: receipts use `unlimited` instead of `∞`. Label may keep `∞` if the glyph renders during Step 1.
18. **Godot `_Ready` patchability**: explicit verification step in implementation plan (Task 1: write no-op `_Ready` postfix; verify it fires; fall back to `_EnterTree` or `_Notification` if needed).

Plus assorted nits, telemetry log lines, and operator-validation case additions. Optional Enhancements (Surfinite-pick) appended at end.

## Goals

1. **Ship the card reward vote end-to-end** — chat votes on which of the (typically 3) cards the streamer adds; suspend-and-resume copy of B.1's pattern; receipts and tally label work identically; Twitch backlog-on-JOIN behaviour from B.1 is reused untouched.
2. **Prevent streamer-side bypass via Proceed.** With default settings, every card reward must be either claimed (chat picks) or counted against a finite skip budget. The streamer cannot silently skip every card to escape chat agency.
3. **Make the skip budget visible** — a small in-game label near the Proceed button shows `Card skips: <act-remaining>/<act-limit> act, <run-remaining>/<run-limit> run`. Streamer always knows where they stand.
4. **Fail soft on every new failure mode** added by B.2.1 — bad settings keys, missing rewards-screen UI nodes, run/act detection edge cases, reroll-mid-vote, run-abandon-mid-vote. Mod stays loaded; game keeps running; vote silently absorbs when resume target is gone.
5. **De-risk B.2.2 boss relic and B.2.3 map path** by making the second working example of suspend-and-resume real. After B.2.1 ships, B.2.2's plan will see what's actually shared (likely: ~80% of the per-patch boilerplate) and a helper extraction can be planned with confidence.
6. **Apply the run-ID guard to `NeowBlessingVotePatch` too** so B.2.2 inherits a consistent template. <!-- CHANGED v2: prevent two subtly-different suspend/resume templates entering B.2.2 — Reviewers 1, 2 -->

## Non-goals

- B.2.2 boss relic, B.2.3 map path, B.2.4 in-game settings UI, B.3 act-boss. All separate sub-plans.
- **Helper / base-class extraction.** Deliberately deferred to B.2.2 per Rule of Three.
- **Patching reroll** or non-`SelectCard` buttons on `NCardRewardSelectionScreen` (alternates etc.). Streamer uses reroll freely; vote starts when streamer clicks an actual card. <!-- CHANGED v2: reroll-mid-vote sends a chat receipt for transparency, but no patch on reroll itself — Reviewers 1, 2 / 6 conflict-resolved -->
- **Patching `NRewardsScreen.OnProceedButtonPressed` directly.** We use `DisallowSkipping()` instead.
- **Per-relic curation** (chat-strong / streamer-strong relic blacklist). Surfinite raised this during brainstorming; deferred to v0.2 polish. Documented in notes/06.
- **Settings-driven vote duration.** B.1's `NeowBlessingVotePatch` hardcodes `TimeSpan.FromSeconds(30)`; B.2.1 keeps the same hardcoded value in `CardRewardVotePatch`. Adding a `voteDuration` settings key is B.2.4 territory. <!-- CHANGED v2: pacing concern (16 votes/act × 30s = ~8 min/act of waiting) acknowledged but addressed via B.2.4 — Reviewer 5 -->
- **BBCode stripping in receipts.** Address if it actually surfaces.
- **Multiplayer co-op support** — B.2.1 bails on `Players.Count > 1` in BOTH the vote patch and the skip gate. <!-- CHANGED v2: was vague in v1 — Reviewer 7 -->
- **Localised receipts.** English only.
- **In-game error toasts** — silent degradation only.
- **Persisting skip counters across save/quit/reload.** Static state resets on process restart; documented as known v0.1 limit. <!-- CHANGED v2: Save/Load loophole acknowledged — Reviewer 6 -->
- **Skip-without-looking detection** (differentiate "claimed but didn't pick" from "didn't open sub-screen"). Budget already bounds the bypass; document not implement. <!-- CHANGED v2: Reviewer 6's reasoning preserved, Reviewer 2's suggestion deferred -->
- **`AllowSkipping()` re-enable after card claim.** Vanilla's existing logic transitions Proceed mode after card-claim, partially handling this. Operator validation will catch any remaining lockout in multi-reward screens; B.2.2 polish if observed.
- **Streamer-configurable per-vote receipts.** Default is "always chat receipt"; settings-driven receipt policy is a Plan A v2.3 follow-up.

## Decisions

| # | Decision | Rationale |
|---|---|---|
| 1 | **B.2.1 covers card reward only.** | Single-vote vertical slice; matches B.1's discipline. |
| 2 | **Patch target for vote: `NCardRewardSelectionScreen.SelectCard(NCardHolder)` Prefix.** | Verified from decompiled source; AutoSlay handler confirms call path. Single intercept point, identical shape to B.1's `OptionButtonClicked`. |
| 3 | **Patch target for skip gate: `NRewardsScreen._Ready` Postfix.** | After vanilla builds the rewards container, postfix checks for unclaimed card reward + skip budget, calls vanilla's `DisallowSkipping()` if budget exhausted. **Implementation note (v2)**: `_Ready` is a Godot lifecycle method invoked via native-to-managed interop. Implementation MUST verify Harmony patches it correctly (Task 1 in implementation plan: write no-op `_Ready` postfix that just logs; confirm it fires; fall back to `_EnterTree` or `_Notification` postfix if `_Ready` patch is unreliable). <!-- CHANGED v2: highest technical risk made explicit — Reviewer 3 --> |
| 4 | **Patch target for skip-detect: `NRewardsScreen.RewardSkippedFrom(Control)` Postfix.** | Vanilla calls this when a reward button is skipped via Proceed. Postfix detects card-reward skips, decrements counters, sends chat receipt, refreshes label, **and re-evaluates gate** — if THIS skip exhausted the budget while other unclaimed card rewards remain on the same screen, call `__instance.DisallowSkipping()` again to prevent multi-card bypass. <!-- CHANGED v2: multi-card-reward gate re-evaluation — Reviewers 1, 6 --> |
| 5 | **Suspend-and-resume pattern reused verbatim from B.1.** | Same two-flag re-entry guard, same post-Start fallback in `HandleVoteAsync`'s outer catch, same `IsInstanceValid` resume check, same `dispatcher.Post(...)` resume invocation. **Plus new: `VoteTallyLabel.AttachTo(session)` explicitly posted at vote start** (matches B.1; explicit to prevent copy-paste oversight). <!-- CHANGED v2: explicit AttachTo mention — Reviewers 3, 5 --> |
| 6 | **Run-ID guard added to resume path of BOTH vote patches** (Card AND Neow). | B.1's notes/06 flagged this as B.2 hardening. v2: capture `RunManager.Instance.DebugOnlyGetState()?.Id` (or equivalent — see Implementation note below) at vote start, compare at resume, skip resume if changed. **Implementation MUST verify `DebugOnlyGetState()` returns a non-null state object with a stable Id property in modded production builds (not just debug builds)**. Pin exact field name + type in `Prepare`; if shape mismatch, log Warn and disable the run-ID check (vote still works, just without the guard — fail-open). Run-ID mismatch at resume logged at **Warn** (not Info — this is a safety feature firing). <!-- CHANGED v2: tightened across the board, applied to Neow, Warn level — Reviewers 1, 2, 3, 4, 5, 6, 7 (universal) --> |
| 7 | **Skip is never a chat-vote option.** | Chat-vs-streamer asymmetry. Vote options = current cards on screen, dynamic count (1 to N). |
| 8 | **Skip budget: dual cap, per-act + per-run, both enforced (AND).** | `cardSkipsPerAct` (default `1`) and `cardSkipsPerRun` (default `-1` = unlimited). Skip allowed iff `actLimit < 0 || _actSkipsUsed < actLimit` AND same for run. (Treat `-1` as ∞.) Default = chaos-by-default-mild. Strict mode = `cardSkipsPerAct: 0`. **AND semantics documented**: per-run cap can prevent skipping even in a fresh act where per-act has reset; both numbers shown in label so streamer sees which gate is firing. <!-- CHANGED v2: AND semantics + correct boundary conditions — Reviewer 2 nit + Reviewer 3 documentation request --> |
| 9 | **In-game "skips remaining" label parented under `NRewardsScreen` near `_proceedButton`.** | New `CardSkipCounterLabel` Godot Label, attached during the skip-gate postfix. Hidden when both limits are unlimited. Cleans up when screen frees (Godot lifecycle), AND **the static `_activeLabel` reference is nulled in an `_ExitTree` postfix on `NRewardsScreen`** (belt-and-suspenders for the dangling-reference bug). All consumer sites guard with `Godot.GodotObject.IsInstanceValid(_activeLabel)` before use. <!-- CHANGED v2: dangling-reference fix — Reviewers 1, 2, 3, 5, 6 --> |
| 10 | **Random fallback (zero votes received): random card, never skip.** | Same "play the game" semantics as B.1's Neow random fallback. Skip is never selected by the fallback even when `cardSkipsPerAct > 0`. Chat-silence shouldn't auto-trigger streamer-only escape valve. <!-- CHANGED v2: rationale tightened — Reviewer 7 alternative explicitly rejected --> |
| 11 | **Receipt format: name-only.** | `Vote: #0 Strike, #1 Defend, #2 Bash — 30s, type #N or N`. Matches B.1's Neow option-title format. |
| 12 | **Reroll, alternates, and other non-`SelectCard` buttons not patched.** | Streamer uses them freely. Vote starts when streamer clicks an actual card. **NEW (v2): if streamer clicks reroll mid-vote, chat receives a `vote cancelled — streamer rerolled` receipt** (cheap transparency; no patch on reroll itself). Vote silently absorbs at resume; streamer must click a new card → new vote. <!-- CHANGED v2: reroll receipt for transparency — Reviewers 1, 2 conflict-resolved with R6 --> |
| 13 | **No helper / base class extraction in B.2.1.** | Rule of Three. Re-evaluate after B.2.2. |
| 14 | **Use vanilla DevConsole for dev iteration, no custom debug patches.** | DevConsole auto-unlocks when `ModManager.IsRunningModded()`. |
| 15 | **`ModEntry.Settings` static accessor for patches.** <!-- NEW v2 --> | B.1 doesn't expose settings via static. v2 adds `internal static SettingsResult.Success? Settings { get; private set; }` to `ModEntry.cs`, set after successful `ModSettings.Load()`. Patches read via `ModEntry.Settings`. Mark `ModEntry.cs` as ✏️ in architecture. <!-- CHANGED v2: addresses fictional ModEntryState.Settings — Reviewers 1, 2, 3, 5, 7 --> |
| 16 | **Skip receipt formatting inlined in `CardRewardSkipGatePatch`.** <!-- NEW v2 --> | Game-domain knowledge stays in Game/. Do not modify `Ti/Voting/EnglishReceipts`. <!-- CHANGED v2: TI/Game seam preserved — Reviewers 1, 2, 3, 4, 5, 7 --> |
| 17 | **Skip counters use plain `++`, not `Interlocked`.** <!-- NEW v2 --> | All `NRewardsScreen` callbacks (`_Ready`, `RewardSkippedFrom`, `RewardCollectedFrom`) run on Godot's main thread. `Interlocked` would be misleading theatre. Document in code comment. (Vote patch's `Interlocked.CompareExchange` for `_voteInProgress` stays — that flag genuinely crosses threads.) <!-- CHANGED v2: thread-model honesty — Reviewers 2, 3, 5, 7 --> |

## Architecture

```
src/
├── Ti/                                          ✅ unchanged from B.1; NO modifications in B.2.1
├── Game/                                        ✏️  extended in B.2.1
│   ├── Bootstrap/
│   │   └── ModSettings.cs                       ✏️  add `cardSkipsPerAct`, `cardSkipsPerRun` keys
│   ├── DecisionVotes/
│   │   ├── NeowBlessingVotePatch.cs             ✏️  add run-ID guard for template consistency  <!-- CHANGED v2 -->
│   │   ├── CardRewardVotePatch.cs               🆕 B.2.1 — Harmony Prefix on NCardRewardSelectionScreen.SelectCard
│   │   └── CardRewardSkipGatePatch.cs           🆕 B.2.1 — Postfix on NRewardsScreen._Ready + RewardSkippedFrom + _ExitTree; owns counter state
│   └── Ui/                                      🆕 B.2.1 — new sub-namespace; StS2-coupled UI
│       └── CardSkipCounterLabel.cs              🆕 B.2.1 — Godot Label parented under NRewardsScreen near proceed button
└── ModEntry.cs                                  ✏️  v2: add static Settings accessor for patches  <!-- CHANGED v2 -->

tests/
├── Bootstrap/
│   └── ModSettingsTests.cs                      ✏️  extend with ~7 tests for new keys (incl. negative-other-than--1)  <!-- CHANGED v2 -->
└── Game/
    └── DecisionVotes/
        └── CardRewardSkipGateTests.cs           🆕 B.2.1 — skip-counter logic in isolation (~12 tests)  <!-- CHANGED v2 -->
```

**Net new code estimate**: `CardRewardVotePatch` ~230 LOC (B.1 Neow patch + run-ID guard + bounds checks + holder re-derivation); `CardRewardSkipGatePatch` ~220 LOC (counter state + three postfixes + label management + skip receipt formatter); `CardSkipCounterLabel` ~80 LOC; `NeowBlessingVotePatch` ~10 LOC additions (run-ID guard); `ModSettings` additions ~30 LOC + ~50 LOC tests; `CardRewardSkipGateTests` ~180 LOC; `ModEntry` additions ~10 LOC. Total ~570 LOC of source, ~230 LOC of tests. <!-- CHANGED v2: estimates revised upward per Reviewers 2, 3, 5 -->

## `CardRewardVotePatch` (the vote)

Copy-paste-modified from `NeowBlessingVotePatch.cs`. Same five sections, same flags, same try/catch shape. Plus the run-ID guard, the holder re-derivation at resume, and explicit MP-bail.

### Reflected members verified by `Prepare` <!-- NEW v2 -->

The `Prepare` method MUST validate (and cache via `Lazy<FieldInfo?>` / `Lazy<MethodInfo?>` matching B.1's pattern):

- Type `NCardRewardSelectionScreen` exists.
- Method `SelectCard(NCardHolder)` exists with the exact signature.
- Field `_options` exists, type assignable to `IReadOnlyList<CardCreationResult>`.
- The card-holder collection field exists (decompiled source needs verification — likely `_cardRow`'s children, or a dedicated holder list). `Prepare` must pin the exact accessor and validate count matches `_options.Count`.
- `RunManager.Instance` is reachable; `DebugOnlyGetState()` exists; result type has an `Id` property of type `Guid` (or actual run-id type — pin in `Prepare`); also exposes `Players.Count`.

If ANY check fails: log Error with the specific shape mismatch, return false from `Prepare`, patch silently skips registration. Vote degrades to vanilla card reward.

### Patch shape

```csharp
[HarmonyPatch(typeof(NCardRewardSelectionScreen), nameof(NCardRewardSelectionScreen.SelectCard))]
internal static class CardRewardVotePatch
{
    private static int _voteInProgress;       // Interlocked: genuinely cross-thread (vote async / postfix sync)
    private static int _resumeInProgress;     // Interlocked: same
    private static int _multiplayerWarnFired; // Interlocked: same; one-time warning per process

    static bool Prepare(MethodBase? original) { /* see Reflected members above */ }

    static bool Prefix(NCardRewardSelectionScreen __instance, NCardHolder cardHolder)
    {
        // — guard: if _resumeInProgress, return true (let our own re-call run vanilla) —
        // — guard: extract _options; bail if null/empty (return true → vanilla) —
        // — guard: Players.Count > 1 → MP bail with one-time warning, return true —          // CHANGED v2
        // — chat-readiness gate (coordinator.Chat.State == ConnectedReadWrite) — return true if not —
        // — capture run ID via RunManager.Instance.DebugOnlyGetState()?.Id; null → log Warn,    // CHANGED v2
        //   set runIdAtStart = null and continue without run-ID guard (fail-open) —
        // — Interlocked _voteInProgress 0→1; if already 1, return false (suppress repeat click) —
        // — derive playerClickIndex by finding cardHolder in current holder collection —
        // — snapshot option labels (card names) via _options[i].Card.Name.GetText() —
        // — call coordinator.Start(...) with try/catch fallback to vanilla on throw —
        // — fire-and-forget: _ = HandleVoteAsync(coordinator, __instance, session,
        //                       optionsSnapshot, playerClickIndex, runIdAtStart) —
        // — return false (suspend) —
    }

    private static async Task HandleVoteAsync(VoteCoordinator coordinator,
        NCardRewardSelectionScreen screen, VoteSession session,
        IReadOnlyList<CardCreationResult> snapshot, int playerClickIndex, Guid? runIdAtStart)
    {
        try {
            coordinator.Dispatcher.Post(() => VoteTallyLabel.AttachTo(session));   // CHANGED v2: explicit
            int winnerIndex;
            try {
                winnerIndex = await session.AwaitWinnerAsync();
            } catch (Exception ex) {
                TiLog.Error("[card-vote] AwaitWinnerAsync threw; falling back to player click", ex);
                winnerIndex = playerClickIndex;
            }
            if (winnerIndex < 0 || winnerIndex >= snapshot.Count) {
                TiLog.Warn($"[card-vote] winnerIndex {winnerIndex} out of range; using player click");
                winnerIndex = playerClickIndex;
            }
            coordinator.Dispatcher.Post(() => ResumeOnMainThread(screen, winnerIndex, playerClickIndex, runIdAtStart));
        } catch (Exception ex) {
            TiLog.Error("[card-vote] HandleVoteAsync threw; attempting fallback resume with player click", ex);
            try {
                coordinator.Dispatcher.Post(() => ResumeOnMainThread(screen, playerClickIndex, playerClickIndex, runIdAtStart));
            } catch (Exception postEx) {
                TiLog.Error("[card-vote] fallback resume Post threw; resetting flags", postEx);
                Interlocked.Exchange(ref _resumeInProgress, 0);
                Interlocked.Exchange(ref _voteInProgress, 0);
            }
        }
    }

    private static void ResumeOnMainThread(NCardRewardSelectionScreen screen,
        int preferredIndex, int playerClickIndex, Guid? runIdAtStart)
    {
        Interlocked.Exchange(ref _resumeInProgress, 1);
        try {
            // — IsInstanceValid check — drop if screen freed —
            // — Run-ID guard: if runIdAtStart != null AND current Id != runIdAtStart, log Warn, drop —   // CHANGED v2
            // — Re-derive current holder collection from screen state —                                    // CHANGED v2
            // — Bounds check on preferredIndex; fall back to playerClickIndex if out of range —
            // — If still out of range (cards replaced by reroll), drop —
            // — screen.SelectCard(currentHolders[applyIndex]) —                                            // CHANGED v2
        } catch (Exception ex) {
            TiLog.Error("[card-vote] resume threw", ex);
        } finally {
            Interlocked.Exchange(ref _resumeInProgress, 0);
            Interlocked.Exchange(ref _voteInProgress, 0);
        }
    }

    internal static bool VoteInProgress => _voteInProgress == 1;   // NEW v2: exposed for skip gate cross-check
}
```

### Differences from `NeowBlessingVotePatch`

1. **Patch target** is `SelectCard` (single-arg `NCardHolder`) instead of `OptionButtonClicked` (two-arg `EventOption, int`).
2. **`Prepare` validation** checks card-screen-specific shape per the Reflected members list above.
3. **Option enumeration** comes from `_options` field; option count = `_options.Count` (typically 3, can be 1-N).
4. **`playerClickIndex` derivation**: find clicked `cardHolder` in current holder collection via reflection.
5. **Resume action**: re-derives current holder from screen state (`currentHolders[applyIndex]`), not captured ref. <!-- CHANGED v2 -->
6. **Run-ID guard** included from day one. <!-- CHANGED v2 -->
7. **No `DisableEventOptions` analogue**: re-entry guard suffices. **First click sets `playerClickIndex` fallback; subsequent clicks during vote are swallowed (no fallback update)** — matches B.1 Neow behaviour. <!-- CHANGED v2: explicit semantic — Reviewer 1 -->
8. **`VoteTallyLabel.AttachTo(session)` posted explicitly at vote start.** <!-- CHANGED v2: prevents copy-paste oversight -->
9. **MP-bail in prefix**: `Players.Count > 1` → return true (vanilla). <!-- CHANGED v2 -->
10. **`internal static bool VoteInProgress` accessor** for skip-gate cross-check. <!-- CHANGED v2 -->

### Lifecycle: streamer dies / abandons mid-vote

Run-ID guard fires at resume → `Warn` log → no resume. No crash, no spurious card-add into a dead run.

### Lifecycle: streamer triggers reroll mid-vote

Streamer clicks card (vote starts) → streamer clicks reroll → vanilla discards old cards. At resume time: re-derived holders don't match captured snapshot; bounds check on `preferredIndex` against new holder collection may pass (if same count) but the resulting `SelectCard(currentHolders[applyIndex])` would pick a *different card than chat voted on*. **v2 fix**: `Prepare` captures a "snapshot signature" of the original holder set; resume verifies signature unchanged before re-calling SelectCard. If signature changed (reroll occurred), drop resume + send chat receipt `vote cancelled — streamer rerolled`. Streamer must click a new card → new vote. <!-- CHANGED v2: reroll receipt + signature check — Reviewers 1, 2, 5 -->

## `CardRewardSkipGatePatch` (the gate)

Owns the skip-counter state and three postfix patches.

### Reflected members verified by `Prepare` <!-- NEW v2 -->

- Type `NRewardsScreen` exists.
- Field `_rewardButtons` exists, type assignable to `List<Control>` (or similar enumerable).
- Field `_skippedRewardButtons` exists (used to exclude already-skipped buttons from "unclaimed" check).
- Field `_proceedButton` exists, type `NProceedButton` (used for label positioning).
- Method `DisallowSkipping()` exists, public, parameterless.
- Vanilla `RewardCollectedFrom(Control)` removes the button from `_rewardButtons` (verified by inspecting decompiled source: `int a = _rewardButtons.IndexOf(button); RemoveButton(button);`). If verification fails, `HasUnclaimedCardReward` falls back to excluding via `_skippedRewardButtons`.
- Type `NRewardButton` exists; has accessor (property or field) for the underlying `Reward` of type `CardReward`.
- `RunManager.Instance.DebugOnlyGetState()` returns object with `Players.Count` and `Acts` (List<Act>) for run/act detection.

If ANY check fails: log Error, return false from `Prepare`, skip gate silently disables. Card vote still works.

### State

```csharp
internal static class CardRewardSkipGatePatch
{
    // All these fields are main-thread-only (Godot signals dispatch on main thread).
    // Plain ++ / = is correct; Interlocked would be misleading theatre.   // CHANGED v2
    private static int _actSkipsUsed;
    private static int _runSkipsUsed;
    private static int? _lastSeenActIndex;       // null = no act seen yet
    private static Guid? _lastSeenRunId;         // type pinned in Prepare; null = no run seen yet
    private static CardSkipCounterLabel? _activeLabel;
}
```

### Three postfix patches (same class, separate `[HarmonyPatch]` static-method targets via inner classes)

#### `NRewardsScreen._Ready` Postfix

```csharp
public static void Postfix(NRewardsScreen __instance)
{
    try {
        // — Players.Count > 1 bail (no skip gate in MP) —

        var runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null) return;

        // — Run-change detection: if runState.Id != _lastSeenRunId, reset _runSkipsUsed = 0 + _actSkipsUsed = 0 —
        // — Act-change detection: if runState.CurrentActIndex (resolved per Prepare) != _lastSeenActIndex, reset _actSkipsUsed = 0 —
        // — Update _lastSeenRunId, _lastSeenActIndex —

        if (!HasUnclaimedCardReward(__instance)) return;

        var settingsResult = ModEntry.Settings;     // CHANGED v2: real accessor
        if (settingsResult is not SettingsResult.Success success) return;
        // (No settings → no chat → no gate enforcement; degrades to vanilla.)
        var settings = success.Settings;

        int actLimit = settings.CardSkipsPerAct;
        int runLimit = settings.CardSkipsPerRun;
        bool actExhausted = actLimit >= 0 && _actSkipsUsed >= actLimit;
        bool runExhausted = runLimit >= 0 && _runSkipsUsed >= runLimit;

        if (actExhausted || runExhausted) {
            __instance.DisallowSkipping();
        }

        AttachOrUpdateLabel(__instance, actLimit, runLimit);
    } catch (Exception ex) {
        TiLog.Error("[card-skip-gate] _Ready postfix failed", ex);
    }
}
```

#### `NRewardsScreen.RewardSkippedFrom` Postfix

```csharp
public static void Postfix(NRewardsScreen __instance, Control button)
{
    try {
        if (!IsCardRewardButton(button)) return;

        _actSkipsUsed++;       // CHANGED v2: plain ++; main-thread-only
        _runSkipsUsed++;

        var settingsResult = ModEntry.Settings;
        if (settingsResult is SettingsResult.Success success) {
            var settings = success.Settings;
            SendSkipReceipt(settings.CardSkipsPerAct, settings.CardSkipsPerRun);

            // — NEW v2: re-evaluate gate; if THIS skip exhausted budget AND another unclaimed
            //   card reward remains on this screen, call DisallowSkipping() again to prevent
            //   multi-card-reward bypass on same screen —                              // CHANGED v2
            int actLimit = settings.CardSkipsPerAct;
            int runLimit = settings.CardSkipsPerRun;
            bool actExhausted = actLimit >= 0 && _actSkipsUsed >= actLimit;
            bool runExhausted = runLimit >= 0 && _runSkipsUsed >= runLimit;
            if ((actExhausted || runExhausted) && HasUnclaimedCardReward(__instance)) {
                __instance.DisallowSkipping();
            }
        }

        if (_activeLabel != null && Godot.GodotObject.IsInstanceValid(_activeLabel)) {
            _activeLabel.UpdateText(...);   // CHANGED v2: IsInstanceValid guard
        }
    } catch (Exception ex) {
        TiLog.Error("[card-skip-gate] RewardSkippedFrom postfix failed", ex);
    }
}
```

#### `NRewardsScreen._ExitTree` Postfix <!-- NEW v2 -->

```csharp
public static void Postfix(NRewardsScreen __instance) {
    // Belt-and-suspenders for the dangling-reference bug:
    // null _activeLabel when the rewards screen exits.
    _activeLabel = null;
}
```

### Skip receipt formatter (inline, not in `EnglishReceipts`) <!-- CHANGED v2 -->

```csharp
// Inlined in CardRewardSkipGatePatch — game-domain copy stays in Game/.
private static string FormatSkipReceipt(int actUsed, int actLimit, int runUsed, int runLimit)
{
    string actPart = actLimit < 0 ? "unlimited act" : $"{actUsed}/{actLimit} act";
    string runPart = runLimit < 0 ? null : $", {runUsed}/{runLimit} run";   // suppressed when unlimited
    return $"Streamer skipped a card reward ({actPart}{runPart ?? ""})";
}

private static void SendSkipReceipt(int actLimit, int runLimit)
{
    var coordinator = Voter.Default;
    if (coordinator?.Chat?.State != ChatConnectionState.ConnectedReadWrite) return;

    string text = FormatSkipReceipt(_actSkipsUsed, actLimit, _runSkipsUsed, runLimit);
    // Routes through OutgoingMessageQueue — rate limiting preserved.   // CHANGED v2
    _ = coordinator.Chat.SendMessageAsync(text, OutgoingMessagePriority.Normal);
}
```

Receipt examples (used/limit semantics; `unlimited` not `∞`): <!-- CHANGED v2 -->
- Default config: `Streamer skipped a card reward (1/1 act)`
- `cardSkipsPerAct: 2, cardSkipsPerRun: 5`: `Streamer skipped a card reward (1/2 act, 3/5 run)`
- `cardSkipsPerAct: -1, cardSkipsPerRun: 5`: `Streamer skipped a card reward (unlimited act, 3/5 run)`

### `HasUnclaimedCardReward` / `IsCardRewardButton`

Both inspect via cached reflection (verified in `Prepare`). For each button in `_rewardButtons`:
- Is it an `NRewardButton`? (Skip linked-reward sets etc.)
- Excluded from `_skippedRewardButtons`? (Belt-and-suspenders if vanilla doesn't remove from `_rewardButtons`.) <!-- CHANGED v2 -->
- Is the underlying `Reward` a `CardReward`?

If yes to all → unclaimed card reward exists. Reflection failure → log Warn → return false (assume no card reward → fail-open).

### Act / run change detection

`RunState.Acts` is the act list (verified). `Prepare` MUST identify the current-act access pattern — likely candidates:
- `Acts.Count - 1` (assumes acts are appended monotonically)
- A `CurrentAct` / `CurrentActIndex` property if one exists
- Derived from `runState.CurrentRoom`'s parent act

**`Prepare` MUST pin the chosen pattern and verify it returns a stable index.** If unverifiable, skip gate disables (fail-open: vanilla card flow runs without budget enforcement).

`RunState.Id` (or actual run-id field — pin in `Prepare`) provides run identity. Same `Prepare` discipline.

Both compared on every `_Ready` postfix; counter resets on mismatch.

## `CardSkipCounterLabel` (the UI)

Godot `Label` (no formatting needed for v0.1; future colour can swap to `RichTextLabel`).

### Lifecycle

- Created and parented in `CardRewardSkipGatePatch._Ready` postfix.
- Position: anchored relative to `_proceedButton`'s position (offset above-and-left). If `_proceedButton` not found in `Prepare`'s reflection check, label falls back to fixed offset from rewards-screen root with a `Warn` log. <!-- CHANGED v2 -->
- Hidden if `cardSkipsPerAct == -1 AND cardSkipsPerRun == -1`.
- Updated text format: `Card skips: <actRemaining>/<actLimit> act, <runRemaining>/<runLimit> run` — render `-1` as `∞` (label only — receipts use `unlimited`). <!-- CHANGED v2 -->
- Cleaned up automatically when the rewards screen is freed (parented under `NRewardsScreen`).
- Static `_activeLabel` reference nulled in `_ExitTree` postfix (belt-and-suspenders). <!-- CHANGED v2 -->
- All consumer sites guard with `Godot.GodotObject.IsInstanceValid(_activeLabel)` before use. <!-- CHANGED v2 -->

### Failure modes

- Proceed button not found: fallback positioning + `Warn` log. Gate logic still works.
- Failed to attach label: `Error` log, gate logic continues. Label is non-essential UX.

## `ModEntry` extensions <!-- NEW v2 -->

Add a static accessor for parsed settings:

```csharp
internal static class ModEntry {
    // ... existing init code ...
    internal static SettingsResult? Settings { get; private set; }   // NEW v2

    // After ModSettings.Load(...) succeeds:
    Settings = result;   // result is SettingsResult.Success / Missing / Malformed
}
```

Patches read via `ModEntry.Settings` and pattern-match the result. `Missing`/`Malformed` cases are handled by patches as "no settings → no enforcement; degrade to vanilla".

## `NeowBlessingVotePatch` extensions (run-ID guard) <!-- NEW v2 -->

Apply the run-ID guard to Neow's resume path for template consistency with B.2.1's card vote. ~10 LOC addition:

```csharp
// In Prefix: capture runIdAtStart = RunManager.Instance.DebugOnlyGetState()?.Id (Warn-log if null)
// Pass to HandleVoteAsync via new parameter.
// In ResumeOnMainThread: if runIdAtStart != null AND current Id != runIdAtStart, Warn log + drop resume.
```

Same fail-open semantics as B.2.1's gate. Acceptance check: B.1 Neow regression test (Step 0) must still pass with the guard added.

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

- Missing key → use default.
- Non-integer value → warning + use default. Non-fatal.
- Value < -1 → warning, clamp to -1.
- Both keys are independent; no cross-validation.

### Tests to add to `ModSettingsTests`

- `CardSkipsPerActMissingUsesDefault` (default = 1).
- `CardSkipsPerRunMissingUsesDefault` (default = -1).
- `CardSkipsPerAct_InvalidValue_WarnsAndUsesDefault`.
- `CardSkipsPerRun_NegativeOtherThanMinusOne_ClampsToMinusOne`. <!-- explicit per Reviewer 7 -->
- `CardSkipsPerAct_NegativeFive_ClampsToMinusOne` (e.g., `-5` → `-1`). <!-- NEW v2 -->
- `CardSkipsPerAct_Zero_IsStrict` (parses successfully, no warning; value = 0).
- `CardSkipsPerRun_PositiveValue_Parses` (e.g., 5).

## Failure modes & degradation

Inherits B.1's "fail soft, degrade to vanilla" stance. New + revised failure modes for B.2.1:

| # | Failure mode | Behaviour |
|---|---|---|
| 1 | `CardRewardVotePatch.Prepare` fails | Vote patch silently skips registration. Card rewards play vanilla (no chat vote, but no crash). Skip gate still works (separate patch). |
| 2 | `CardRewardSkipGatePatch.Prepare` fails | Skip gate skips registration. Card vote still works; streamer can bypass via Proceed (vanilla behaviour). |
| 3 | `_Ready` postfix throws | Logs Error, vanilla `_Ready` already completed → rewards screen still functional. No gate this round. |
| 4 | `RewardSkippedFrom` postfix throws | Logs Error. Counter may not be incremented → future gates may be too permissive (fail-open). <!-- CHANGED v2: was confused in v1 — Reviewer 1 nit --> |
| 5 | `_ExitTree` postfix throws | Logs Error. `_activeLabel` may dangle until next `_Ready` overwrites it; `IsInstanceValid` guard catches it. |
| 6 | Reflection failure on `_options` / `_rewardButtons` field | Logs Warn, returns "no card reward" / "can't determine cards" → fail-open. |
| 7 | `DebugOnlyGetState()` returns null at vote start | Logs Warn ("run-ID guard degraded — null state"). Vote proceeds without run-ID guard. <!-- CHANGED v2 --> |
| 8 | `DebugOnlyGetState()` returns null at resume time | Null-safe comparison naturally aborts resume. No crash. <!-- NEW v2 --> |
| 9 | Run-ID guard fires (run abandoned mid-vote) | Logs Warn ("Resume aborted: run changed during vote"), no resume. <!-- CHANGED v2: Warn not Info --> |
| 10 | Reroll mid-vote (cards replaced before resume) | Holder snapshot signature mismatch → resume aborts → chat receipt `vote cancelled — streamer rerolled` → streamer clicks new card → new vote. <!-- CHANGED v2 --> |
| 11 | Settings file completely missing | Mod loads silently with no chat capability. Skip gate detects `Settings` is `Missing`/`Malformed` and degrades to vanilla — no Proceed gating, no chat receipts. Streamer can claim or skip cards normally. <!-- CHANGED v2: explicit policy — Reviewer 1 --> |
| 12 | Save/quit/reload mid-run | Static counters reset on process restart. If StS2 preserves the same `RunState.Id` after reload, `_lastSeenRunId` matches → counter stays at 0 (effectively reset). **Documented as known v0.1 limit; persistence deferred to v0.2 if streamers exploit it.** <!-- NEW v2 — Reviewer 6 --> |
| 13 | AutoSlay running and triggers `SelectCard` programmatically | Vote fires (acceptable behaviour). AutoSlay is off in production streamer play. Documented; no special handling. <!-- NEW v2 — Reviewer 2 --> |
| 14 | Multiplayer (Players.Count > 1) | Both vote patch and skip gate bail. Vanilla card flow runs. <!-- CHANGED v2: explicit for both --> |

## Acceptance gate (operator-validation, runs after unit tests pass)

7-step gate (split from v1's 6 steps). Each is a manual playthrough; mod is B.2.1-ready only when all green.

- **Step 0 — Pure regression check (B.1 features only).** <!-- CHANGED v2: split from v1 Step 0/1 --> Settings present with B.1 keys only (no `cardSkipsPer*`). Mod loads cleanly. Run starts; Neow vote works (chat votes, winner applies); chat connect-once receipt fires. **No card-reward path exercised.** Confirms the new patches don't regress B.1 behaviour. (Run abandoned before first combat.)

- **Step 1 — Card vote happy path (3 successful runs).**
  - chat votes for a card via `#0`/`#1`/`#2`, winning card claimed via dispatcher.Post resume
  - latest-wins on multi-vote-from-one-user
  - both `#N` and bare `N` accepted
  - close receipt fires with correct card name
  - VoteTallyLabel (top-right) shows tally during vote
  - skip-counter label visible near Proceed button
  - skip-counter label updates correctly when cards are claimed (no skip used)

- **Step 2 — Skip used.** With `cardSkipsPerAct: 1`: rewards screen → click Proceed without claiming card → skip allowed → chat receipt `Streamer skipped a card reward (1/1 act)` (run-half suppressed when unlimited) → counter label updates → next combat: rewards screen opens with Proceed disabled (must claim) → click card → vote runs → claim → Proceed enabled.

- **Step 3 — Skip blocked.** With `cardSkipsPerAct: 0` from start: rewards screen opens, Proceed visibly disabled. Streamer must click card → vote runs → claim → Proceed enabled. No way to bypass.

- **Step 4 — Counter resets.** Use `act 2` console command to jump acts → next rewards screen: counter label resets to `1/1 act` → skip usable again. Same for new run via menu (verify `_lastSeenRunId` mismatch resets counters).

- **Step 5 — Multi-reward-type screen.** <!-- NEW v2 --> Find or trigger a rewards screen with both card AND another reward type (gold / potion / boss relic). With `cardSkipsPerAct: 0`: claim card via vote → verify Proceed becomes enabled (vanilla `_skipDisallowed` becomes irrelevant when button transitions to non-Skip mode) → claim or skip the other reward as normal. **If the other reward is locked from skipping after card claim** (the R5 lockout concern), document and add to v0.2 follow-up.

- **Step 6 — Edge cases.**
  - **Mid-vote run abandon** <!-- CHANGED v2 --> — start a card vote, immediately open menu and click Abandon Run, wait 30s for vote timer to expire. Verify run-ID guard fires (`Warn` log: "Resume aborted: run changed during vote"). No crash, no spurious card-add into a dead run.
  - **Mid-vote reroll** if a relic enables it — start vote, click reroll on sub-screen, wait for vote timer. Verify chat receipt `vote cancelled — streamer rerolled` fires. Streamer clicks new card → new vote.
  - **Streamer escape** (via menu) mid-vote — vote runs to normal close in background; resume drops via `IsInstanceValid` check; no crash.
  - **Rapid card clicks** — only first triggers vote; subsequent clicks no-op via `_voteInProgress` guard.

## Open questions

None blocking. One soft question for the implementation phase:

1. **`DisallowSkipping()` lifecycle in multi-reward screens** — does vanilla's `TryEnableProceedButton` self-correct after card claim (button transitions out of Skip mode), allowing other rewards to be skipped? Step 5 of the acceptance gate is the validation point. If self-correction fails, B.2.2 polish adds a `RewardCollectedFrom` postfix to re-evaluate. <!-- CHANGED v2 -->

## Cross-references

- [`docs/superpowers/specs/2026-05-09-plan-b-1-vertical-slice-design-v3.md`](./2026-05-09-plan-b-1-vertical-slice-design-v3.md) — B.1 spec; suspend-and-resume pattern source-of-truth.
- [`docs/superpowers/specs/META-REVIEW-2026-05-10-plan-b-2-1-card-reward-vote-design.md`](./META-REVIEW-2026-05-10-plan-b-2-1-card-reward-vote-design.md) — meta-review with reviewer details, consensus, conflicts, and full Must-do/Should-do/Consider/Reject categorization.
- [`notes/06-followups-and-deferred.md`](../../../notes/06-followups-and-deferred.md) — B.1 completion findings; run-ID guard origin; relic curation deferral; Save/Load loophole follow-up.
- [`decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/CardSelection/NCardRewardSelectionScreen.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/CardSelection/NCardRewardSelectionScreen.cs) — vote patch target.
- [`decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/NRewardsScreen.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/NRewardsScreen.cs) — skip gate target; `DisallowSkipping`/`RewardSkippedFrom`/`_skipDisallowed` mechanism source.
- [`decompiled/sts2/MegaCrit/sts2/Core/AutoSlay/Handlers/Screens/CardRewardScreenHandler.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/AutoSlay/Handlers/Screens/CardRewardScreenHandler.cs) — confirms call path; AutoSlay interaction note.
- [`decompiled/sts2/MegaCrit/sts2/Core/Nodes/Debug/NDevConsole.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Debug/NDevConsole.cs) — vanilla DevConsole.

---

## Optional Enhancements (pick what you want)

10 items from the Consider tier. Tell me which numbers to apply and I'll fold them into v3 (or proceed straight to writing-plans if none).

| # | Change | Reviewer(s) | Effort | Recommendation |
|---|---|---|---|---|
| **1** | **Drop `cardSkipsPerRun` for v0.1.** Single per-act knob; simpler settings, simpler label, simpler receipt. Per-act × acts ≈ same total cap. Conflicts with your brainstorming choice ("Can we have Per Act and Per Run please") — re-decide explicitly. | R2, R5, R7 | trivial | neutral (you chose dual; reviewers say YAGNI) |
| **2** | **Extend `VoteTallyLabel` instead of separate `CardSkipCounterLabel`.** 4 reviewers prefer this. Eliminates anchor risk, dangling-ref bug entirely, and ~80 LOC. Counter only visible during votes (not pre-vote on rewards screen). Conflicts with your stated "close to the proceed button" requirement. | R3, R5, R6, R7 (alt) | small | neutral (your call: spatial vs simpler) |
| **3** | **Pure `SkipBudgetTracker` extraction.** Pull counter logic into a testable class; postfix becomes thin shim. Different from the deferred suspend/resume helper — pure logic only. Improves test ergonomics. | R1, R2 | small | lean yes (matches clean-code preference) |
| **4** | **Patch `StartRun` / `TransitionToAct` for precise reset.** Avoids lazy-by-one-rewards-screen detection. More patch surface. | R6 | small | lean no (lazy is fine) |
| **5** | **`RichTextLabel` for `CardSkipCounterLabel` from day one.** Leaves design space open for colour later (e.g., red when act budget exhausted). | R7 | trivial | lean yes (cheap option value) |
| **6** | **Composite run-id fingerprint as fallback to `DebugOnlyGetState()`.** If the debug method returns null in production, compute a synthetic ID from `Acts.Count + CurrentRoom.NodeId + Health`. Insurance against debug-API stripping. | R7 | small | lean no (only if Step 0 reveals `DebugOnlyGetState` is null — defer until then) |
| **7** | **Card name uniqueness disambiguation in receipts.** Suffix duplicate names (e.g., `Strike, Strike+, Strike+`). Edge case; rare in practice. | R5 | trivial | lean no (defer until observed) |
| **8** | **Skip-without-looking detection** via `_Ready` postfix on `NCardRewardSelectionScreen`. Differentiate receipt: "Streamer skipped a card reward without viewing it (...)". | R2 | small | lean no (R6 reasoning preserved — budget bounds bypass) |
| **9** | **`notes/06` entry listing every reflected sts2.dll member B.2.1 depends on.** Single update point when MegaCrit ships breaking patches. | R2 | trivial | lean yes (maintainability win) |
| **10** | **TiLog `[SlayTheStreamer2]` prefix.** Mod tag in every log line for greppability. | R4 | trivial | lean yes (cheap; helps debugging) |
