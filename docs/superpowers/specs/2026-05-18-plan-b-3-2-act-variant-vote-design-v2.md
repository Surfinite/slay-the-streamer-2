# 2026-05-18 — Plan B.3.2: Act-variant vote (design v2)

**Date**: 2026-05-18 (v2)
**Status**: Design v2 post-meta-review. Implementation plan pending plan + approval.
**Slice**: B.3.2 (post-B.3.1) — chat picks Act 1 variant (Underdocks vs Overgrowth) at run start, before vanilla's coin-flip in `GetRandomList` would otherwise pick.
**Scope**: New Harmony patch on `StartRunLobby.BeginRunLocally`. New popup `Control` rendering vertical 50/50 split of each variant's combat-room background + entry banner. Suspend-and-resume pattern (same shape as B.1 / B.2.1 / B.3). No changes to existing voting infrastructure.

**v2 changelog**: This version folds in 12 must-do and 11 should-do changes from the [meta-review](META-REVIEW-2026-05-18-plan-b-3-2-act-variant-vote-design.md). Changes are annotated inline with `<!-- CHANGED: reason — Reviewers X, Y -->` comments.

## TL;DR

Vanilla picks the Act 1 variant via a coin-flip inside [`ActModel.GetRandomList`](../../../decompiled/sts2/MegaCrit/sts2/Core/Models/ActModel.cs#L414), called once at the start of [`StartRunLobby.BeginRunLocally:411`](../../../decompiled/sts2/MegaCrit/sts2/Core/Multiplayer/Game/Lobby/StartRunLobby.cs#L411). The next line — `list[0] = GetAct(Act1) ?? list[0]` ([line 412](../../../decompiled/sts2/MegaCrit/sts2/Core/Multiplayer/Game/Lobby/StartRunLobby.cs#L412)) — is a vanilla override hook: if `StartRunLobby.Act1` is a known variant key (`"overgrowth"` / `"underdocks"`), it replaces the coin-flip pick. The hook exists because `NCharacterSelectScreen` has a (currently UI-hidden) `_actDropdown` that writes to this property; we use the same hook from chat instead.

**Mechanism.** Harmony prefix on `BeginRunLocally`, returns `false` to suspend. We start a 30s chat vote with two options (`#0 Overgrowth` / `#1 Underdocks`), `await session.AwaitWinnerAsync()` and inspect `VoteSnapshot.NoVotesReceived` to distinguish "real winner" from "no votes → vanilla stands" <!-- CHANGED: no-votes detection fix — Reviewers 1, 3, 5, 7, 8, 9 -->, then on the Godot main thread write `__instance.Act1 = winnerKey`, reflectively re-invoke `BeginRunLocally(seed, modifiers)`, and **restore `Act1` to its previous value in `finally`** <!-- CHANGED: one-shot Act1 — Reviewers 1, 2, 4, 9 -->. Vanilla's existing line 412 picks up the chat winner via the same code path the dropdown would have used. No reimplementation of `GetRandomList` / seed / RNG.

**Net change**: ~520 LOC across 4 new files + 1 edit (settings). One new Harmony patch class, one new popup `Control`, one pure helper (`ActVariantVoteResolver`) with **extracted bail logic** <!-- CHANGED: testability — Reviewers 1, 4, 5, 8, 9 -->, one DTO (`ActVariantOption`). No `src/Ti/*` edits — existing generic receipt formatters work as-is.

**Risk surface**: ~~smaller than B.3 (1 Harmony target, 1 popup, no idempotency-on-re-entry complexity since each Embark click is a fresh run-start).~~ Comparable to B.3 — the bail-condition surface and cancellation-probe requirements are richer than originally appreciated. <!-- CHANGED: more honest risk framing — meta-review --> Two flagged uncertainties for implementation: (1) `Act1` write-then-reinvoke pattern needs runtime validation in the spike, (2) combat-background and entry-banner asset paths not yet located in the decompile — research-spike during implementation with a text-only L3 fallback.

## Goals

- **Chat picks the Act 1 variant** when chat is connected and the streamer hasn't explicitly pinned a variant.
- **Reuse existing voting infrastructure** verbatim — `VoteCoordinator`, `VoteSession`, `VoteTallyLabel`. <!-- CHANGED: removed stale "Only new voting-layer addition is the receipt-string set" line which contradicted commit 0b2131e — Reviewers 1, 8 -->
- **Forward-compatible internals** — `ActVariantVotePopup` is parameterized on `IReadOnlyList<ActVariantOption>`; the candidate-pool builder is a private static method per-act. Adding an Act 2 variant vote in the future means a new Harmony patch + new candidate-pool method, not a popup rewrite.
- **Preserve TI/Game seam** — popup is MegaCrit-free at its public interface; all MegaCrit type contact lives in `ActVariantVotePatch`.
- **No regression** to existing voting features (B.1 / B.2.1 / B.2.2 / B.3 / B.3.1).

## Non-goals

- **Act 2 / Act 3 variant votes.** Vanilla has no Act 2 or Act 3 variants today. The architecture is forward-compatible (popup is generic) but no second-act patch ships in this slice. Future Act-2 variants would need ~1 day of additive work.
- **Generic act-transition trigger surface.** Speculative infra for a feature MegaCrit hasn't shipped. YAGNI.
- **Multiplayer.** Singleplayer-only per Surfinite 2026-05-17. (Defensive multiplayer bail is added per meta-review — see Trigger mechanics.)
- **Pre-act-N votes for variant pools that don't exist yet.** No Hive variants, no Glory variants.
- **Variant pool expansion** (option 1 from notes/10's earlier deferral). Different feature; deferred per memory 31663.
- **Settings UI surface** for the new toggle — `voteOnActVariant` is JSON-only for v1.
- **Localized receipts** — English-only, matches B.1/B.2.1/B.3 posture.
- **Unlock gating** of the candidate pool — design under `unlock all`. Both variants always shown.

## Architecture

### File layout

```
src/Game/DecisionVotes/
  ActVariantVotePatch.cs       — NEW. Harmony patch on StartRunLobby.BeginRunLocally.
                                  Suspend-and-resume, asset pre-warm. EXCLUDED from
                                  test compile.
  ActVariantVoteResolver.cs    — NEW. Pure static helpers (BuildCandidates,
                                  ResolveWinnerKey, ShouldBail, ActVariantAssetPaths).
                                  INCLUDED in test compile via auto-include of
                                  src/Game/DecisionVotes/**. <!-- CHANGED: bail logic
                                  extracted for testability + AssetPaths renamed
                                  to avoid collision with ActModel.AssetPaths —
                                  Reviewers 1, 4, 5, 8, 9 -->

src/Game/Ui/
  ActVariantVotePopup.cs       — NEW. Godot Control + CanvasLayer. EXCLUDED from
                                  test compile (Godot types).
  ActVariantOption.cs          — NEW. DTO with nullable asset paths + hex fallback
                                  color. INCLUDED in test compile via surgical
                                  <Compile Include="..\src\Game\Ui\ActVariantOption.cs" />.

src/Game/Bootstrap/
  ModSettings.cs               — EDIT. Add VoteOnActVariant : bool (default true).

(no changes to src/Ti/* — existing EnglishReceipts.FormatOpen / FormatPeriodicTally
                  / FormatClose are generic over VoteSnapshot; the slice label
                  "Act 1 variant vote" is passed at coordinator.Start time.
                  Cancellation and no-votes receipts are raw chat sends matching
                  B.3's BossVotePatch.SendIgnoredResultReceipt pattern.)
```

### TI/Game seam preserved

- [`src/Game/Ui/ActVariantOption.cs`](../../../src/Game/Ui/ActVariantOption.cs) is fully free of `MegaCrit.Sts2.*` references AND free of `Godot.*` types — it carries `(int Index, string Key, string Title, string? BackgroundPath, string? BannerPath, string FallbackColorHex)` only. <!-- CHANGED: nullable asset paths + hex color (test-csproj has no Godot reference) — Reviewers 1, 4, 5, 8, 9 + M5 -->
- [`src/Game/Ui/ActVariantVotePopup.cs`](../../../src/Game/Ui/ActVariantVotePopup.cs)'s **public interface** (constructor, public methods) is MegaCrit-free. Its **implementation** uses only `Godot.*` types.
- [`src/Game/DecisionVotes/ActVariantVotePatch.cs`](../../../src/Game/DecisionVotes/ActVariantVotePatch.cs) is the only file touching `MegaCrit.Sts2.*` types.
- [`src/Game/DecisionVotes/ActVariantVoteResolver.cs`](../../../src/Game/DecisionVotes/ActVariantVoteResolver.cs) is pure CLR. Testable in `Microsoft.NET.Sdk` with no DLL resolution.

### Vanilla API surface

- [`StartRunLobby.BeginRunLocally(string seed, List<ModifierModel> modifiers)`](../../../decompiled/sts2/MegaCrit/sts2/Core/Multiplayer/Game/Lobby/StartRunLobby.cs#L408) — `private void` instance method. Harmony target.
- [`StartRunLobby.Act1`](../../../decompiled/sts2/MegaCrit/sts2/Core/Multiplayer/Game/Lobby/StartRunLobby.cs#L87) — `public string` get/set property, default `"random"`. Our write target; restored after re-invoke. <!-- CHANGED: restoration noted — M3 -->
- [`StartRunLobby.GetAct(string)`](../../../decompiled/sts2/MegaCrit/sts2/Core/Multiplayer/Game/Lobby/StartRunLobby.cs#L441) — `private static`. Decodes `"overgrowth"` / `"underdocks"` / else → null.
- [`StartRunLobby.Players`](../../../decompiled/sts2/MegaCrit/sts2/Core/Multiplayer/Game/Lobby/StartRunLobby.cs) — `List<LobbyPlayer>`. Used for multiplayer bail. <!-- CHANGED: defensive MP bail — Reviewer 2 -->
- [`ActModel.GetRandomList(rng, unlockState, isMultiplayer)`](../../../decompiled/sts2/MegaCrit/sts2/Core/Models/ActModel.cs#L414) — vanilla's coin-flip variant picker. We don't call it; we override its output via the `Act1` write.
- `ModelDb.Act<Overgrowth>()` / `ModelDb.Act<Underdocks>()` — canonical `ActModel` accessors.
- `PreloadManager.Cache.GetTexture(string)` — discarded-return cache prime. **Synchronicity verification is now a spike deliverable** — see Asset discovery section. <!-- CHANGED: M12c — Reviewer 8 -->
- `VoteSnapshot.NoVotesReceived` — flag exposed on the snapshot returned at session close. Used to distinguish "real winner" from "synthesized random index due to zero votes." <!-- CHANGED: M1 — Reviewers 1, 3, 5, 7, 8, 9 -->

## Trigger mechanics — suspend-and-resume

### Bail order (Prefix)

<!-- CHANGED: bail order reordered to close chat-disconnect race — Reviewer 8 (M10) -->

```csharp
[HarmonyPatch(typeof(StartRunLobby), "BeginRunLocally",
              new[] { typeof(string), typeof(List<ModifierModel>) })]
internal static class ActVariantVotePatch {
    private static int _voteInProgress;
    private static int _resumeInProgress;
    private static int _multiplayerWarnFired;

    // Shared cancellation state per active vote. Recreated each vote;
    // popup writes Cancelled = 1 on ESC/screen-change; resume reads it.
    // Replaces the broken IsInstanceValid probe (StartRunLobby is not a
    // GodotObject, so IsInstanceValid would always return true).
    // <!-- CHANGED: explicit cancellation state — Reviewers 1, 2, 3, 4 (M7) -->
    private sealed class PendingActVariantVote {
        public int Cancelled;
    }
    private static PendingActVariantVote? _pending;

    private static readonly Lazy<MethodInfo?> _beginRunLocallyMethod =
        new(() => AccessTools.Method(typeof(StartRunLobby), "BeginRunLocally",
                                     new[] { typeof(string), typeof(List<ModifierModel>) }));

    static bool Prefix(StartRunLobby __instance, string seed, List<ModifierModel> modifiers) {
        // 1. Synthetic resume → let vanilla through with chat-set Act1.
        if (_resumeInProgress == 1) return true;

        // 2. Concurrent-vote suppression — moved up to close the chat-disconnect race.
        //    If a vote is in flight, suppress immediately regardless of any other
        //    state (chat could have disconnected mid-vote, which would otherwise
        //    cause bail at #4 to let vanilla through while our vote runs).
        // <!-- CHANGED: moved from position 6 — Reviewer 8 (M10) -->
        if (Interlocked.CompareExchange(ref _voteInProgress, 1, 0) != 0) {
            TiLog.Debug("[SlayTheStreamer2][act-variant-vote] repeat click during open vote; suppressed");
            return false;
        }

        // From here, _voteInProgress is acquired. Any subsequent bail MUST release it.
        try {
            // 3. Settings toggle off.
            if (!ModSettings.Current.VoteOnActVariant) {
                Interlocked.Exchange(ref _voteInProgress, 0);
                return true;
            }

            // 4. Multiplayer bail. <!-- CHANGED: defensive MP guard — Reviewer 2 (M8) -->
            int? playerCount = TryGetPlayerCount(__instance);
            if (playerCount is int n && n > 1) {
                if (Interlocked.CompareExchange(ref _multiplayerWarnFired, 1, 0) == 0) {
                    TiLog.Warn($"[SlayTheStreamer2][act-variant-vote] multiplayer detected (Players.Count={n}); bailing to vanilla");
                }
                Interlocked.Exchange(ref _voteInProgress, 0);
                return true;
            }

            // 5. Chat unreadable.
            var coordinator = Voter.Default;
            if (coordinator is null
                || coordinator.Chat.State is not (ChatConnectionState.ConnectedReadWrite
                                              or ChatConnectionState.ConnectedReadOnly)) {
                TiLog.Debug($"[SlayTheStreamer2][act-variant-vote] chat not readable; bailing to vanilla");
                Interlocked.Exchange(ref _voteInProgress, 0);
                return true;
            }

            // 6. Custom-mode pin (X policy). Respect explicit dropdown choice.
            if (!string.Equals(__instance.Act1, "random", StringComparison.Ordinal)) {
                TiLog.Info($"[SlayTheStreamer2][act-variant-vote] Act1 explicitly pinned ({__instance.Act1}); skipping vote");
                Interlocked.Exchange(ref _voteInProgress, 0);
                return true;
            }

            // 7. Pool degeneracy (defensive — pool is always 2 today).
            var candidates = ActVariantVoteResolver.BuildCandidates();
            if (candidates.Count <= 1) {
                TiLog.Info($"[SlayTheStreamer2][act-variant-vote] degenerate pool (count={candidates.Count}); bailing to vanilla");
                Interlocked.Exchange(ref _voteInProgress, 0);
                return true;
            }

            return PrefixContinue(__instance, seed, modifiers, candidates, coordinator);
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][act-variant-vote] Prefix threw; bailing to vanilla", ex);
            Interlocked.Exchange(ref _voteInProgress, 0);
            return true;
        }
    }

    private static int? TryGetPlayerCount(StartRunLobby instance) {
        try { return instance?.Players?.Count; }
        catch { return null; }
    }
}
```

`ActVariantVoteResolver.BuildCandidates()` and the bail logic helper `ActVariantVoteResolver.ShouldBail(...)` are extracted into the pure-CLR resolver for unit testability. <!-- CHANGED: M6 — Reviewers 1, 4, 5, 8, 9 -->

### Suspend-and-resume shape

```csharp
private static bool PrefixContinue(
        StartRunLobby instance,
        string seed,
        List<ModifierModel> modifiers,
        IReadOnlyList<ActVariantOption> candidates,
        VoteCoordinator coordinator) {

    // Copy modifiers at prefix time so the resumed run is deterministic against
    // any UI mutation during the 30s window.
    // <!-- CHANGED: defensive modifier copy — Reviewers 1, 4 (S8) -->
    var capturedModifiers = modifiers.ToList();

    VoteSession? session = null;
    try {
        PreWarmAssets(candidates);

        var labels = candidates.Select(c => c.Title).ToList();
        session = coordinator.Start("Act 1 variant vote", labels, TimeSpan.FromSeconds(30));

        _pending = new PendingActVariantVote();
        var pending = _pending;  // capture local for closure

        var popup = new ActVariantVotePopup(
            options: candidates,
            session: session,
            dispatcher: coordinator.Dispatcher,
            onUserAbandoned: () => Interlocked.Exchange(ref pending.Cancelled, 1));
        coordinator.Dispatcher.Post(() => popup.Show());

        _ = HandleVoteAsync(instance, seed, capturedModifiers, session, candidates, coordinator, pending);
    } catch (Exception ex) {
        // Orphan-session cleanup. <!-- CHANGED: prevent orphan vote/session on
        // exception path — Reviewer 1 (concern #4) -->
        TiLog.Error("[SlayTheStreamer2][act-variant-vote] PrefixContinue threw; cancelling any started session", ex);
        try { session?.Cancel(); } catch { /* swallow */ }
        _pending = null;
        Interlocked.Exchange(ref _voteInProgress, 0);
        return true;
    }
    return false;  // suspend vanilla
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
        bool noVotes = false;
        try {
            int idx = await session.AwaitWinnerAsync();
            // Read the post-close snapshot to distinguish "real winner" from
            // "synthesized random index due to no votes". VoteSession's no-votes
            // branch still produces a valid index (verified via
            // EnglishReceipts.FormatClose:30-31), so checking idx >= 0 alone is
            // not enough — we must check NoVotesReceived explicitly.
            // <!-- CHANGED: M1 no-votes detection — Reviewers 1, 3, 5, 7, 8, 9 -->
            var snapshot = session.Snapshot;  // assumed available post-close;
                                              // spike must confirm the exact API
            noVotes = snapshot.NoVotesReceived;
            if (!noVotes && idx >= 0 && idx < candidates.Count) {
                winnerIndex = idx;
            }
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][act-variant-vote] AwaitWinnerAsync threw; no override", ex);
        }

        string winnerKey = noVotes
            ? "random"
            : ActVariantVoteResolver.ResolveWinnerKey(candidates, winnerIndex);

        TiLog.Info($"[SlayTheStreamer2][act-variant-vote] resume: dispatching with winnerKey={winnerKey} (noVotes={noVotes}, cancelled={Volatile.Read(ref pending.Cancelled)})");

        // If no votes, send a custom receipt before the generic FormatClose can
        // lie about chat picking a random option. <!-- CHANGED: M1 receipt fix —
        // Reviewers 1, 3, 7, 8 -->
        if (noVotes) {
            SendNoVotesReceipt(coordinator);
        }

        coordinator.Dispatcher.Post(() => ResumeOnMainThread(instance, seed, capturedModifiers, winnerKey, pending));
    } catch (Exception ex) {
        TiLog.Error("[SlayTheStreamer2][act-variant-vote] HandleVoteAsync threw; fallback resume", ex);
        try {
            coordinator.Dispatcher.Post(() => ResumeOnMainThread(instance, seed, capturedModifiers, "random", pending));
        } catch (Exception postEx) {
            // Outer catch must reset _voteInProgress for symmetry with
            // PrefixContinue's catch. <!-- CHANGED: M11 leak fix — Reviewers 1, 3, 9 -->
            TiLog.Error("[SlayTheStreamer2][act-variant-vote] fallback Post threw; resetting flags", postEx);
            Interlocked.Exchange(ref _resumeInProgress, 0);
            Interlocked.Exchange(ref _voteInProgress, 0);
            _pending = null;
        }
    }
}

private static void ResumeOnMainThread(
        StartRunLobby instance,
        string seed,
        List<ModifierModel> capturedModifiers,
        string winnerKey,
        PendingActVariantVote pending) {
    Interlocked.Exchange(ref _resumeInProgress, 1);
    string? previousAct1 = null;
    try {
        // Check explicit cancellation flag — replaces broken IsInstanceValid.
        // <!-- CHANGED: M7 — Reviewers 1, 2, 3, 4 -->
        if (Volatile.Read(ref pending.Cancelled) == 1) {
            TiLog.Info("[SlayTheStreamer2][act-variant-vote] resume: vote was cancelled; aborting without re-invoke");
            SendCancellationReceipt();
            return;
        }

        // Capture previous Act1 and restore after invoke (one-shot override).
        // <!-- CHANGED: M3 — Reviewers 1, 2, 4, 9 -->
        previousAct1 = instance.Act1;

        if (winnerKey != "random") {
            instance.Act1 = winnerKey;
            TiLog.Info($"[SlayTheStreamer2][act-variant-vote] resume: Act1 = {winnerKey} (previous: {previousAct1})");
        } else {
            TiLog.Info("[SlayTheStreamer2][act-variant-vote] resume: no winner / cancelled vote; preserving vanilla pick");
        }

        var method = _beginRunLocallyMethod.Value;
        if (method is null) {
            TiLog.Error("[SlayTheStreamer2][act-variant-vote] resume: _beginRunLocallyMethod is null; cannot re-invoke");
            return;
        }

        try {
            method.Invoke(instance, new object?[] { seed, capturedModifiers });
        } catch (TargetInvocationException tie) {
            // Fallback re-invoke: reset Act1 to random, try once more so player
            // is not soft-locked at character-select. <!-- CHANGED: S10 — Reviewer 4 -->
            TiLog.Error($"[SlayTheStreamer2][act-variant-vote] re-invoke threw; attempting fallback with Act1=random", tie.InnerException ?? tie);
            try {
                instance.Act1 = "random";
                method.Invoke(instance, new object?[] { seed, capturedModifiers });
            } catch (Exception fallbackEx) {
                TiLog.Error("[SlayTheStreamer2][act-variant-vote] fallback re-invoke also threw; player may be stuck at character-select", fallbackEx);
            }
        }
    } catch (Exception ex) {
        TiLog.Error("[SlayTheStreamer2][act-variant-vote] resume threw", ex);
    } finally {
        // Restore Act1 if we wrote it (one-shot semantics).
        // <!-- CHANGED: M3 — Reviewers 1, 2, 4, 9 -->
        if (previousAct1 is not null && winnerKey != "random") {
            try { instance.Act1 = previousAct1; } catch { /* swallow */ }
        }
        Interlocked.Exchange(ref _resumeInProgress, 0);
        Interlocked.Exchange(ref _voteInProgress, 0);
        _pending = null;
    }
}

private static void SendCancellationReceipt() {
    // <!-- CHANGED: implementation shown — S3, Reviewers 4, 9 -->
    var coordinator = Voter.Default;
    if (coordinator?.Chat?.State != ChatConnectionState.ConnectedReadWrite) return;
    _ = coordinator.Chat.SendMessageAsync(
        "Act 1 variant vote cancelled — run-start abandoned.",
        OutgoingMessagePriority.Normal);
}

private static void SendNoVotesReceipt(VoteCoordinator coordinator) {
    // <!-- CHANGED: M1 — custom receipt prevents generic formatter from lying —
    // Reviewers 1, 3, 7, 8 -->
    if (coordinator.Chat.State != ChatConnectionState.ConnectedReadWrite) return;
    _ = coordinator.Chat.SendMessageAsync(
        "Act 1 variant vote closed: no votes received — vanilla random pick stands.",
        OutgoingMessagePriority.Normal);
}
```

### Cancellation

The popup observes the character-select screen lifecycle via a probe identified during the spike (see Spike deliverables M12c) — likely `NCharacterSelectScreen.Instance == null` or `instance.GetParent() == null`. When the probe fires, the popup writes to the shared `PendingActVariantVote.Cancelled` flag and calls `session.Cancel()`. `ResumeOnMainThread` checks the flag explicitly before any state mutation. <!-- CHANGED: M7 — replaces broken IsInstanceValid probe -->

No idempotency marker required: each Embark click is a fresh run-start.

## Candidate pool + bail logic

```csharp
// ActVariantVoteResolver.cs — pure CLR, in test compile via auto-include.

internal static class ActVariantVoteResolver {
    // <!-- CHANGED: renamed from AssetPaths to avoid collision with
    // ActModel.AssetPaths — Reviewers 7, 8 (M9) -->
    internal static class ActVariantAssetPaths {
        // Populated by the asset spike (M12). Null entries trigger L3 fallback.
        internal const string? OvergrowthCombatBackground = null;  // spike output
        internal const string? UnderdocksCombatBackground = null;  // spike output
        internal const string? OvergrowthEntryBanner = null;       // spike output
        internal const string? UnderdocksEntryBanner = null;       // spike output

        // Fallback colors sourced from vanilla's MapBgColor on each ActModel.
        // Hex strings to keep the DTO test-csproj-friendly (no Godot.Color dep).
        // <!-- CHANGED: M5 — Reviewer 1 -->
        internal const string OvergrowthFallbackHex = "A78A67";  // Overgrowth.MapBgColor
        internal const string UnderdocksFallbackHex = "9F95A5";  // Underdocks.MapBgColor
    }

    internal static IReadOnlyList<ActVariantOption> BuildCandidates() {
        return new[] {
            new ActVariantOption(
                Index: 0,
                Key: "overgrowth",
                Title: "Overgrowth",
                BackgroundPath: ActVariantAssetPaths.OvergrowthCombatBackground,
                BannerPath: ActVariantAssetPaths.OvergrowthEntryBanner,
                FallbackColorHex: ActVariantAssetPaths.OvergrowthFallbackHex),
            new ActVariantOption(
                Index: 1,
                Key: "underdocks",
                Title: "Underdocks",
                BackgroundPath: ActVariantAssetPaths.UnderdocksCombatBackground,
                BannerPath: ActVariantAssetPaths.UnderdocksEntryBanner,
                FallbackColorHex: ActVariantAssetPaths.UnderdocksFallbackHex),
        };
    }

    internal static string ResolveWinnerKey(IReadOnlyList<ActVariantOption> options, int? winnerIndex) {
        if (winnerIndex is null) return "random";
        if (winnerIndex < 0 || winnerIndex >= options.Count) return "random";
        return options[winnerIndex.Value].Key;
    }

    // <!-- CHANGED: M6 — extracted from Prefix for testability —
    // Reviewers 1, 4, 5, 8, 9 -->
    internal enum BailReason { None, ResumeInProgress, VoteInProgress, SettingsOff,
                                Multiplayer, ChatUnreadable, Act1Pinned, PoolDegenerate }

    internal static BailReason ShouldBail(
            bool resumeInProgress,
            bool voteAlreadyInProgress,
            bool settingsEnabled,
            int playerCount,
            ChatConnectionState chatState,
            string act1Value,
            int candidateCount) {
        if (resumeInProgress) return BailReason.ResumeInProgress;
        if (voteAlreadyInProgress) return BailReason.VoteInProgress;
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
    string? BackgroundPath,    // <!-- CHANGED: nullable — M4 -->
    string? BannerPath,        // <!-- CHANGED: nullable — M4 -->
    string FallbackColorHex);  // <!-- CHANGED: added — M5 -->
```

## Asset discovery — research spike

The popup needs four assets per variant pair (8 total: 2 backgrounds + 2 banners), paths NOT yet located in the decompile.

### Spike deliverables

<!-- CHANGED: M12 expanded — now 5 deliverables — meta-review consensus -->

1. **Asset paths**: 4 verified `res://...` paths (or `null` per asset if not located cleanly). Verified via `ResourceLoader.Exists(path) == true` AND `PreloadManager.Cache.GetTexture(path)` returns without warning.
2. **Banner anchor convention**: verified before locking the `column_width / 2, screen_height / 3` overlay position. <!-- CHANGED: S1 typo fix — `half_width / 2` was wrong; column-relative is correct — Reviewers 3, 4 -->
3. **`Cache.GetTexture` synchronicity**: explicit verification that it returns a fully-loaded texture (not a placeholder for async load). If async, `Gate 8`'s envelope claim is invalid and pre-warm needs redesign. <!-- CHANGED: M12c — Reviewer 8 -->
4. **`BeginRunLocally` idempotency**: confirm the method is safe to call twice on the same instance with the same arguments. Audit pre-line-411 code for instance-state mutations that would affect a second call. <!-- CHANGED: M12b — Reviewers 4, 5, 9 -->
5. **Cancellation probe**: identify the right run-start-abandonment signal to replace the broken `IsInstanceValid(StartRunLobby)` check. Candidates: `NCharacterSelectScreen.Instance == null`, `instance.GetParent() == null`, an explicit screen-visibility signal. Pick one that fires reliably on ESC/character-button-click. <!-- CHANGED: M12c — Reviewers 1, 2, 3, 4 -->

### Path-building approach

Direct construction via `SceneHelper.GetScenePath(...)` / `ImageHelper.GetImagePath(...)` — **NOT** via `ActModel.AssetPaths` enumeration. Per B.3.1's findings, the accessor has canonical-state landmines.

### L3 fallback (all-or-nothing)

<!-- CHANGED: S7 — all-or-nothing policy chosen over per-column to avoid mixed-quality
visual split — Reviewer 3 (vs Reviewer 8 per-column) -->

If **any** of the 4 assets is not locatable, the popup degrades to L3 across **both columns**:
- Per column: full-height `ColorRect` with the variant's `FallbackColorHex` color, plus a centered `Label` with the variant title in a large font.
- Per-column tally label preserved.
- Banner overlay skipped.
- Visually consistent across both columns. Avoids the "one pretty, one ugly" split that mixed-quality fallback would produce.

## Pre-warm — BOTH variants

```csharp
private static void PreWarmAssets(IReadOnlyList<ActVariantOption> candidates) {
    var sw = Stopwatch.StartNew();
    int succeeded = 0;
    int total = 0;
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
    string mode = total == 0 ? "L3 (no paths configured)"
                : succeeded < total ? $"L3 (only {succeeded}/{total} assets loaded)"
                : "L1";
    TiLog.Info($"[SlayTheStreamer2][act-variant-vote] pre-warm: {succeeded}/{total} assets in {sw.ElapsedMilliseconds}ms (mode={mode})");
}
```

Expected envelope: ≤ 100ms on baseline hardware. Gate 8 verifies this. <!-- CHANGED: mode logging clarifies L1/L3 status — partly addresses Reviewer 5 -->

## Popup UI structure

<!-- CHANGED: M2 — added Control inside each PanelContainer for free-positioning
overlay; PanelContainer is a Container and would otherwise stack children
sequentially — Reviewer 4 (codebase-verified) -->

```
CanvasLayer (layer = 100, owned by popup)
├── ColorRect (full screen, Color(0, 0, 0, 0.6), mouse_filter = Stop)   ← backdrop
└── HBoxContainer (full screen, separation = 0)
    ├── PanelContainer (50% width, 100% height, clip_contents = true)    ← column #0 wrapper
    │   └── Control (anchors full-rect, mouse_filter = Ignore)            ← free positioning
    │       ├── TextureRect (background, stretch_mode = KeepCenter,
    │       │                anchors full-rect, set Texture only if
    │       │                BackgroundPath != null else use FallbackColorHex
    │       │                via a sibling ColorRect)
    │       ├── TextureRect (banner, anchor top-center,
    │       │                position: column_width / 2, screen_height / 3,
    │       │                ONLY shown if BannerPath != null AND mode == L1)
    │       └── Label (tally, anchor bottom-center, MarginBottom = 80)
    └── PanelContainer (50% width, 100% height, clip_contents = true)    ← column #1 wrapper
        └── (same shape)
```

### Sizing

- `column_width / 2, screen_height / 3` (column-relative coordinates) is computed at `Show()` from the viewport size. <!-- CHANGED: S1 typo — was `half_width / 2`, which would center at quarter-width — Reviewers 3, 4 -->
- `clip_contents = true` + `stretch_mode = KeepCenter` is the native-size center-crop.
- Tally label font matches B.3's `VoteTallyLabel` styling for cross-vote consistency.

### Lifecycle

```csharp
// ActVariantVotePopup.cs — sketch of the _Process polling and lifecycle.
// <!-- CHANGED: S2 — _Process implementation shown — Reviewer 9 -->

public override void _Ready() {
    _session.TallyChanged += OnTally;
    _session.Closed += OnClosed;  // unsubscribes + QueueFrees
}

public override void _Process(double delta) {
    if (_userAbandoned) return;
    if (IsAbandonmentDetected()) {
        _userAbandoned = true;
        _onUserAbandoned();    // writes Cancelled flag in patch
        _session.Cancel();     // bounces through Closed → cleanup
    }
}

private bool IsAbandonmentDetected() {
    // Probe identified during spike — see M12c.
    // Placeholder: check NCharacterSelectScreen.Instance, or instance.GetParent().
    return /* spike-output probe */ false;
}

private void OnClosed(VoteSnapshot snapshot) {
    _session.TallyChanged -= OnTally;
    _session.Closed -= OnClosed;
    QueueFree();
}

public override void _UnhandledInput(InputEvent @event) {
    // Treat ESC as explicit abort (independent of the spike's screen-state probe).
    // Saves the streamer from a confused mid-vote ESC press.
    if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape }) {
        _userAbandoned = true;
        _onUserAbandoned();
        _session.Cancel();
        GetViewport().SetInputAsHandled();
    }
}
```

### Occlusion freeze (deferred)

B.3.1's `ProcessMode.Disabled` cascade is not applied to B.3.2's v1 — backgrounds and banners are static textures. Documented as future polish if asset spike turns up animated banners.

## Settings

```jsonc
{
  "voteOnActVariant": true   // default true; toggles the entire B.3.2 patch
}
```

`public bool VoteOnActVariant { get; init; } = true;` in `ModSettings.cs`. No schema-version bump (optional field with default).

## Receipts

**No `EnglishReceipts.cs` edits.** The existing `FormatOpen` / `FormatPeriodicTally` / `FormatClose` formatters are generic over `VoteSnapshot` — they derive all wording from `s.Label`, `s.Options`, `s.Tallies`, `s.WinnerIndex`, `s.NoVotesReceived`, `s.RandomTieAmong`, `s.TimeRemaining`. The slice label `"Act 1 variant vote"` is passed in at `coordinator.Start(...)` and flows through to every receipt automatically.

**Exception**: the no-votes path is intercepted by B.3.2 to prevent the generic formatter from falsely naming a chat-chosen variant. `SendNoVotesReceipt` is called from `HandleVoteAsync` before `ResumeOnMainThread` runs. <!-- CHANGED: M1 — Reviewers 1, 3, 5, 7, 8, 9 -->

What chat sees:
- **Open** (via `FormatOpen`): `"Vote [NN]: Act 1 variant vote! Type 0, 1 — 30s left."`
- **Periodic tally** (via `FormatPeriodicTally`): `"Vote: 0=5 1=3, 22s left."`
- **Close — winner** (via `FormatClose`): `"Chat chose 0: Overgrowth."`
- **Close — tie** (via `FormatClose`): `"Tie between 0 Overgrowth and 1 Underdocks — chat chose 0: Overgrowth randomly."`
- **Close — no votes** (B.3.2 custom send, suppresses generic formatter for this case): `"Act 1 variant vote closed: no votes received — vanilla random pick stands."`
- **Cancellation** (raw send): `"Act 1 variant vote cancelled — run-start abandoned."`

Receipt-policy cadence reuses `VoteReceiptPolicy.Default`. Periodic-tally dedup is on structural tally state.

## Forward-compatibility notes

For when MegaCrit ships Act 2 variants (not built today):

1. **New Harmony patch**: prefix some Act-2-transition point.
2. **New candidate-pool builder**: e.g., `BuildAct2Candidates()`.
3. **Mutate `runState.Acts[1]`**: harder than Act 1 — at run-start vanilla has already baked `Acts[1]` AND already called `GenerateRooms` on it. Need to re-run `GenerateRooms` on the new variant and preserve the shared-ancient subset.
4. **Popup reuse**: zero changes — `ActVariantVotePopup` takes `IReadOnlyList<ActVariantOption>`.

Estimated additional work: ~1 day per future variant pool.

## Test architecture

### Test csproj edits

`tests/slay_the_streamer_2.tests.csproj` auto-includes `..\src\Game\DecisionVotes\**\*.cs`. `ActVariantVoteResolver.cs` (including the nested `ActVariantAssetPaths` and `ShouldBail` helper) is picked up automatically.

`ActVariantOption.cs` in `src/Game/Ui/` needs surgical include:
```xml
<Compile Include="..\src\Game\Ui\ActVariantOption.cs" />
```

### Test classes

<!-- CHANGED: S9 — explicit [Collection] carve-out for pure CLR resolver tests —
Reviewer 7 -->

- **`ActVariantVoteResolverTests`** (NO `[Collection]` marker — pure CLR, no `TiLog`):
  - Winner-index → key mapping (5 cases).
  - `ShouldBail` (10+ cases covering all `BailReason` branches).
- **`ActVariantVoteCandidatesTests`** (NO `[Collection]` marker — pure CLR):
  - Returns exactly 2 entries.
  - `[0].Key == "overgrowth"`, `[1].Key == "underdocks"` (lowercase).
  - `[0].Index == 0`, `[1].Index == 1`.
  - `FallbackColorHex` non-empty and matches hex pattern.
  - `BackgroundPath` / `BannerPath` MAY be null (L3 fallback). <!-- CHANGED: removed "must be non-null" assertion — M4 -->
  - Successive calls return independent list instances (no static caching).
- **`ActVariantVoteReceiptsTests`** (`[Collection("TiLog.Sink")]` — exercises `VoteCoordinator` → `VoteSession` → `TiLog`):
  - Uses `VoteSessionTestBase.CreateCoordinator(...)`.
  - Open / tally / close-winner / close-tie receipts via generic formatters.
  - Custom no-votes receipt format.
  - Cancellation receipt format.

`ActVariantVotePatch.cs` and `ActVariantVotePopup.cs` are NOT in test compile.

## Operator-validation gates

<!-- CHANGED: Gates 12, 13 added; existing gates preserved — meta-review S4, S5 -->

13 gates total.

| # | Gate | How verified |
|---|------|--------------|
| 1 | Vote fires on Embark click | Chat connected, click Embark → popup appears, character-select frozen behind. `godot.log` shows `[act-variant-vote] opening vote`. |
| 2 | Winner applied | Vote `#0` or `#1` → run starts with chat-chosen variant. Verify via first combat room (enemy set matches the variant's `GenerateAllEncounters`). |
| 3 | No-winner fallback | Chat silent → vote times out at 30s → custom no-votes receipt sent → run starts with vanilla's seed-deterministic pick. `godot.log` shows `[act-variant-vote] resume: ... noVotes=True`. |
| 4 | Settings toggle off | `voteOnActVariant: false` → no vote fires, vanilla flow unchanged. |
| 5 | Pool degeneracy guard | Defensive — not exercisable in current vanilla. Smoke-only. |
| 6 | Cancellation | Embark, vote starts, ESC → popup tears down, chat receives cancellation receipt, NO run starts. |
| 7 | Spam-Embark guard | Click Embark twice in quick succession → second click suppressed. |
| 8 | Pre-warm telemetry | `godot.log` shows `[act-variant-vote] pre-warm: N/M assets in Tms (mode=L1|L3)`. Envelope ≤ 100ms. |
| 9 | Sealed Deck modifier coexistence | Run with Sealed Deck modifier active → vote still fires; run proceeds with chat-chosen variant + Sealed Deck flow. |
| 10 | Receipt delivery | Open + ≥1 periodic-tally + close receipts all arrive in chat. |
| 11 | Save-quit preservation | Start with chat-chosen variant. After entering first combat, save-quit + Continue. Verify variant preserved (combat-bg matches chat pick). |
| 12 | Embark→ESC→Embark cycle | Click Embark, ESC during vote, click Embark again. Verify second Embark fires a fresh vote (atomic state reset correctly). <!-- CHANGED: S4 — Reviewer 7 --> |
| 13 | Chat disconnect mid-vote | Click Embark, disconnect Twitch IRC during vote → vote times out or returns no-winner → vanilla pick stands. `godot.log` shows degraded state but no crash. <!-- CHANGED: S5 — Reviewer 9 --> |

**Operator-validation gate for bail condition X (`Act1 != "random"`)** is un-testable in the current build (dropdown is UI-hidden). The bail path itself is now unit-testable via `ActVariantVoteResolver.ShouldBail` (M6). In-game validation deferred until MegaCrit surfaces the dropdown.

## Open items / risks

1. **`Act1` write-then-reinvoke approach validated end-to-end by spike (M12d).** Post-line-412 read-site audit + runtime postfix logging during a vanilla run confirms no other reads.
2. **Asset paths not yet located in decompile.** Research-spike during implementation. L3 fallback is graceful.
3. **Save-quit serialization stability** smoke-tested via Gate 11.
4. **Banner anchor convention** verified during spike (M12).
5. **`Cache.GetTexture` synchronicity** verified during spike (M12c).
6. **`BeginRunLocally` idempotency** verified during spike (M12b).
7. **Cancellation probe** identified during spike (M12c).

## Cross-references

- [memory: plan-b-3-2-design-checkpoint](../../../../.claude/projects/c--Users-Surfinite-slay-the-streamer-2/memory/plan_b_3_2_design_checkpoint.md)
- [memory: sts2-act-dropdown-hidden](../../../../.claude/projects/c--Users-Surfinite-slay-the-streamer-2/memory/sts2_act_dropdown_hidden.md)
- [meta-review v1 → v2](META-REVIEW-2026-05-18-plan-b-3-2-act-variant-vote-design.md)
- [`BossVotePatch.cs`](../../../src/Game/DecisionVotes/BossVotePatch.cs) — suspend-and-resume reference (B.3).
- [`BossVotePopup.cs`](../../../src/Game/Ui/BossVotePopup.cs) — popup reference (B.3 / B.3.1).
- B.3.1 design: [2026-05-15-plan-b-3-1-combat-idle-boss-portraits-design.md](2026-05-15-plan-b-3-1-combat-idle-boss-portraits-design.md).
- [`StartRunLobby.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/Multiplayer/Game/Lobby/StartRunLobby.cs)
- [`ActModel.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/Models/ActModel.cs)
- [`NCharacterSelectScreen.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/CharacterSelect/NCharacterSelectScreen.cs)

---

## Optional Enhancements (pick what you want)

These are the **Consider-tier** items from the meta-review — good ideas worth thinking about but not critical to ship. Tell me which numbers to incorporate.

1. **Receipt label `"Act 1 variant"` (drop "vote")** — Reviewer 8. Reads better when generic formatter interpolates: `"Vote [NN]: Act 1 variant! Type 0, 1 — 30s left."` vs the current `"Vote [NN]: Act 1 variant vote! ..."`. Effort: **trivial** (one-line change). My recommendation: **lean yes** — cleaner phrasing, no downside.

2. **Debug `ModSetting` to force L3 fallback** — Reviewer 6. Adds `forceL3Fallback: bool` to settings (default false) so the popup can be tested without locating actual textures. Useful during the asset spike. Effort: **trivial** (one field + one branch in pre-warm). My recommendation: **lean yes** — cheap dev-ergonomics win, and useful for ongoing L3 regression testing.

3. **Pre-warm timeout/degradation path** — Reviewer 9. Hard 200ms wall-clock cap; if exceeded, stop loading remaining assets and proceed with whatever loaded (degrades to partial L3). Effort: **small** (~10 lines, refactor pre-warm loop with `break` on time check). My recommendation: **neutral** — defensive but B.3.1 baseline was 76–82ms; YAGNI today, easy to add if Gate 8 reports stalls.

4. **Rename `VoteOnActVariant` to `VoteOnAct1Variant`** — Reviewer 8. Explicit about scope. Future Act-2 work introduces `VoteOnAct2Variant`. Effort: **trivial**. My recommendation: **lean no** — single toggle is simpler today and the rename forces a settings-schema decision when Act 2 ships; let future-us deal with future granularity.

5. **Twitch rate-limit interaction note in "Open items / risks"** — Reviewer 8. ~5–6 messages per B.3.2 vote, compounding existing per-run receipt budget. Effort: **trivial** (one paragraph). My recommendation: **lean yes** — keeps the known debt visible.

6. **Postfix on `BeginRunLocally` for runtime `Act1`-read validation** — Reviewer 5. During the spike, attach a postfix that logs `Act1` value after line 412 during a normal vanilla run, to confirm no other read sites. Effort: **small** (~15 lines, removed after spike). My recommendation: **lean yes** — closes the highest-risk open item with empirical evidence.

7. **Subscribe to `session.TallyChanged` in popup constructor not `_Ready`** — Reviewer 8 (L3). Eliminates the (tiny) race window between construction and `_Ready`. Effort: **trivial**. My recommendation: **neutral** — race is essentially zero in practice given dispatcher ordering.

8. **Log unwrap `TargetInvocationException` from `Method.Invoke`** — Reviewer 7, 8. The v2 spec already does this in the fallback re-invoke path (S10), but not in the outer catch. Make it consistent. Effort: **trivial**. My recommendation: **lean yes** — better diagnostics.

9. **Aspect-ratio awareness for ultrawide monitors** — Reviewer 6, 8 (L1). Recompute popup positions based on actual viewport ratio. Effort: **small** (~20 lines + tuning). My recommendation: **lean no** — StS2 doesn't ship custom resolutions; revisit if reports come in.

10. **Verify `BeginRunLocally` has no other call sites** — Reviewer 8 ADD1. One-line grep audit during spike, documented in spec. Effort: **trivial**. My recommendation: **lean yes** — cheap closure on the "what fires this patch" question.

11. **`string.Equals(Act1, "random", StringComparison.Ordinal)`** — Reviewer 1. Already applied in v2's Prefix code (bail #6). **Already-applied; no action needed.**

12. **Explicit `readonly record struct` declaration for `ActVariantOption`** — Reviewer 9. Already applied in v2. **Already-applied; no action needed.**

13. **`Voter.Default` documented in CONTEXT doc** — Reviewer 3. Out-of-scope for the spec (it's a CONTEXT-doc update). Effort: **trivial**. My recommendation: **lean yes** — apply to CONTEXT doc when next regenerated.

---

**v2 ready for review.** Reply with which Optional Enhancement numbers (if any) to apply, or confirm the v2 plan is good as-is and we'll move to implementation-plan writing.
