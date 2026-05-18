# 2026-05-18 — Plan B.3.2: Act-variant vote (design v3)

**Date**: 2026-05-18 (v3)
**Status**: Design v3 post-round-2 meta-review. Implementation plan pending plan + approval.
**Slice**: B.3.2 (post-B.3.1) — chat picks Act 1 variant (Underdocks vs Overgrowth) at run start, before vanilla's coin-flip in `GetRandomList` would otherwise pick.
**Scope**: New Harmony patch on `StartRunLobby.BeginRunLocally`. New popup `Control` rendering vertical 50/50 split of each variant's combat-room background + entry banner. Suspend-and-resume pattern. No changes to existing voting infrastructure.

**v3 changelog**: Folds in 8 must-do and 10 should-do changes from the [round-2 meta-review](META-REVIEW-round2-2026-05-18-plan-b-3-2-act-variant-vote-design.md). Annotations: `<!-- CHANGED v3: reason — Reviewers X, Y -->`. The largest change is pivoting from a never-existed "suppress + send custom" no-votes receipt mechanism to using `VoteSession`'s **existing** `formatReceipt` callback parameter.

## TL;DR

Vanilla picks the Act 1 variant via a coin-flip inside [`ActModel.GetRandomList`](../../../decompiled/sts2/MegaCrit/sts2/Core/Models/ActModel.cs#L414), called once at the start of [`StartRunLobby.BeginRunLocally:411`](../../../decompiled/sts2/MegaCrit/sts2/Core/Multiplayer/Game/Lobby/StartRunLobby.cs#L411). The next line — `list[0] = GetAct(Act1) ?? list[0]` ([line 412](../../../decompiled/sts2/MegaCrit/sts2/Core/Multiplayer/Game/Lobby/StartRunLobby.cs#L412)) — is a vanilla override hook for the (currently UI-hidden) `_actDropdown`. We co-opt it from chat.

**Mechanism.** Harmony prefix on `BeginRunLocally`, returns `false` to suspend. We start a 30s chat vote with two options (`#0 Overgrowth` / `#1 Underdocks`), passing a custom `formatReceipt` callback to `VoteSession` that intercepts close-receipt formatting and (a) detects `snapshot.NoVotesReceived`, (b) substitutes accurate text in that case. <!-- CHANGED v3: M1 — use existing formatReceipt hook (Reviewers R2-1, R2-2, R2-3) --> The callback also writes the no-votes flag to a captured `VoteOutcome` shared object so the resume path can read it without needing a `session.Snapshot` accessor (which doesn't exist). <!-- CHANGED v3: M2 — Reviewers R2-1, R2-2 -->

We then `await session.AwaitWinnerAsync()`, read the captured outcome, and on the Godot main thread write `__instance.Act1 = winnerKey`, reflectively re-invoke `BeginRunLocally(seed, modifiers)`, restore `Act1` in `finally`. Vanilla's existing line 412 picks up the chat winner via the same code path the dropdown would have used.

**Net change**: ~540 LOC across 4 new files + 1 edit (settings). One new Harmony patch class, one new popup `Control`, one pure helper (`ActVariantVoteResolver`) with bail-logic extraction, one DTO (`ActVariantOption`). No `src/Ti/*` edits — `VoteSession`'s existing `formatReceipt` parameter supplies the substitution hook.

**Risk surface**: Comparable to B.3 — bail-condition surface, lifecycle/cancellation semantics, asset-path uncertainty. Round 2 of meta-review surfaced no architectural shifts; remaining risks are uncertainty items resolved during the spike.

## Goals

- **Chat picks the Act 1 variant** when chat is connected and the streamer hasn't explicitly pinned a variant.
- **Works in both Standard and Custom game modes.**
- **Reuse existing voting infrastructure** verbatim — `VoteCoordinator`, `VoteSession`, `VoteTallyLabel`, including the `formatReceipt` callback for per-session receipt substitution.
- **Forward-compatible internals** — `ActVariantVotePopup` is parameterized on `IReadOnlyList<ActVariantOption>` + `ActVariantPopupMode`. <!-- CHANGED v3: mode parameter — M4 -->
- **Preserve TI/Game seam** — popup is fully MegaCrit-free (probes are `Func<bool>` injected from patch). <!-- CHANGED v3: M3 — Reviewer R2-1 -->
- **No regression** to existing voting features (B.1 / B.2.1 / B.2.2 / B.3 / B.3.1).

## Non-goals

- **Act 2 / Act 3 variant votes.** Vanilla has no other variants today.
- **Generic act-transition trigger surface.** YAGNI.
- **Multiplayer.** Singleplayer-only.
- **Variant pool expansion** (deferred per memory 31663).
- **Settings UI surface** for the new toggle — JSON-only for v1.
- **Localized receipts** — English-only.
- **Unlock gating** of the candidate pool.
- **Mid-vote viewport resize support** — static aspect ratios only.

## Architecture

### File layout

```
src/Game/DecisionVotes/
  ActVariantVotePatch.cs       — NEW. Harmony patch on StartRunLobby.BeginRunLocally.
                                  Holds abandonment probe + cancellation/no-votes
                                  shared state. EXCLUDED from test compile.
  ActVariantVoteResolver.cs    — NEW. Pure static helpers (BuildCandidates,
                                  ResolveWinnerKey, ShouldBail, ActVariantAssetPaths,
                                  custom-formatter helper).
                                  INCLUDED in test compile via auto-include.

src/Game/Ui/
  ActVariantVotePopup.cs       — NEW. Godot Control + CanvasLayer. Takes Func<bool>
                                  probes injected from patch. EXCLUDED from test compile.
  ActVariantOption.cs          — NEW. DTO with nullable asset paths + hex fallback color.
                                  INCLUDED via surgical <Compile Include>.

src/Game/Bootstrap/
  ModSettings.cs               — EDIT. Add VoteOnActVariant, ForceL3PopupFallback.

(no src/Ti/* changes — VoteSession's existing formatReceipt parameter supplies
                       per-session receipt substitution; existing EnglishReceipts
                       generic formatters cover open/tally/winner/tie cases.)
```

### TI/Game seam preserved <!-- CHANGED v3: popup probe seam fixed — M3, Reviewer R2-1 -->

- `ActVariantOption` carries primitives only: `(int Index, string Key, string Title, string? BackgroundPath, string? BannerPath, string FallbackColorHex)`. No `Godot.*`, no `MegaCrit.Sts2.*`.
- `ActVariantVotePopup`'s public interface AND implementation are **fully MegaCrit-free**. Abandonment detection is injected as `Func<bool> shouldCancel` from the patch (mirrors `BossVotePopup`'s `isOccludingOverlayVisible` / `isRunDying` pattern from B.3).
- `ActVariantVotePatch` is the only file touching `MegaCrit.Sts2.*` types (`StartRunLobby`, `ActModel`, `ModelDb`, plus the spike-output abandonment probe target).
- `ActVariantVoteResolver` is pure CLR.

### Vanilla API surface

- [`StartRunLobby.BeginRunLocally(string seed, List<ModifierModel> modifiers)`](../../../decompiled/sts2/MegaCrit/sts2/Core/Multiplayer/Game/Lobby/StartRunLobby.cs#L408) — `private void` instance method. Harmony target.
- [`StartRunLobby.Act1`](../../../decompiled/sts2/MegaCrit/sts2/Core/Multiplayer/Game/Lobby/StartRunLobby.cs#L87) — `public string` get/set. Write target; restored after re-invoke.
- [`StartRunLobby.GetAct(string)`](../../../decompiled/sts2/MegaCrit/sts2/Core/Multiplayer/Game/Lobby/StartRunLobby.cs#L441) — vanilla's decoder.
- `StartRunLobby.Players` — `List<LobbyPlayer>`. Multiplayer bail.
- `ModelDb.Act<Overgrowth>()` / `ModelDb.Act<Underdocks>()`.
- `PreloadManager.Cache.GetTexture(string)` — synchronicity verified by spike.
- [`VoteCoordinator.Start(label, options, duration, receipts?, parsing?, formatReceipt?)`](../../../src/Ti/Voting/VoteCoordinator.cs#L45) — the `formatReceipt: Func<VoteSnapshot, ReceiptKind, string>?` parameter is what we use for per-session receipt substitution. <!-- CHANGED v3: M1 — verified in codebase 2026-05-18 -->

## Trigger mechanics — suspend-and-resume

### Bail order (Prefix)

```csharp
[HarmonyPatch(typeof(StartRunLobby), "BeginRunLocally",
              new[] { typeof(string), typeof(List<ModifierModel>) })]
internal static class ActVariantVotePatch {
    private static int _voteInProgress;
    private static int _resumeInProgress;
    // <!-- CHANGED v3: M6 — _pending static removed; local-only — Reviewer R2-2 -->
    // _multiplayerWarnFired is intentionally process-lifetime: a streamer who
    // bounces in and out of MP doesn't need repeat warnings on every Embark.
    // <!-- CHANGED v3: S6 annotation — Reviewer R2-2 -->
    private static int _multiplayerWarnFired;

    private sealed class PendingActVariantVote {
        public int Cancelled;
        public int NoVotes;     // <!-- CHANGED v3: M2 — written by formatReceipt callback so resume can read no-votes without session.Snapshot -->
    }

    private static readonly Lazy<MethodInfo?> _beginRunLocallyMethod =
        new(() => AccessTools.Method(typeof(StartRunLobby), "BeginRunLocally",
                                     new[] { typeof(string), typeof(List<ModifierModel>) }));

    static bool Prefix(StartRunLobby __instance, string seed, List<ModifierModel> modifiers) {
        // 1. Synthetic resume → let vanilla through with chat-set Act1.
        if (_resumeInProgress == 1) return true;

        // 2. Concurrent-vote suppression (atomic acquire moved here in v2 to close
        //    the chat-disconnect race).
        if (Interlocked.CompareExchange(ref _voteInProgress, 1, 0) != 0) {
            TiLog.Debug("[SlayTheStreamer2][act-variant-vote] repeat click during open vote; suppressed");
            return false;
        }

        try {
            // 3..7: pure-CLR bail logic — delegated to ActVariantVoteResolver.ShouldBail.
            // The atomic-acquire bails (#1 ResumeInProgress, #2 VoteInProgress) are
            // handled inline above; ShouldBail covers the rest.
            // <!-- CHANGED v3: M8 — ShouldBail enum pruned; Prefix actually calls it — Reviewers R2-1, R2-3 -->
            int? playerCount = TryGetPlayerCount(__instance);
            var coordinator = Voter.Default;
            var chatState = coordinator?.Chat.State ?? ChatConnectionState.Disconnected;
            var candidates = ActVariantVoteResolver.BuildCandidates();

            var reason = ActVariantVoteResolver.ShouldBail(
                settingsEnabled: ModSettings.Current.VoteOnActVariant,
                playerCount: playerCount ?? 1,
                chatState: chatState,
                act1Value: __instance.Act1,
                candidateCount: candidates.Count);

            if (reason is not ActVariantVoteResolver.BailReason.None) {
                LogBailAndRelease(reason, __instance);
                return true;
            }
            // After ShouldBail.None, coordinator is guaranteed non-null (chat readable
            // was a precondition of that bail reason being None).

            return PrefixContinue(__instance, seed, modifiers, candidates, coordinator!);
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][act-variant-vote] Prefix threw; bailing to vanilla", ex);
            Interlocked.Exchange(ref _voteInProgress, 0);
            return true;
        }
    }
}
```

### Suspend-and-resume shape

```csharp
private static bool PrefixContinue(
        StartRunLobby instance,
        string seed,
        List<ModifierModel> modifiers,
        IReadOnlyList<ActVariantOption> candidates,
        VoteCoordinator coordinator) {

    var capturedModifiers = modifiers.ToList();

    // Pre-warm and obtain L1/L3 mode. <!-- CHANGED v3: M4 — Reviewer R2-1 -->
    var prewarm = PreWarmAssets(candidates, ModSettings.Current.ForceL3PopupFallback);

    // Local-only pending state; no static field. <!-- CHANGED v3: M6 -->
    var pending = new PendingActVariantVote();

    // Custom formatReceipt callback: substitutes the no-votes close text AND
    // sets pending.NoVotes side-channel so HandleVoteAsync can read it without
    // session.Snapshot. <!-- CHANGED v3: M1 + M2 — Reviewers R2-1, R2-2, R2-3 -->
    Func<VoteSnapshot, ReceiptKind, string> formatReceipt = (snapshot, kind) => {
        if (kind == ReceiptKind.Close && snapshot.NoVotesReceived) {
            Interlocked.Exchange(ref pending.NoVotes, 1);
            return "Act 1 variant vote closed: no votes received — vanilla random pick stands.";
        }
        // Delegate to EnglishReceipts for all other cases (open, tally, winner, tie).
        return kind switch {
            ReceiptKind.Open           => EnglishReceipts.FormatOpen(snapshot),
            ReceiptKind.PeriodicTally  => EnglishReceipts.FormatPeriodicTally(snapshot),
            ReceiptKind.Close          => EnglishReceipts.FormatClose(snapshot),
            _ => EnglishReceipts.FormatClose(snapshot),
        };
    };

    VoteSession? session = null;
    try {
        var labels = candidates.Select(c => c.Title).ToList();
        session = coordinator.Start(
            label: "Act 1 variant",
            options: labels,
            duration: TimeSpan.FromSeconds(30),
            receipts: null,                  // VoteReceiptPolicy.Default
            parsing: null,
            formatReceipt: formatReceipt);    // <-- per-session substitution

        // Probe lives in patch; popup receives only the Func<bool>.
        // <!-- CHANGED v3: M3 — Reviewer R2-1 -->
        Func<bool> shouldCancel = () => IsRunStartAbandoned(instance);
        Action onUserAbandoned = () => Interlocked.Exchange(ref pending.Cancelled, 1);

        var popup = new ActVariantVotePopup(
            options: candidates,
            session: session,
            dispatcher: coordinator.Dispatcher,
            mode: prewarm.Mode,                // <-- L1/L3 mode propagated
            shouldCancel: shouldCancel,
            onUserAbandoned: onUserAbandoned);
        coordinator.Dispatcher.Post(() => popup.Open());

        _ = HandleVoteAsync(instance, seed, capturedModifiers, session, candidates, coordinator, pending);
    } catch (Exception ex) {
        TiLog.Error("[SlayTheStreamer2][act-variant-vote] PrefixContinue threw; cancelling started session", ex);
        try { session?.Cancel(); } catch { /* swallow */ }
        Interlocked.Exchange(ref _voteInProgress, 0);
        return true;
    }
    return false;
}

private static async Task HandleVoteAsync(
        StartRunLobby instance,
        string seed,
        List<ModifierModel> capturedModifiers,
        VoteSession session,
        IReadOnlyList<ActVariantOption> candidates,
        VoteCoordinator coordinator,
        PendingActVariantVote pending) {
    try {
        coordinator.Dispatcher.Post(() => VoteTallyLabel.AttachTo(session));

        int? winnerIndex = null;
        try {
            int idx = await session.AwaitWinnerAsync();
            if (idx >= 0 && idx < candidates.Count) winnerIndex = idx;
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][act-variant-vote] AwaitWinnerAsync threw", ex);
        }

        // Cancellation dominates no-votes in receipt ordering.
        // <!-- CHANGED v3: M5 — Reviewer R2-1 -->
        bool cancelled = Volatile.Read(ref pending.Cancelled) == 1;
        bool noVotes = Volatile.Read(ref pending.NoVotes) == 1;

        string winnerKey;
        if (cancelled) {
            winnerKey = "random";
            // Cancellation receipt is sent by ResumeOnMainThread; no separate
            // no-votes receipt fires even if pending.NoVotes is also set.
        } else if (noVotes) {
            winnerKey = "random";
            // The no-votes receipt was ALREADY sent by the formatReceipt callback
            // when VoteSession internally fired ReceiptKind.Close. No additional
            // send needed here.
        } else {
            winnerKey = ActVariantVoteResolver.ResolveWinnerKey(candidates, winnerIndex);
        }

        TiLog.Info($"[SlayTheStreamer2][act-variant-vote] resume: winnerKey={winnerKey} (cancelled={cancelled}, noVotes={noVotes}, seed={seed})");
        // <!-- C2 candidate: seed logging — Reviewer R2-3 ADD: applied -->

        coordinator.Dispatcher.Post(() =>
            ResumeOnMainThread(instance, seed, capturedModifiers, winnerKey, cancelled, pending));
    } catch (Exception ex) {
        TiLog.Error("[SlayTheStreamer2][act-variant-vote] HandleVoteAsync threw; fallback resume", ex);
        try {
            coordinator.Dispatcher.Post(() =>
                ResumeOnMainThread(instance, seed, capturedModifiers, "random", cancelled: true, pending));
        } catch (Exception postEx) {
            TiLog.Error("[SlayTheStreamer2][act-variant-vote] fallback Post threw; resetting flags", postEx);
            Interlocked.Exchange(ref _resumeInProgress, 0);
            Interlocked.Exchange(ref _voteInProgress, 0);
        }
    }
}

private static void ResumeOnMainThread(
        StartRunLobby instance,
        string seed,
        List<ModifierModel> capturedModifiers,
        string winnerKey,
        bool cancelled,
        PendingActVariantVote pending) {
    Interlocked.Exchange(ref _resumeInProgress, 1);
    string? previousAct1 = null;
    try {
        if (cancelled) {
            TiLog.Info("[SlayTheStreamer2][act-variant-vote] resume: vote cancelled; aborting without re-invoke");
            SendCancellationReceipt();
            return;
        }

        // previousAct1 capture is always "random" in the current bail order
        // (bail #6 rejects non-"random" Act1 values). Captured anyway for
        // defensive symmetry — if the bail order ever changes, this still works.
        // <!-- CHANGED v3: S7 annotation — Reviewer R2-2 -->
        previousAct1 = instance.Act1;

        if (winnerKey != "random") {
            instance.Act1 = winnerKey;
            TiLog.Info($"[SlayTheStreamer2][act-variant-vote] resume: Act1 = {winnerKey} (previous: {previousAct1})");
        }

        var method = _beginRunLocallyMethod.Value;
        if (method is null) {
            TiLog.Error("[SlayTheStreamer2][act-variant-vote] _beginRunLocallyMethod is null; cannot re-invoke");
            return;
        }

        try {
            method.Invoke(instance, new object?[] { seed, capturedModifiers });
        } catch (TargetInvocationException tie) {
            // Fallback re-invoke: align winnerKey = "random" so finally restoration
            // semantics are clean. <!-- CHANGED v3: S4 — Reviewer R2-3 -->
            // CAVEAT: this fallback is conditional on Spike #4 (BeginRunLocally
            // idempotency). If the spike finds the method is NOT safe to retry
            // after a partial failure, REMOVE this fallback before ship and
            // accept that the player must restart on rare reflection failures.
            // <!-- CHANGED v3: gate fallback behind spike — Reviewer R2-1 #5 -->
            TiLog.Error($"[SlayTheStreamer2][act-variant-vote] re-invoke threw; attempting fallback (spike-gated)",
                tie.InnerException ?? tie);
            winnerKey = "random";  // align with finally so restoration is consistent
            try {
                instance.Act1 = "random";
                method.Invoke(instance, new object?[] { seed, capturedModifiers });
            } catch (TargetInvocationException fallbackTie) {
                TiLog.Error("[SlayTheStreamer2][act-variant-vote] fallback re-invoke threw; player may be soft-locked",
                    fallbackTie.InnerException ?? fallbackTie);
            } catch (Exception fallbackEx) {
                TiLog.Error("[SlayTheStreamer2][act-variant-vote] fallback re-invoke threw (non-reflection)", fallbackEx);
            }
        }
    } catch (TargetInvocationException tie) {
        TiLog.Error("[SlayTheStreamer2][act-variant-vote] resume threw (reflection)", tie.InnerException ?? tie);
    } catch (Exception ex) {
        TiLog.Error("[SlayTheStreamer2][act-variant-vote] resume threw", ex);
    } finally {
        // Restore Act1 if we wrote it AND we didn't fall back to "random".
        if (previousAct1 is not null && winnerKey != "random") {
            try { instance.Act1 = previousAct1; } catch { /* swallow */ }
        }
        Interlocked.Exchange(ref _resumeInProgress, 0);
        Interlocked.Exchange(ref _voteInProgress, 0);
    }
}

private static bool IsRunStartAbandoned(StartRunLobby instance) {
    // Spike #5 deliverable — abandonment probe specific to StartRunLobby /
    // NCharacterSelectScreen lifecycle. MegaCrit references kept in the patch.
    // <!-- CHANGED v3: M3 — Reviewer R2-1 -->
    try { return /* spike-output probe */ false; } catch { return false; }
}

private static void SendCancellationReceipt() {
    var coordinator = Voter.Default;
    if (coordinator?.Chat?.State != ChatConnectionState.ConnectedReadWrite) return;
    _ = coordinator.Chat.SendMessageAsync(
        "Act 1 variant vote cancelled — run-start abandoned.",
        OutgoingMessagePriority.Normal);
}
```

### Cancellation

`ActVariantVotePopup` polls `shouldCancel()` each frame. On firing, it sets `_userAbandoned = true`, calls `onUserAbandoned()` (which writes `pending.Cancelled = 1`), and calls `session.Cancel()`. `HandleVoteAsync` reads `pending.Cancelled` after `AwaitWinnerAsync` returns; cancellation dominates the no-votes flag.

`session.Cancel()` is assumed idempotent — confirmed during spike (see Spike deliverable #9). <!-- CHANGED v3: idempotency note — Reviewer R2-3 -->

ESC handling: popup overrides `_Input` (not `_UnhandledInput` — that fires too late if a parent control consumes ESC first). Calls `GetViewport().SetInputAsHandled()` and triggers the same cancellation path. <!-- CHANGED v3: S5 alternative phrasing — Reviewer R2-1 #12 -->

## Candidate pool + bail logic

```csharp
// ActVariantVoteResolver.cs — pure CLR, in test compile via auto-include.

internal static class ActVariantVoteResolver {
    internal static class ActVariantAssetPaths {
        internal const string? OvergrowthCombatBackground = null;  // spike output
        internal const string? UnderdocksCombatBackground = null;  // spike output
        internal const string? OvergrowthEntryBanner = null;       // spike output
        internal const string? UnderdocksEntryBanner = null;       // spike output

        // FallbackColorHex format: 6-digit RRGGBB (no leading '#', no alpha).
        // <!-- CHANGED v3: S10 — Reviewer R2-1 -->
        internal const string OvergrowthFallbackHex = "A78A67";
        internal const string UnderdocksFallbackHex = "9F95A5";
    }

    internal static IReadOnlyList<ActVariantOption> BuildCandidates() => new[] {
        new ActVariantOption(0, "overgrowth", "Overgrowth",
            ActVariantAssetPaths.OvergrowthCombatBackground,
            ActVariantAssetPaths.OvergrowthEntryBanner,
            ActVariantAssetPaths.OvergrowthFallbackHex),
        new ActVariantOption(1, "underdocks", "Underdocks",
            ActVariantAssetPaths.UnderdocksCombatBackground,
            ActVariantAssetPaths.UnderdocksEntryBanner,
            ActVariantAssetPaths.UnderdocksFallbackHex),
    };

    internal static string ResolveWinnerKey(IReadOnlyList<ActVariantOption> options, int? winnerIndex) {
        if (winnerIndex is null) return "random";
        if (winnerIndex < 0 || winnerIndex >= options.Count) return "random";
        return options[winnerIndex.Value].Key;
    }

    // <!-- CHANGED v3: M8 — enum pruned to TESTABLE bail reasons only.
    // ResumeInProgress and VoteInProgress are handled inline in Prefix
    // (atomic-acquire semantics that pure functions can't replicate).
    // Those bail paths are verified by Gates 7, 12 instead of unit tests.
    // — Reviewers R2-1, R2-3 -->
    internal enum BailReason {
        None,
        SettingsOff,
        Multiplayer,
        ChatUnreadable,
        Act1Pinned,
        PoolDegenerate
    }

    internal static BailReason ShouldBail(
            bool settingsEnabled,
            int playerCount,
            ChatConnectionState chatState,
            string act1Value,
            int candidateCount) {
        if (!settingsEnabled) return BailReason.SettingsOff;
        if (playerCount > 1) return BailReason.Multiplayer;
        if (chatState is not (ChatConnectionState.ConnectedReadWrite
                            or ChatConnectionState.ConnectedReadOnly))
            return BailReason.ChatUnreadable;
        if (!string.Equals(act1Value, "random", StringComparison.Ordinal))
            return BailReason.Act1Pinned;
        if (candidateCount <= 1) return BailReason.PoolDegenerate;
        return BailReason.None;
    }
}

// ActVariantOption.cs — DTO, in test compile via surgical include.
internal readonly record struct ActVariantOption(
    int Index,
    string Key,
    string Title,
    string? BackgroundPath,
    string? BannerPath,
    string FallbackColorHex);
```

## Asset discovery — research spike

The popup needs **4 total assets**: 2 backgrounds + 2 banners. <!-- CHANGED v3: S3 — Reviewer R2-1 #9; was wrongly "8 total" -->

### Spike deliverables (9 items)

1. **Asset paths**: 4 verified `res://...` paths (or `null` per asset if not located).
2. **Banner anchor convention**: verify before locking `column_width / 2, gameplay_height / 3`.
3. **`Cache.GetTexture` synchronicity**: verify returns fully-loaded texture (not async placeholder).
4. **`BeginRunLocally` idempotency**: confirm safe to call twice with same args. If unsafe, REMOVE the fallback re-invoke (S4).
5. **Cancellation probe** for `IsRunStartAbandoned`. **Candidate probes** (refined): poll `NCharacterSelectScreen.Instance` for null/visibility change; subscribe to a Godot scene-tree-change signal; check `SceneTree.CurrentScene` identity; observe a known character-select-state property. <!-- CHANGED v3: S1 — bad `instance.GetParent()` example removed; replaced with viable candidates — Reviewer R2-2 -->
6. **Runtime `Act1` read-site validation** via temporary postfix.
7. **`BeginRunLocally` call-site audit**.
8. **Gameplay-area surface** identified for popup `CanvasLayer` parenting (mirror `BossVotePopup`'s pattern; verify at 3 tested resolutions).
9. **`VoteSession.Cancel()` idempotency**: confirm `session.Cancel()` is safe to call twice (popup's `_Input` ESC handler + frame-poll `shouldCancel()` can both fire). <!-- CHANGED v3: added — Reviewer R2-3 -->

### L3 fallback (all-or-nothing)

If any of the 4 assets is not locatable (or `ForceL3PopupFallback` is set), popup degrades to L3 across both columns: full-height `ColorRect` per column with the variant's `FallbackColorHex`, plus centered title `Label`. The L3/L1 decision is made by `PreWarmAssets` and propagated to the popup via `ActVariantPopupMode`. <!-- CHANGED v3: M4 -->

## Pre-warm — returns mode

```csharp
// <!-- CHANGED v3: M4 — Reviewer R2-1 -->
internal enum ActVariantPopupMode { L1Textures, L3Fallback }

internal readonly record struct ActVariantPrewarmResult(
    ActVariantPopupMode Mode,
    int Succeeded,
    int Total,
    long ElapsedMs);

private static ActVariantPrewarmResult PreWarmAssets(
        IReadOnlyList<ActVariantOption> candidates,
        bool forceL3) {
    var sw = Stopwatch.StartNew();

    if (forceL3) {
        sw.Stop();
        // Normalized log format matches success path. <!-- CHANGED v3: S8 — Reviewer R2-2 -->
        TiLog.Info($"[SlayTheStreamer2][act-variant-vote] pre-warm: 0/0 assets in {sw.ElapsedMilliseconds}ms (mode=L3, reason=ForceL3PopupFallback)");
        return new ActVariantPrewarmResult(ActVariantPopupMode.L3Fallback, 0, 0, sw.ElapsedMilliseconds);
    }

    int succeeded = 0, total = 0;
    foreach (var option in candidates) {
        foreach (var path in new[] { option.BackgroundPath, option.BannerPath }) {
            if (string.IsNullOrEmpty(path)) continue;
            total++;
            try {
                _ = PreloadManager.Cache.GetTexture(path);
                succeeded++;
            } catch (Exception ex) {
                TiLog.Warn($"[SlayTheStreamer2][act-variant-vote] preload failed for {option.Key} ({path}): {ex.Message}");
            }
        }
    }
    sw.Stop();
    var mode = (total > 0 && succeeded == total)
        ? ActVariantPopupMode.L1Textures
        : ActVariantPopupMode.L3Fallback;
    var reason = mode == ActVariantPopupMode.L1Textures ? "all assets loaded"
               : total == 0 ? "no paths configured"
               : $"{succeeded}/{total} assets loaded";
    TiLog.Info($"[SlayTheStreamer2][act-variant-vote] pre-warm: {succeeded}/{total} assets in {sw.ElapsedMilliseconds}ms (mode={mode}, reason={reason})");
    return new ActVariantPrewarmResult(mode, succeeded, total, sw.ElapsedMilliseconds);
}
```

## Popup UI structure

Same tree shape as v2 (Control inside each `PanelContainer` for free-positioning). The popup is fully MegaCrit-free; abandonment detection is the injected `shouldCancel` callback. `mode` parameter determines whether banners and combat-bgs render or are replaced by `FallbackColorHex` + title label.

```csharp
// ActVariantVotePopup.cs — sketch
internal sealed partial class ActVariantVotePopup : Control {
    private readonly IReadOnlyList<ActVariantOption> _options;
    private readonly VoteSession _session;
    private readonly IMainThreadDispatcher _dispatcher;
    private readonly ActVariantPopupMode _mode;
    private readonly Func<bool> _shouldCancel;
    private readonly Action _onUserAbandoned;
    private bool _userAbandoned;
    private CanvasLayer? _canvasLayer;

    public ActVariantVotePopup(
        IReadOnlyList<ActVariantOption> options,
        VoteSession session,
        IMainThreadDispatcher dispatcher,
        ActVariantPopupMode mode,
        Func<bool> shouldCancel,
        Action onUserAbandoned) {
        _options = options; _session = session; _dispatcher = dispatcher;
        _mode = mode; _shouldCancel = shouldCancel; _onUserAbandoned = onUserAbandoned;
    }

    // <!-- CHANGED v3: M3 — Open() instead of Show() to avoid Godot Control.Show() collision -->
    // (renamed in v2 already, kept for clarity)
    public void Open() {
        _canvasLayer = BuildNodeTree();
        // Parent to the gameplay-area surface identified by Spike #8.
        var gameplayAreaParent = /* spike output */ GetGameplayAreaParent();
        gameplayAreaParent.AddChild(_canvasLayer);
        _session.TallyChanged += OnTally;
        _session.Closed += OnClosed;
    }

    public override void _Process(double delta) {
        if (_userAbandoned) return;
        if (_shouldCancel()) {
            _userAbandoned = true;
            _onUserAbandoned();
            _session.Cancel();
        }
    }

    public override void _Input(InputEvent @event) {
        // _Input runs BEFORE _UnhandledInput; popup gets ESC even if a parent
        // would consume it via _UnhandledInput. <!-- CHANGED v3: S5 — Reviewer R2-1 #12 -->
        if (_userAbandoned) return;
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape }) {
            _userAbandoned = true;
            _onUserAbandoned();
            _session.Cancel();
            GetViewport().SetInputAsHandled();
        }
    }

    private void OnClosed(object? sender, VoteSession session) {
        _session.TallyChanged -= OnTally;
        _session.Closed -= OnClosed;
        _canvasLayer?.QueueFree();
    }

    // ... node-tree construction respects _mode for L1 vs L3 ...
}
```

### Sizing (gameplay-area-aware)

Same as v2 — anchor to the 4:3 gameplay area `Control` identified by Spike #8, compute `column_width / 2, gameplay_height / 3` from gameplay-area `Size`, no mid-vote reflow.

## Settings

```jsonc
{
  "voteOnActVariant": true,
  "forceL3PopupFallback": false
}
```

```csharp
public bool VoteOnActVariant      { get; init; } = true;
public bool ForceL3PopupFallback  { get; init; } = false;
```

`ForceL3PopupFallback` is logged both in pre-warm telemetry (as `reason=ForceL3PopupFallback`) and at vote-open time so operator validation can see why textures are absent without grepping. <!-- CHANGED v3: S8 + R2-1 nit -->

## Receipts

**No `EnglishReceipts.cs` edits.** `VoteCoordinator.Start(...)` already accepts a `Func<VoteSnapshot, ReceiptKind, string>? formatReceipt` parameter ([VoteSession.cs:67](../../../src/Ti/Voting/VoteSession.cs#L67)) — B.3.2 passes a custom callback that:

1. For `ReceiptKind.Close` when `snapshot.NoVotesReceived`: returns the custom no-votes text AND side-channels `pending.NoVotes = 1` so `HandleVoteAsync` can read the outcome without `session.Snapshot` (which doesn't exist publicly).
2. For all other cases: delegates to `EnglishReceipts.FormatOpen` / `FormatPeriodicTally` / `FormatClose`.

This is the existing per-session substitution mechanism; v2's pseudocode incorrectly assumed `session.Snapshot` was a public property. <!-- CHANGED v3: M1 + M2 — Reviewers R2-1, R2-2, R2-3 -->

What chat sees:
- **Open** (delegated to `FormatOpen`): `"Vote [NN]: Act 1 variant! Type 0, 1 — 30s left."`
- **Periodic tally** (delegated to `FormatPeriodicTally`): `"Vote: 0=5 1=3, 22s left."`
- **Close — winner** (delegated to `FormatClose`): `"Chat chose 0: Overgrowth."`
- **Close — tie** (delegated to `FormatClose`): `"Tie between 0 Overgrowth and 1 Underdocks — chat chose 0: Overgrowth randomly."`
- **Close — no votes** (custom substitution in callback): `"Act 1 variant vote closed: no votes received — vanilla random pick stands."`
- **Cancellation** (raw send via `coordinator.Chat.SendMessageAsync`, gated on `ConnectedReadWrite`): `"Act 1 variant vote cancelled — run-start abandoned."`

Receipt-policy cadence reuses `VoteReceiptPolicy.Default`. Periodic-tally dedup is on structural tally state.

## Operator-validation gates

**15 gates total.** <!-- CHANGED v3: M7 — Reviewers R2-1, R2-3 -->

| # | Gate | How verified |
|---|------|--------------|
| 1 | Vote fires on Embark click | Click Embark → popup appears, mouse interaction with character-select blocked, ESC cancels vote. <!-- CHANGED v3: S5 wording — Reviewer R2-1 #13 --> `godot.log` shows `[act-variant-vote] opening vote`. |
| 2 | Winner applied | Vote `#0` or `#1` → run starts with chat-chosen variant; first combat room enemy set matches. |
| 3 | No-winner fallback | Chat silent → 30s timeout → custom no-votes receipt sent via formatReceipt callback → run starts with vanilla random pick. `godot.log` shows `noVotes=True`. |
| 4 | Settings toggle off | `voteOnActVariant: false` → no vote fires. |
| 5 | Pool degeneracy guard | Defensive — not exercisable in vanilla. |
| 6 | Cancellation | Embark, ESC → popup tears down, chat receives cancellation receipt only (no no-votes receipt even though `NoVotesReceived` may be true), NO run starts. |
| 7 | Spam-Embark guard | Two quick Embark clicks → second click suppressed. Verifies `_voteInProgress` atomic-acquire (NOT covered by `ShouldBail` unit tests per M8). |
| 8 | Pre-warm telemetry | `godot.log` shows `pre-warm: N/M assets in Tms (mode=L1|L3, reason=...)`. ≤ 100ms. |
| 9 | Sealed Deck coexistence | Run with Sealed Deck modifier → vote still fires; run proceeds with chat pick + Sealed Deck. |
| 10 | Receipt delivery | Open + ≥1 tally + close receipts all arrive. **Validated only in `ConnectedReadWrite`.** In `ConnectedReadOnly`, the vote counts chat input but receipt sends are skipped (per `SendCancellationReceipt`'s state gate at line 224); Gate 10 is N/A and the operator should confirm no exceptions in `godot.log`. <!-- CHANGED v3.1: Optional #4 — Reviewer R2-1 #14 --> |
| 11 | Save-quit preservation | Mid-run save-quit + Continue → chat-picked variant preserved. |
| 12 | Embark→ESC→Embark cycle | Verify atomic state resets correctly between abandoned + fresh votes. Also verifies `_voteInProgress` clearing (M8 coverage). |
| 13 | Chat disconnect mid-vote | Disconnect Twitch IRC mid-vote → vote times out, vanilla pick stands, no crash. |
| 14 | **Multi-resolution popup correctness** at 3 tested resolutions (1/3-monitor windowed, 1920×1080, ultrawide 1440 fullscreen). Verify: <!-- CHANGED v3: S2 — Reviewer R2-2 --> (a) popup centered in 4:3 gameplay area (not raw window); (b) backgrounds preserve aspect ratio (no horizontal/vertical squish); (c) banners stay inside column boundary (no bleed across divider); (d) `CanvasLayer` parent is the gameplay-area `Control` per Spike #8 (verify via scene-tree inspector). |
| 15 | Standard mode (no modifiers) | Start a Standard run with NO modifiers → vote fires identically. Verifies no implicit gating on `_settings.GameMode == Custom`. |

### Spike → Gate dependency appendix <!-- CHANGED v3.1: Optional #1 — Reviewer R2-2 ADD1 -->

The operator-validation gates depend on different spike deliverables landing first. This table helps planning: some gates can be exercised before the spike completes, others are blocked on specific spike outputs.

| Gate | Blocked on spike deliverable(s) | Notes |
|---|---|---|
| 1 — Vote fires on Embark | #5 (cancellation probe), #7 (call-site audit) | Probe wiring required to detect non-fire-when-it-should-have-fired regressions; call-site audit confirms the patch surface is correct. |
| 2 — Winner applied | #1 (asset paths), #4 (idempotency), #6 (Act1 read-site), #7 (call-site audit) | Idempotency determines whether the re-invoke completes cleanly; Act1 read-site validation determines whether the override actually applies. |
| 3 — No-winner fallback | #6 (Act1 read-site) | Validates that NOT overriding Act1 leaves vanilla's random pick intact. |
| 4 — Settings toggle off | (none) | Pure-CLR bail path; unit-testable today. |
| 5 — Pool degeneracy | (none) | Defensive only; not exercisable in vanilla. |
| 6 — Cancellation | #5 (cancellation probe), #9 (`Cancel()` idempotency) | Probe must fire reliably; `Cancel()` safe under double-call. |
| 7 — Spam-Embark guard | (none) | Atomic-acquire is in-Prefix; validates today. |
| 8 — Pre-warm telemetry | #1 (asset paths), #3 (`GetTexture` sync) | If `GetTexture` is async, the envelope claim is meaningless. |
| 9 — Sealed Deck coexistence | #7 (call-site audit) | Validates the patch doesn't gate on `GameMode`. |
| 10 — Receipt delivery | (none) | Tests `formatReceipt` callback wiring; validatable as soon as bait-test connects to chat. |
| 11 — Save-quit preservation | #4 (idempotency), #6 (Act1 read-site) | Save snapshot timing relative to Act1 write must be safe. |
| 12 — Embark→ESC→Embark cycle | #5 (cancellation probe), #9 (`Cancel()` idempotency) | Same probe machinery as Gate 6. |
| 13 — Chat disconnect mid-vote | (none) | Tests bail-condition behavior under existing chat infrastructure. |
| 14 — Multi-resolution popup | #2 (banner anchor), #8 (gameplay-area surface) | Anchor convention must be locked before sub-checks make sense. |
| 15 — Standard-mode coverage | #7 (call-site audit) | Same dependency as Gate 9. |

**Gates with zero spike dependency** (validatable as soon as the patch class exists with default behavior): 4, 5, 7, 10, 13. These are the "early signal" gates — failing them means the patch infrastructure itself is broken before any spike-specific concern matters.

## Test architecture

### Test classes

- **`ActVariantVoteResolverTests`** (NO `[Collection]` — pure CLR):
  - `ResolveWinnerKey` mapping (5 cases).
  - `ShouldBail` over the **5 testable** `BailReason` values: `SettingsOff`, `Multiplayer`, `ChatUnreadable`, `Act1Pinned`, `PoolDegenerate`, `None`. **NOT** tested: `ResumeInProgress`, `VoteInProgress` (these have atomic-acquire semantics and are verified by Gates 7, 12 per M8). <!-- CHANGED v3: M8 — Reviewers R2-1, R2-3 -->
- **`ActVariantVoteCandidatesTests`** (NO `[Collection]`):
  - `BuildCandidates()` returns exactly 2 entries.
  - Keys lowercase, indices stable.
  - `FallbackColorHex` matches `^[0-9A-F]{6}$`.
  - Asset paths may be null.
  - Successive calls return independent list instances.
- **`ActVariantVoteFormatterTests`** (`[Collection("TiLog.Sink")]` — exercises `VoteSnapshot`): <!-- CHANGED v3: replaces ReceiptsTests; tests the formatReceipt callback behavior -->
  - For `ReceiptKind.Close` + `NoVotesReceived = true`: returns custom no-votes text AND side-channels the no-votes flag.
  - For `ReceiptKind.Close` + `NoVotesReceived = false`: delegates to `EnglishReceipts.FormatClose`.
  - For `ReceiptKind.Open` / `PeriodicTally`: delegates to `EnglishReceipts.{FormatOpen,FormatPeriodicTally}`.

## Open items / risks

1. **`Act1` write-then-reinvoke approach** validated by Spike #6.
2. **Asset paths not yet located** — L3 fallback is graceful.
3. **Save-quit serialization stability** — Gate 11.
4. **Banner anchor convention** — Spike #2.
5. **`Cache.GetTexture` synchronicity** — Spike #3.
6. **`BeginRunLocally` idempotency** — Spike #4. Determines whether fallback re-invoke ships or is removed.
7. **Cancellation probe** — Spike #5.
8. **Multi-resolution correctness** — Spike #8 + Gate 14.
9. **`BeginRunLocally` other call sites** — Spike #7.
10. **`VoteSession.Cancel()` idempotency** — Spike #9.

### Twitch rate-limit interaction (informational)

B.3.2 adds ~3–5 chat messages per run (open + 1–3 tallies + close, plus optional cancellation). Combined with existing per-run receipts across other slices, the per-run total approaches the Twitch 20-msgs/30s limit. Compositional pressure is small for this slice but adds to the global concern. Known v0.2 polish item.

## Cross-references

- [Round-2 meta-review](META-REVIEW-round2-2026-05-18-plan-b-3-2-act-variant-vote-design.md)
- [Round-1 meta-review](META-REVIEW-2026-05-18-plan-b-3-2-act-variant-vote-design.md)
- [v2 spec](2026-05-18-plan-b-3-2-act-variant-vote-design-v2.md) (predecessor)
- [memory: plan-b-3-2-design-checkpoint](../../../../.claude/projects/c--Users-Surfinite-slay-the-streamer-2/memory/plan_b_3_2_design_checkpoint.md)
- [`VoteSession.cs`](../../../src/Ti/Voting/VoteSession.cs) — `formatReceipt` parameter
- [`VoteCoordinator.cs`](../../../src/Ti/Voting/VoteCoordinator.cs)
- [`EnglishReceipts.cs`](../../../src/Ti/Voting/EnglishReceipts.cs)
- [`BossVotePatch.cs`](../../../src/Game/DecisionVotes/BossVotePatch.cs)
- [`BossVotePopup.cs`](../../../src/Game/Ui/BossVotePopup.cs)
- B.3.1 design: [2026-05-15-plan-b-3-1-combat-idle-boss-portraits-design.md](2026-05-15-plan-b-3-1-combat-idle-boss-portraits-design.md)

---

## Optional Enhancements — disposition

| # | Item | Status |
|---|------|--------|
| 1 | Spike → Gate dependency table appendix | ✅ **APPLIED** (v3.1) — added as a sub-section before Test architecture; 15-row table; identifies the 5 "early-signal" gates with zero spike dependency. |
| 2 | Multiplayer regression gate (Gate 16) | ❌ Not selected. MP is out of scope; real-world test cost > value for hobby slice. |
| 3 | Rename `ForceL3PopupFallback` | ❌ Not selected. Current naming consistent with internal vocabulary. |
| 4 | Read-only chat receipt-gate scoping | ✅ **APPLIED** (v3.1) — Gate 10 updated with explicit `ConnectedReadWrite`-only scoping note. |
| 5 | `_multiplayerWarnFired` reset on SP close | ❌ Not selected. Process-lifetime suppression is correct. |

**v3 (with selected enhancements) ready.** Moving to implementation-plan writing via the writing-plans skill.
