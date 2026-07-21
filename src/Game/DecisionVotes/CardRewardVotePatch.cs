using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Rewards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using SlayTheStreamer2.Game.Bootstrap;
using SlayTheStreamer2.Game.Ui;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Internal;
using SlayTheStreamer2.Ti.Ui;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2.Game.DecisionVotes;

// SelectCard is private — must use string literal (nameof won't bind across accessibility).
[HarmonyPatch(typeof(NCardRewardSelectionScreen), "SelectCard")]
internal static class CardRewardVotePatch {
    private static int _voteInProgress;
    private static int _resumeInProgress;
    private static int _chatSkipResumeInProgress;
    private static int _multiplayerWarnFired;
    private static VoteSession? _activeSession;
    private static bool _activeIncludeSkip;
    private static int _overrideSkipPending;

    /// <summary>
    /// True iff the patch passed Prepare's hard checks at registration time.
    /// Read by CardRewardSkipGatePatch.ShouldEnforceSkipGate() — gate degrades
    /// to vanilla if the vote patch couldn't register.
    /// </summary>
    internal static bool PreparedSuccessfully { get; private set; }

    /// <summary>
    /// True iff Prepare's soft check confirmed RunManager.Instance.DebugOnlyGetState()
    /// is reachable. False means we register the vote patch but skip the run-id guard
    /// (vote still works; resume just doesn't get the abandon-mid-vote safety net).
    /// </summary>
    internal static bool RunIdGuardEnabled { get; private set; } = true;

    /// <summary>
    /// True while a card vote is in flight. Exposed so the skip-gate can cross-check
    /// (e.g., refuse to act on a Proceed click that might race a pending resume).
    /// </summary>
    internal static bool VoteInProgress => _voteInProgress == 1;

    private static readonly Lazy<FieldInfo?> _optionsField =
        new(() => AccessTools.Field(typeof(NCardRewardSelectionScreen), "_options"));
    private static readonly Lazy<FieldInfo?> _cardRowField =
        new(() => AccessTools.Field(typeof(NCardRewardSelectionScreen), "_cardRow"));
    private static readonly Lazy<MethodInfo?> _selectCardMethod =
        new(() => AccessTools.Method(typeof(NCardRewardSelectionScreen), "SelectCard", new[] { typeof(NCardHolder) }));
    // v0.106.1 changed OnAlternateRewardSelected from taking PostAlternateCardRewardAction to
    // taking int (the index of the chosen alternative button in _extraOptions). See
    // [decompiled/sts2/MegaCrit.Sts2.Core.Nodes.Screens.CardSelection/NCardRewardSelectionScreen.cs:260].
    private static readonly Lazy<MethodInfo?> _onAlternateRewardSelectedMethod =
        new(() => AccessTools.Method(typeof(NCardRewardSelectionScreen), "OnAlternateRewardSelected", new[] { typeof(int) }));
    private static readonly Lazy<FieldInfo?> _extraOptionsField =
        new(() => AccessTools.Field(typeof(NCardRewardSelectionScreen), "_extraOptions"));

    /// <summary>
    /// Captures option identity (not holder identity) for reroll detection at resume.
    /// Reroll is the failure mode the spec wanted to catch — and reroll uniquely
    /// rebuilds `_options` (NCardRewardSelectionScreen.RefreshOptions). Holder
    /// instances, by contrast, can be re-created mid-vote by hover animations,
    /// focus-loss redraw cycles, or other vanilla UI lifecycle events that do NOT
    /// indicate reroll. Comparing CardCreationResult references gives us exactly
    /// the signal we want with no false positives.
    /// </summary>
    private readonly record struct OptionsSignature(int Count, CardCreationResult[] Snapshot) {
        public bool Matches(IReadOnlyList<CardCreationResult> current) {
            if (current.Count != Count) return false;
            for (int i = 0; i < Count; i++) {
                if (!ReferenceEquals(current[i], Snapshot[i])) return false;
            }
            return true;
        }
    }

    private static OptionsSignature CaptureSignature(IReadOnlyList<CardCreationResult> options) {
        return new OptionsSignature(options.Count, options.ToArray());
    }

    private static IReadOnlyList<NCardHolder>? GetCurrentHolders(NCardRewardSelectionScreen screen) {
        var host = _cardRowField.Value?.GetValue(screen) as Node;
        if (host is null) return null;
        // Sort by Position.X so list-order matches visual left-to-right order, which is
        // also the order options[] is in. Vanilla NGridCardHolder.OnFocus calls
        // MoveToFrontSafely(), reordering Godot's child list whenever a card gets focus
        // — without this sort, GetChildren() returns scrambled order and the
        // option-index → holder mapping breaks (chat votes would resolve to the wrong
        // card). Tweens to the final 350*i position complete in 0.5s on screen show,
        // well before any user click could trigger this code path.
        return host.GetChildren().OfType<NCardHolder>().OrderBy(h => h.Position.X).ToList();
    }

    private static int? FindHolderIndex(IReadOnlyList<NCardHolder> holders, NCardHolder target) {
        for (int i = 0; i < holders.Count; i++) {
            if (holders[i] == target || holders[i].GetInstanceId() == target.GetInstanceId()) return i;
        }
        return null;
    }

    private static IReadOnlyList<CardCreationResult>? GetCurrentOptions(NCardRewardSelectionScreen screen) {
        return _optionsField.Value?.GetValue(screen) as IReadOnlyList<CardCreationResult>;
    }

    /// <summary>
    /// Find the index of the "Skip" alternative in the screen's <c>_extraOptions</c>.
    /// Always 0 in the default vanilla layout (Skip is added first when CanSkip), but
    /// resolved dynamically because Hook.ModifyCardRewardAlternatives can mutate the
    /// list. Returns null if no Skip alt is present (e.g., CanSkip=false rewards).
    /// </summary>
    private static int? FindSkipAlternativeIndex(NCardRewardSelectionScreen screen) {
        var raw = _extraOptionsField.Value?.GetValue(screen);
        if (raw is not IReadOnlyList<CardRewardAlternative> extras) return null;
        for (int i = 0; i < extras.Count; i++) {
            if (extras[i]?.OptionId == "Skip") return i;
        }
        return null;
    }

    /// <summary>
    /// Streamer override: an armed click on a card during an open vote ends the
    /// vote immediately with that card as the winner and consumes one override
    /// (spec 2026-07-21 §2.3). Returns false — with the click staying suppressed —
    /// whenever any precondition fails; the caller then logs the normal
    /// suppressed-click line. Budget is consumed strictly AFTER TryCloseNow
    /// succeeds (never consume on a lost close-timer race).
    /// </summary>
    private static bool TryOverrideWithCard(NCardRewardSelectionScreen screen, NCardHolder clicked) {
        try {
            var session = _activeSession;
            if (session is null || session.State != VoteSessionState.Open) return false;
            if (!VoteOverrideBudget.Enabled || VoteOverrideBudget.Remaining <= 0) return false;
            if (session.Elapsed < VoteOverrideBudget.ArmingDelay) return false;

            var holders = GetCurrentHolders(screen);
            var options = GetCurrentOptions(screen);
            if (holders is null || options is null) return false;
            int? cardIndex = FindHolderIndex(holders, clicked);
            if (cardIndex is null || cardIndex.Value >= options.Count) return false;

            int voteIndex = _activeIncludeSkip ? cardIndex.Value + 1 : cardIndex.Value;
            string takenLabel = options[cardIndex.Value].Card.Title;

            if (!session.TryCloseNow(voteIndex)) return false;

            VoteOverrideBudget.RecordUse();
            VoteOverrideBudget.SendOverrideReceipt(takenLabel);
            TiLog.Info($"[SlayTheStreamer2][card-vote] override: streamer forced #{voteIndex} ({takenLabel}); {VoteOverrideBudget.Remaining} override(s) remaining this act");
            return true;
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][card-vote] override attempt failed; click suppressed", ex);
            return false;
        }
    }

    /// <summary>
    /// Streamer override via the Skip button during an open vote. Consumes an
    /// OVERRIDE, never a card skip (spec Decision 5). Two sub-cases:
    /// includeSkip=true — Skip is vote option #0, force it through the normal
    /// TryCloseNow path (ResumeSkipOnMainThread then applies chat-skip
    /// semantics, budget-free via _chatSkipResumeInProgress). includeSkip=false
    /// — Skip is not a vote option, so flag _overrideSkipPending and Cancel();
    /// HandleVoteAsync routes the cancellation to ResumeSkipOnMainThread.
    /// </summary>
    private static bool TryOverrideWithSkip(NCardRewardSelectionScreen screen, int clickedIndex) {
        try {
            var session = _activeSession;
            if (session is null || session.State != VoteSessionState.Open) return false;
            if (!VoteOverrideBudget.Enabled || VoteOverrideBudget.Remaining <= 0) return false;
            if (session.Elapsed < VoteOverrideBudget.ArmingDelay) return false;

            var skipIndex = FindSkipAlternativeIndex(screen);
            if (skipIndex is null || clickedIndex != skipIndex.Value) return false;

            if (_activeIncludeSkip) {
                if (!session.TryCloseNow(0)) return false;   // Skip is vote option #0
            } else {
                Interlocked.Exchange(ref _overrideSkipPending, 1);
                session.Cancel();
                if (session.State != VoteSessionState.Cancelled) {
                    // Lost a race to natural close — revert; do not consume.
                    Interlocked.Exchange(ref _overrideSkipPending, 0);
                    return false;
                }
            }

            VoteOverrideBudget.RecordUse();
            VoteOverrideBudget.SendOverrideReceipt("Skip");
            TiLog.Info($"[SlayTheStreamer2][card-vote] override: streamer forced Skip; {VoteOverrideBudget.Remaining} override(s) remaining this act");
            return true;
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][card-vote] skip-override attempt failed; click suppressed", ex);
            Interlocked.Exchange(ref _overrideSkipPending, 0);
            return false;
        }
    }

    private static int? TryGetPlayerCount() {
        try {
            return RunManager.Instance?.DebugOnlyGetState()?.Players?.Count;
        } catch {
            return null;
        }
    }

    static bool Prepare(MethodBase? original) {
        if (original is null) {
            // Registration-time. Hard checks: vote target shape — failure aborts patch.
            if (_optionsField.Value is null) {
                TiLog.Error("[SlayTheStreamer2][card-vote] hard check failed: NCardRewardSelectionScreen._options field not found; patch will not register");
                PreparedSuccessfully = false;
                return false;
            }
            if (_cardRowField.Value is null) {
                TiLog.Error("[SlayTheStreamer2][card-vote] hard check failed: NCardRewardSelectionScreen._cardRow field not found; patch will not register");
                PreparedSuccessfully = false;
                return false;
            }
            if (_selectCardMethod.Value is null) {
                TiLog.Error("[SlayTheStreamer2][card-vote] hard check failed: NCardRewardSelectionScreen.SelectCard(NCardHolder) method not found via reflection; patch will not register");
                PreparedSuccessfully = false;
                return false;
            }
            if (_onAlternateRewardSelectedMethod.Value is null) {
                TiLog.Warn("[SlayTheStreamer2][card-vote] soft check: OnAlternateRewardSelected(int) not found; chat Skip votes will fall back to no-op (sub-screen will not auto-dismiss)");
            }
            if (_extraOptionsField.Value is null) {
                TiLog.Warn("[SlayTheStreamer2][card-vote] soft check: NCardRewardSelectionScreen._extraOptions field not found; chat Skip votes will fall back to no-op");
            }

            // Soft check: run-id accessor. Failure logs Warn but does NOT abort registration.
            try {
                var rm = RunManager.Instance;
                if (rm == null) {
                    TiLog.Warn("[SlayTheStreamer2][card-vote] run-ID guard degraded:RunManager.Instance not reachable");
                    RunIdGuardEnabled = false;
                } else {
                    var stateMethod = rm.GetType().GetMethod("DebugOnlyGetState");
                    if (stateMethod is null) {
                        TiLog.Warn("[SlayTheStreamer2][card-vote] run-ID guard degraded:DebugOnlyGetState() not found");
                        RunIdGuardEnabled = false;
                    }
                    // Don't deeply walk Rng.StringSeed here — state may be null at Prepare
                    // time (no run in progress). Defer to runtime; if accessor throws,
                    // log Warn and treat that vote as guard-disabled.
                }
            } catch (Exception ex) {
                TiLog.Warn($"[SlayTheStreamer2][card-vote] run-ID guard degraded:Prepare soft check threw: {ex.Message}");
                RunIdGuardEnabled = false;
            }

            PreparedSuccessfully = true;
            return true;
        }

        // Per-method signature check.
        var parameters = original.GetParameters();
        if (parameters.Length != 1 || parameters[0].ParameterType != typeof(NCardHolder)) {
            TiLog.Error($"[SlayTheStreamer2][card-vote] target signature mismatch: {original.DeclaringType?.FullName}.{original.Name}({string.Join(", ", parameters.Select(p => p.ParameterType.Name))})");
            PreparedSuccessfully = false;
            return false;
        }
        TiLog.Info($"[SlayTheStreamer2][card-vote] target resolved: {original.DeclaringType?.FullName}.{original.Name}");
        return true;
    }

    static bool Prefix(NCardRewardSelectionScreen __instance, NCardHolder cardHolder) {
        if (_resumeInProgress == 1) return true;

        // Hard guards
        if (!GodotObject.IsInstanceValid(__instance) || !GodotObject.IsInstanceValid(cardHolder)) return true;

        // Vote in progress: nothing may fall through to vanilla. Must run
        // BEFORE the multiplayer/chat bail-to-vanilla gates — a mid-vote click
        // during a chat disconnect would otherwise reach vanilla SelectCard
        // and claim a card under a pending vote.
        if (_voteInProgress == 1) {
            if (TryOverrideWithCard(__instance, cardHolder)) return false;
            TiLog.Debug("[SlayTheStreamer2][card-vote] repeat click during open vote; suppressed");
            return false;
        }

        // Multiplayer bail
        int? playerCount = TryGetPlayerCount();
        if (playerCount is int n && n > 1) {
            if (Interlocked.CompareExchange(ref _multiplayerWarnFired, 1, 0) == 0) {
                TiLog.Warn("[SlayTheStreamer2][card-vote] multiplayer detected (Players.Count > 1); bailing to vanilla");
            } else {
                TiLog.Debug("[SlayTheStreamer2][card-vote] multiplayer bail-out");
            }
            return true;
        }

        // Chat-readiness gate
        var coordinator = Voter.Default;
        if (coordinator is null) return true;
        // Vote-start gate: any chat state where we can READ messages is sufficient.
        // ConnectedReadOnly covers v0.2 YT-only-mode (Twitch terminal, YT alive): YT
        // messages still flow into VoteSession; receipts won't fire but the vote runs.
        // Receipt-send sites independently check ConnectedReadWrite per D3.
        if (coordinator.Chat.State is not (ChatConnectionState.ConnectedReadWrite
                                        or ChatConnectionState.ConnectedReadOnly)) {
            TiLog.Debug($"[SlayTheStreamer2][card-vote] chat not readable (state={coordinator.Chat.State}); bailing to vanilla");
            return true;
        }

        // Atomic vote-in-progress flip
        if (Interlocked.CompareExchange(ref _voteInProgress, 1, 0) != 0) {
            if (TryOverrideWithCard(__instance, cardHolder)) return false;
            TiLog.Debug("[SlayTheStreamer2][card-vote] repeat click during open vote; suppressed");
            return false;
        }

        // Snapshot options + holders
        var options = GetCurrentOptions(__instance);
        var holders = GetCurrentHolders(__instance);
        if (options is null || options.Count == 0 || holders is null || holders.Count == 0) {
            Interlocked.Exchange(ref _voteInProgress, 0);
            return true;
        }
        // Single-option vote is degenerate (chat "votes" on the only option). Skip
        // the vote ceremony and let vanilla apply the streamer's click directly --
        // streamer-side it's still a click, chat-side they would have just been told
        // "Vote: #0 SomeCard" with nothing to choose against. Surfinite's request
        // 2026-05-12 after Discord feedback about edge cases (e.g., relics that
        // collapse rewards to a single option).
        if (options.Count <= 1) {
            TiLog.Info($"[SlayTheStreamer2][card-vote] single-option reward; skipping vote (option: {options[0].Card.Title})");
            Interlocked.Exchange(ref _voteInProgress, 0);
            return true;
        }
        var optionsSnapshot = options.ToList();
        var holdersSnapshot = holders.ToList();

        var settings = ModSettings.Current;
        var voteDuration = TimeSpan.FromSeconds(settings?.VoteDurationSeconds ?? 30);
        var showTag = settings?.ShowVoteTag ?? false;
        var includeSkip = settings?.CardSkipAsVoteOption ?? false;

        var cardTitles = optionsSnapshot.Select(o => o.Card.Title).ToList();   // SPIKE-CORRECTED: was Card.Name.GetText()
        var labels = CardRewardOptionLabels.Build(cardTitles, includeSkip);

        int playerClickIndex = FindHolderIndex(holdersSnapshot, cardHolder) ?? 0;
        var optionsSig = CaptureSignature(optionsSnapshot);

        // Capture run-id (soft guard — uses Rng.StringSeed per spike, not .Id)
        string? runIdAtStart = null;
        if (RunIdGuardEnabled) {
            try {
                runIdAtStart = RunManager.Instance?.DebugOnlyGetState()?.Rng?.StringSeed;
                if (runIdAtStart is null) {
                    TiLog.Warn("[SlayTheStreamer2][card-vote] run-ID guard degraded for this vote — null state or null seed at start");
                }
            } catch (Exception ex) {
                TiLog.Warn($"[SlayTheStreamer2][card-vote] run-ID guard degraded for this vote — {ex.Message}");
            }
        }

        // Vote-override budget: best-effort observe so the counter is fresh even
        // if this vote fires before any rewards-screen _Ready this act.
        try {
            var rsForObserve = RunManager.Instance?.DebugOnlyGetState();
            int? actIdx = rsForObserve?.CurrentActIndex;
            var overrideReason = VoteOverrideBudget.Observe(rsForObserve?.Rng?.StringSeed, actIdx);
            VoteOverrideBudget.SendResetReceiptIfAny(overrideReason, actIdx.HasValue ? actIdx.Value + 1 : 0);
        } catch { /* observe is best-effort */ }

        // Voter.Start with try/catch fallback to vanilla
        VoteSession session;
        try {
            session = coordinator.Start("Card Reward", labels, voteDuration, showTag);
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][card-vote] Voter.Default.Start threw; falling back to vanilla", ex);
            Interlocked.Exchange(ref _voteInProgress, 0);
            return true;
        }

        // Vote-override context: consulted by the suppressed-click branches.
        // _overrideSkipPending cleared defensively — a stale flag from a vote
        // whose Cancel() lost a race must not leak into this vote.
        _activeSession = session;
        _activeIncludeSkip = includeSkip;
        Interlocked.Exchange(ref _overrideSkipPending, 0);

        TiLog.Info($"[SlayTheStreamer2][card-vote] opening vote for {optionsSnapshot.Count} options; player clicked #{playerClickIndex}; includeSkip={includeSkip}");
        _ = HandleVoteAsync(coordinator, __instance, session, optionsSnapshot, optionsSig, playerClickIndex, runIdAtStart, includeSkip);
        return false;
    }

    private static async Task HandleVoteAsync(
        VoteCoordinator coordinator,
        NCardRewardSelectionScreen screen,
        VoteSession session,
        IReadOnlyList<CardCreationResult> optionsSnapshot,
        OptionsSignature optionsSig,
        int playerClickIndex,
        string? runIdAtStart,
        bool includeSkip) {
        try {
            coordinator.Dispatcher.Post(() => {
                VoteTallyLabel.AttachTo(session, RunLiveness.IsRunDying, ModSettings.Current?.VoteTallyOnLeft ?? false, OverlayOcclusion.IsOccludingOverlayVisible);
                try {
                    new CardRewardVotePopup(session, coordinator.Dispatcher, screen, includeSkip, RunLiveness.IsRunDying, OverlayOcclusion.IsOccludingOverlayVisible).Show();
                } catch (Exception ex) {
                    TiLog.Warn($"[SlayTheStreamer2][card-vote] popup attach failed; corner tally still active: {ex.Message}");
                }
            });

            int winnerIndex;
            try {
                winnerIndex = await session.AwaitWinnerAsync();
            } catch (OperationCanceledException) {
                if (Interlocked.Exchange(ref _overrideSkipPending, 0) == 1) {
                    // Streamer override-skip on a vote with no Skip option: the
                    // session was cancelled as the transport; apply chat-skip
                    // semantics (reward consumed, no card-skip budget charge).
                    TiLog.Info("[SlayTheStreamer2][card-vote] override-skip: routing cancelled session to skip-resume");
                    coordinator.Dispatcher.Post(() => ResumeSkipOnMainThread(screen, runIdAtStart, optionsSig));
                    return;
                }
                // Non-override cancellation (run death etc.): preserve prior
                // behavior — fallback resume; liveness checks drop it if the
                // world moved on.
                TiLog.Info("[SlayTheStreamer2][card-vote] vote cancelled; falling back to player click");
                winnerIndex = includeSkip ? playerClickIndex + 1 : playerClickIndex;
            } catch (Exception ex) {
                TiLog.Error("[SlayTheStreamer2][card-vote] AwaitWinnerAsync threw; falling back to player click", ex);
                winnerIndex = includeSkip ? playerClickIndex + 1 : playerClickIndex;
            }

            // Remap chat-voted index to card index, accounting for Skip-as-#0 shift.
            var cardIndex = CardRewardOptionLabels.ResolveCardIndex(winnerIndex, includeSkip);

            if (cardIndex is null) {
                // Chat voted Skip (#0 with includeSkip = true). Use
                // OnAlternateRewardSelected(EndSelectionAndCompleteReward) — the same
                // path PaelsWing's SACRIFICE uses. Vanilla's Skip button uses
                // EndSelectionAndDoNotCompleteReward (leaves the reward claimable later
                // and is gated by the streamer's skip budget); CompleteReward marks the
                // reward consumed outright (no re-claim, no budget interaction), which
                // matches the design intent: chat's #0 vote is a permanent decline.
                // Without this, two bugs:
                //   1. Streamer could click "Add a card to your deck" again and re-vote.
                //   2. When the streamer's skip budget is exhausted, the "do not
                //      complete" path's gate no-ops the call — chat-skip became a
                //      soft-lock until a card was picked.
                // (v0.106.1 renamed DismissScreenAndKeepReward → EndSelectionAndDoNot-
                // CompleteReward and DismissScreenAndRemoveReward → EndSelectionAnd-
                // CompleteReward; vanilla CardReward.cs maps CompleteReward to
                // rewardComplete=true, identical pre-rename semantics.)
                TiLog.Info("[SlayTheStreamer2][card-vote] chat voted Skip (#0); removing reward from screen");
                coordinator.Dispatcher.Post(() => ResumeSkipOnMainThread(screen, runIdAtStart, optionsSig));
                return;
            }

            int resolvedCardIndex = cardIndex.Value;
            if (resolvedCardIndex < 0 || resolvedCardIndex >= optionsSnapshot.Count) {
                TiLog.Warn($"[SlayTheStreamer2][card-vote] resolvedCardIndex {resolvedCardIndex} out of range; using player click");
                resolvedCardIndex = playerClickIndex;
            }

            TiLog.Info($"[SlayTheStreamer2][card-vote] resume: applying winner #{resolvedCardIndex} on main thread");
            coordinator.Dispatcher.Post(() => ResumeOnMainThread(screen, resolvedCardIndex, playerClickIndex, runIdAtStart, optionsSig));
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][card-vote] HandleVoteAsync threw; attempting fallback resume with player click", ex);
            try {
                coordinator.Dispatcher.Post(() => ResumeOnMainThread(screen, playerClickIndex, playerClickIndex, runIdAtStart, optionsSig));
            } catch (Exception postEx) {
                TiLog.Error("[SlayTheStreamer2][card-vote] fallback resume Post itself threw; resetting flags", postEx);
                _activeSession = null;
                Interlocked.Exchange(ref _resumeInProgress, 0);
                Interlocked.Exchange(ref _voteInProgress, 0);
            }
        }
    }

    private static void ResumeOnMainThread(
        NCardRewardSelectionScreen screen,
        int preferredIndex,
        int playerClickIndex,
        string? runIdAtStart,
        OptionsSignature snapshotSig) {
        Interlocked.Exchange(ref _resumeInProgress, 1);
        try {
            if (!GodotObject.IsInstanceValid(screen)) {
                TiLog.Warn("[SlayTheStreamer2][card-vote] resume: screen no longer valid; dropping");
                // Most likely cause: vanilla teardown raced our resume Post (run-abandon,
                // game-over, etc.) and freed the screen before IsAbandoned/IsGameOver
                // checks could fire below. Chat saw the normal close receipt already
                // ("Chat chose X: Y"); send the cancellation override so chat knows
                // the pick didn't actually apply.
                SendCancellationReceipt();
                return;
            }

            // Run-state liveness checks. Catches:
            //  - Run abandoned via menu (RunManager.IsAbandoned set true in AbandonInternal — RunState
            //    survives in memory during teardown, so seed-only check below misses this case)
            //  - Player died (RunState.IsGameOver true once GuaranteeKillAllPlayers ran)
            //  - State fully torn down (DebugOnlyGetState null)
            // Seed-only guard remains as a final check for "started a NEW run with a different seed
            // before our resume Posted" — rare but possible.
            if (!IsRunLiveForResume("resume", runIdAtStart)) {
                return;   // helper already sent cancellation receipt
            }

            // Options-signature check — detects reroll specifically (reroll uniquely rebuilds
            // _options via RefreshOptions). Hover/animation/focus changes do NOT touch _options,
            // so this gives us reroll-detection with zero false positives — unlike the previous
            // holder-signature approach which fired spuriously on normal UI lifecycle events.
            var currentOptions = GetCurrentOptions(screen);
            if (currentOptions is null || !snapshotSig.Matches(currentOptions)) {
                TiLog.Warn("[SlayTheStreamer2][card-vote] resume aborted: card selection changed before apply");
                SendCancellationReceipt();
                return;
            }

            // Re-derive holders from current screen state for the actual SelectCard call.
            // Holders may have been re-created during the vote window (e.g., hover animations) —
            // OK to use the current instances since options identity is what we already verified.
            var currentHolders = GetCurrentHolders(screen);
            if (currentHolders is null || currentHolders.Count != currentOptions.Count) {
                TiLog.Warn("[SlayTheStreamer2][card-vote] resume aborted: holders missing or out of sync with options");
                SendCancellationReceipt();
                return;
            }

            // Bounds check
            int applyIndex = preferredIndex;
            if (applyIndex < 0 || applyIndex >= currentHolders.Count) {
                TiLog.Warn($"[SlayTheStreamer2][card-vote] preferred index {applyIndex} out of range; falling back to player click");
                applyIndex = playerClickIndex;
            }
            if (applyIndex < 0 || applyIndex >= currentHolders.Count) {
                TiLog.Warn("[SlayTheStreamer2][card-vote] resume: neither preferred nor player index valid; dropping");
                return;
            }

            // Re-derive holder from current screen state (NOT captured ref) and apply via reflection
            // (SelectCard is private — see Prepare's hard check on _selectCardMethod).
            var winnerHolder = currentHolders[applyIndex];
            var method = _selectCardMethod.Value;
            if (method is null) {
                TiLog.Error("[SlayTheStreamer2][card-vote] resume: SelectCard method not found");
                return;
            }
            method.Invoke(screen, new object[] { winnerHolder });
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][card-vote] resume threw", ex);
        } finally {
            _activeSession = null;
            Interlocked.Exchange(ref _resumeInProgress, 0);
            Interlocked.Exchange(ref _voteInProgress, 0);
        }
    }

    private static void ResumeSkipOnMainThread(
        NCardRewardSelectionScreen screen,
        string? runIdAtStart,
        OptionsSignature snapshotSig) {
        try {
            if (!GodotObject.IsInstanceValid(screen)) {
                TiLog.Warn("[SlayTheStreamer2][card-vote] skip-resume: screen no longer valid; dropping");
                SendCancellationReceipt();
                return;
            }

            // Run-state liveness checks (same guards as ResumeOnMainThread).
            if (!IsRunLiveForResume("skip-resume", runIdAtStart)) {
                return;   // helper already sent cancellation receipt
            }

            // Options-signature check — skip still requires the same options were present.
            var currentOptions = GetCurrentOptions(screen);
            if (currentOptions is null || !snapshotSig.Matches(currentOptions)) {
                TiLog.Warn("[SlayTheStreamer2][card-vote] skip-resume aborted: card selection changed before apply");
                SendCancellationReceipt();
                return;
            }

            var method = _onAlternateRewardSelectedMethod.Value;
            if (method is null) {
                TiLog.Warn("[SlayTheStreamer2][card-vote] OnAlternateRewardSelected(int) not found; chat Skip vote ignored — streamer must dismiss the sub-screen manually");
                // Intentionally NO SendCancellationReceipt: the vote completed normally (chat got
                // a "Chat chose #0: Skip" close-receipt). The mechanism is what failed, not the vote;
                // a cancellation receipt would mislead chat into thinking their vote was invalid.
                return;
            }

            // Resolve Skip's index in _extraOptions. Our CardRewardAlternative_Generate_Postfix
            // flips Skip's AfterSelected to EndSelectionAndCompleteReward so invoking the alt
            // here consumes the reward outright (sub-screen closes; parent button removed via
            // vanilla's natural RewardCollectedFrom path). See the class doc-comment on
            // [CardRewardAlternative_Generate_Postfix] below for full design rationale.
            var skipIndex = FindSkipAlternativeIndex(screen);
            if (skipIndex is null) {
                TiLog.Warn("[SlayTheStreamer2][card-vote] chat-skip resume: no Skip alt in _extraOptions (CanSkip=false reward?); sub-screen not dismissed");
                return;
            }

            // Clear _voteInProgress before invoking so the vote-mid-flight branch of the
            // OnAlternateRewardSelected prefix doesn't fire. Set _chatSkipResumeInProgress
            // so the prefix's streamer-Skip budget gate (which sees a "normal" non-vote
            // invocation) knows to PASS without consuming budget — chat-skip is budget-free
            // by design. _resumeInProgress is NOT set: we're not re-entering SelectCard.
            _activeSession = null;
            Interlocked.Exchange(ref _voteInProgress, 0);
            Interlocked.Exchange(ref _chatSkipResumeInProgress, 1);
            TiLog.Info($"[SlayTheStreamer2][card-vote] skip-resume: invoking OnAlternateRewardSelected({skipIndex.Value}) (Skip alt, flipped to EndSelectionAndCompleteReward)");
            try {
                method.Invoke(screen, new object[] { skipIndex.Value });
            } finally {
                Interlocked.Exchange(ref _chatSkipResumeInProgress, 0);
            }
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][card-vote] skip-resume threw", ex);
        } finally {
            // _resumeInProgress is NOT set in the skip path (we don't re-enter SelectCard).
            // The reset is defensive belt-and-suspenders against future refactor where this
            // path might gain a re-entry. Currently a no-op.
            _activeSession = null;
            Interlocked.Exchange(ref _resumeInProgress, 0);
            Interlocked.Exchange(ref _voteInProgress, 0);
        }
    }

    /// <summary>
    /// Returns true if the run is still in a state where a vote resume can safely apply.
    /// Logs the abort reason and sends a cancellation receipt on failure.
    /// The logPrefix parameter distinguishes the caller in logs (e.g. "resume" vs "skip-resume").
    /// </summary>
    private static bool IsRunLiveForResume(string logPrefix, string? runIdAtStart) {
        try {
            var rm = RunManager.Instance;
            if (rm is null) {
                TiLog.Warn($"[SlayTheStreamer2][card-vote] {logPrefix} aborted: RunManager.Instance is null");
                SendCancellationReceipt();
                return false;
            }
            if (rm.IsAbandoned) {
                TiLog.Warn($"[SlayTheStreamer2][card-vote] {logPrefix} aborted: run was abandoned during vote");
                SendCancellationReceipt();
                return false;
            }
            var currentState = rm.DebugOnlyGetState();
            if (currentState is null) {
                TiLog.Warn($"[SlayTheStreamer2][card-vote] {logPrefix} aborted: run state is gone");
                SendCancellationReceipt();
                return false;
            }
            if (currentState.IsGameOver) {
                TiLog.Warn($"[SlayTheStreamer2][card-vote] {logPrefix} aborted: run is over (player dead)");
                SendCancellationReceipt();
                return false;
            }
            if (runIdAtStart is not null) {
                string? currentRunId = currentState.Rng?.StringSeed;
                if (currentRunId != runIdAtStart) {
                    TiLog.Warn($"[SlayTheStreamer2][card-vote] {logPrefix} aborted: run changed during vote");
                    SendCancellationReceipt();
                    return false;
                }
            }
            return true;
        } catch (Exception ex) {
            TiLog.Warn($"[SlayTheStreamer2][card-vote] {logPrefix} aborted: liveness check threw ({ex.Message})");
            SendCancellationReceipt();
            return false;
        }
    }

    private static void SendCancellationReceipt() {
        var coordinator = Voter.Default;
        var state = coordinator?.Chat?.State;
        if (state != ChatConnectionState.ConnectedReadWrite) {
            TiLog.Warn($"[SlayTheStreamer2][card-vote] cancellation receipt skipped: chat state is {state?.ToString() ?? "null"}");
            return;
        }
        _ = coordinator!.Chat.SendMessageAsync(
            "Vote result ignored — card selection changed before apply",
            OutgoingMessagePriority.Normal);
        TiLog.Info("[SlayTheStreamer2][card-vote] cancellation receipt queued");
    }

    /// <summary>
    /// Three responsibilities, in priority order:
    ///
    /// 1. <b>Pass our chat-skip reflective invoke through.</b> When chat votes Skip, the
    ///    resume code reflectively invokes <c>OnAlternateRewardSelected(skipIndex)</c>
    ///    with <see cref="_chatSkipResumeInProgress"/> set. Without this branch the
    ///    streamer-Skip budget gate below would charge the streamer's budget for a
    ///    chat-side action.
    ///
    /// 2. <b>Block sub-screen alt selections (Skip / Reroll / etc.) while a card vote
    ///    is in progress.</b> Once chat is voting, the streamer's agency has been
    ///    transferred — they can't see the tally trending the wrong way and bail.
    ///    Sub-screen stays open until the vote completes; resume's SelectCard re-call
    ///    closes it normally.
    ///
    /// 3. <b>Gate streamer-Skip clicks against the per-act budget.</b> v0.106.1 chat-skip
    ///    semantics consume the reward outright (our <see cref="CardRewardAlternative_Generate_Postfix"/>
    ///    flipped Skip's <c>AfterSelected</c> to <c>EndSelectionAndCompleteReward</c>),
    ///    so a streamer click would also consume the reward. Without this gate the
    ///    <c>cardSkipsPerAct</c> setting would have no effect on streamer-side clicks.
    ///    Allowed click → decrement budget + receipt + let vanilla run. Exhausted →
    ///    return false (no-op; reward stays alive; streamer must engage chat to consume).
    ///
    /// NOTE: this prefix does NOT block reroll on its own. Vanilla wires the reroll
    /// button click as two statements — OnAlternateRewardSelected(rerollIndex) AND
    /// TaskHelper.RunSafely(rewardOption.OnSelect()) (see
    /// NCardRewardSelectionScreen.RefreshOptions line 215-225). The Reroll alternative's
    /// AfterSelected is DoNothing, so vanilla's OnAlternateRewardSelected body is
    /// essentially a no-op for Reroll either way. The actual reroll runs in OnSelect()
    /// which calls CardReward.Reroll(). See CardReward_Reroll_Prefix below for the
    /// parallel block on that path.
    /// </summary>
    [HarmonyPatch(typeof(NCardRewardSelectionScreen), "OnAlternateRewardSelected")]
    internal static class NCardRewardSelectionScreen_OnAlternateRewardSelected_Prefix {
        static bool Prepare() => true;
        static bool Prefix(NCardRewardSelectionScreen __instance, int index) {
            // (1) Our chat-skip reflective invoke — pass through, no budget cost.
            if (_chatSkipResumeInProgress == 1) return true;

            // (2) Vote in progress — streamer cannot bail via alt-select, but an
            // armed Skip click with override budget ends the vote as an override.
            if (_voteInProgress == 1) {
                if (TryOverrideWithSkip(__instance, index)) return false;
                TiLog.Info("[SlayTheStreamer2][card-vote] OnAlternateRewardSelected blocked: vote in progress");
                return false;
            }

            // (3) Streamer-Skip budget gate. Only applies if this click is the Skip alt;
            // other alts (Reroll, Sacrifice from PaelsWing, future hooks) pass unchanged.
            var skipIndex = FindSkipAlternativeIndex(__instance);
            if (skipIndex.HasValue && index == skipIndex.Value) {
                if (!CardRewardSkipGatePatch.TryConsumeStreamerSkip(__instance)) {
                    return false;   // budget exhausted (or gate inactive in a way that blocks); silent no-op
                }
            }
            return true;
        }
    }

    /// <summary>
    /// Block CardReward.Reroll() when a vote is in progress. Vanilla's reroll button
    /// click invokes this method via the CardRewardAlternative.OnSelect callback (see
    /// the design note on NCardRewardSelectionScreen_OnAlternateRewardSelected_Prefix
    /// above). Without this prefix, the streamer could click reroll late in the
    /// countdown after seeing chat's trending vote, vanilla would shuffle _options,
    /// and our resume's options-signature check would only fire AFTER the timer
    /// expired (firing a "Vote result ignored" cancellation). That's bad UX -- chat
    /// gets a misleading "we voted but it was ignored" rather than the simpler "you
    /// can't reroll mid-vote".
    ///
    /// v0.106.1: Reroll() changed from `async Task` to `void`. The Prefix loses its
    /// `__result` parameter — Harmony rejects ref-__result on void methods with
    /// "Cannot get result from void method". Returning false still suppresses vanilla;
    /// no caller can await a void return anyway, so nothing else needs adjustment.
    /// </summary>
    [HarmonyPatch(typeof(CardReward), nameof(CardReward.Reroll))]
    internal static class CardReward_Reroll_Prefix {
        static bool Prepare() => true;
        static bool Prefix() {
            if (_voteInProgress == 1) {
                TiLog.Info("[SlayTheStreamer2][card-vote] CardReward.Reroll blocked: vote in progress");
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Re-targets the vanilla "Skip" alternative's <c>AfterSelected</c> from
    /// <c>EndSelectionAndDoNotCompleteReward</c> (vanilla "skip but keep reward
    /// claimable") to <c>EndSelectionAndCompleteReward</c> (consume reward outright,
    /// remove from parent NRewardsScreen via vanilla's natural RewardCollectedFrom
    /// path). This is the v0.106.1 replacement for the old chat-skip mechanism — old
    /// vanilla let us pass an action enum directly via OnAlternateRewardSelected;
    /// new vanilla decodes the int result against the locally-Generate'd list and
    /// reads the alt's AfterSelected. By flipping the existing Skip entry in place
    /// (no addition) we stay within the 2-alternatives cap that
    /// <see cref="CardRewardAlternative.Generate"/> enforces at line 53-55.
    ///
    /// Streamer interaction: the Skip button remains visible (the vote popup
    /// anchors its #0 indicator to it when CardSkipAsVoteOption is on), and
    /// changing AfterSelected to <c>EndSelectionAndCompleteReward</c> also reassigns
    /// the alt's hotkey away from <c>MegaInput.cancel</c> (per CardRewardAlternative
    /// ctor line 34), so Escape no longer triggers Skip either. Escape falls through
    /// to NOverlayStack pop → <c>_completionSource = null</c> → <c>rewardComplete =
    /// false</c> → reward stays alive on parent (streamer can re-open under
    /// mandatory-look).
    ///
    /// Multi-pick caveat: <see cref="Hook.ShouldAllowSelectingMoreCardRewards"/> has
    /// zero overrides in v0.106.1, so this design is correct for every current
    /// reward. If a future build ships a multi-pick relic, chat-skip on any pick will
    /// terminate the whole reward (one chat-skip vote ends multi-pick early) —
    /// matches old <c>DismissScreenAndRemoveReward</c> semantics.
    /// </summary>
    [HarmonyPatch(typeof(CardRewardAlternative), nameof(CardRewardAlternative.Generate))]
    internal static class CardRewardAlternative_Generate_Postfix {
        static bool Prepare() => true;

        static void Postfix(ref IReadOnlyList<CardRewardAlternative> __result) {
            try {
                // Vanilla Generate() builds a List<>. Downcast to mutate in place; if a
                // future build returns a different IReadOnlyList implementation, fall back
                // to copying into a new List and assigning __result.
                if (__result is not List<CardRewardAlternative> list) {
                    list = __result.ToList();
                    __result = list;
                }
                for (int i = 0; i < list.Count; i++) {
                    if (list[i]?.OptionId == "Skip" &&
                        list[i].AfterSelected == PostAlternateCardRewardAction.EndSelectionAndDoNotCompleteReward) {
                        list[i] = new CardRewardAlternative("Skip", PostAlternateCardRewardAction.EndSelectionAndCompleteReward);
                        return;
                    }
                }
            } catch (Exception ex) {
                TiLog.Error("[SlayTheStreamer2][card-vote] Generate postfix (Skip alt flip) failed", ex);
            }
        }
    }

}
