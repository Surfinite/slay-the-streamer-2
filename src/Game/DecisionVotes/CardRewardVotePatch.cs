using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
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
    private static int _multiplayerWarnFired;

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
        return host.GetChildren().OfType<NCardHolder>().ToList();
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
        if (coordinator.Chat.State is not ChatConnectionState.ConnectedReadWrite) {
            TiLog.Debug($"[SlayTheStreamer2][card-vote] chat not in ConnectedReadWrite (state={coordinator.Chat.State}); bailing to vanilla");
            return true;
        }

        // Atomic vote-in-progress flip
        if (Interlocked.CompareExchange(ref _voteInProgress, 1, 0) != 0) {
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
        var optionsSnapshot = options.ToList();
        var holdersSnapshot = holders.ToList();
        var labels = optionsSnapshot.Select(o => o.Card.Title).ToList();   // SPIKE-CORRECTED: was Card.Name.GetText()

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

        // Voter.Start with try/catch fallback to vanilla
        VoteSession session;
        try {
            session = coordinator.Start("Card Reward", labels, TimeSpan.FromSeconds(30));
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][card-vote] Voter.Default.Start threw; falling back to vanilla", ex);
            Interlocked.Exchange(ref _voteInProgress, 0);
            return true;
        }

        TiLog.Info($"[SlayTheStreamer2][card-vote] opening vote for {optionsSnapshot.Count} options; player clicked #{playerClickIndex}");
        _ = HandleVoteAsync(coordinator, __instance, session, optionsSnapshot, optionsSig, playerClickIndex, runIdAtStart);
        return false;
    }

    private static async Task HandleVoteAsync(
        VoteCoordinator coordinator,
        NCardRewardSelectionScreen screen,
        VoteSession session,
        IReadOnlyList<CardCreationResult> optionsSnapshot,
        OptionsSignature optionsSig,
        int playerClickIndex,
        string? runIdAtStart) {
        try {
            coordinator.Dispatcher.Post(() => VoteTallyLabel.AttachTo(session));

            int winnerIndex;
            try {
                winnerIndex = await session.AwaitWinnerAsync();
            } catch (Exception ex) {
                TiLog.Error("[SlayTheStreamer2][card-vote] AwaitWinnerAsync threw; falling back to player click", ex);
                winnerIndex = playerClickIndex;
            }

            if (winnerIndex < 0 || winnerIndex >= optionsSnapshot.Count) {
                TiLog.Warn($"[SlayTheStreamer2][card-vote] winnerIndex {winnerIndex} out of range; using player click");
                winnerIndex = playerClickIndex;
            }

            TiLog.Info($"[SlayTheStreamer2][card-vote] resume: applying winner #{winnerIndex} on main thread");
            coordinator.Dispatcher.Post(() => ResumeOnMainThread(screen, winnerIndex, playerClickIndex, runIdAtStart, optionsSig));
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][card-vote] HandleVoteAsync threw; attempting fallback resume with player click", ex);
            try {
                coordinator.Dispatcher.Post(() => ResumeOnMainThread(screen, playerClickIndex, playerClickIndex, runIdAtStart, optionsSig));
            } catch (Exception postEx) {
                TiLog.Error("[SlayTheStreamer2][card-vote] fallback resume Post itself threw; resetting flags", postEx);
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
            try {
                var rm = RunManager.Instance;
                if (rm is null) {
                    TiLog.Warn("[SlayTheStreamer2][card-vote] resume aborted: RunManager.Instance is null");
                    SendCancellationReceipt();
                    return;
                }
                if (rm.IsAbandoned) {
                    TiLog.Warn("[SlayTheStreamer2][card-vote] resume aborted: run was abandoned during vote");
                    SendCancellationReceipt();
                    return;
                }
                var currentState = rm.DebugOnlyGetState();
                if (currentState is null) {
                    TiLog.Warn("[SlayTheStreamer2][card-vote] resume aborted: run state is gone");
                    SendCancellationReceipt();
                    return;
                }
                if (currentState.IsGameOver) {
                    TiLog.Warn("[SlayTheStreamer2][card-vote] resume aborted: run is over (player dead)");
                    SendCancellationReceipt();
                    return;
                }
                if (runIdAtStart is not null) {
                    string? currentRunId = currentState.Rng?.StringSeed;
                    if (currentRunId != runIdAtStart) {
                        TiLog.Warn("[SlayTheStreamer2][card-vote] resume aborted: run changed during vote");
                        SendCancellationReceipt();
                        return;
                    }
                }
            } catch (Exception ex) {
                TiLog.Warn($"[SlayTheStreamer2][card-vote] resume aborted: liveness check threw ({ex.Message})");
                SendCancellationReceipt();
                return;
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
            Interlocked.Exchange(ref _resumeInProgress, 0);
            Interlocked.Exchange(ref _voteInProgress, 0);
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
    /// Block sub-screen alternate-reward selections (Skip / gold-instead / etc.) while a
    /// card vote is in progress. Once chat is voting, the streamer's agency has been
    /// transferred -- they can't see the tally trending the wrong way and bail. Sub-screen
    /// stays open until the vote completes; resume's SelectCard re-call closes it normally.
    /// Outside of a vote, alternates work as vanilla intends.
    ///
    /// NOTE: this prefix does NOT block reroll on its own. Vanilla wires the reroll
    /// button click as two statements -- OnAlternateRewardSelected(AfterSelected) AND
    /// TaskHelper.RunSafely(rewardOption.OnSelect()) (see
    /// NCardRewardSelectionScreen.RefreshOptions line 209-213). The reroll alternative's
    /// AfterSelected is None, so vanilla's OnAlternateRewardSelected body is a no-op
    /// either way -- blocking it changes nothing for reroll. The actual reroll runs in
    /// OnSelect() which calls CardReward.Reroll(). See CardReward_Reroll_Prefix below
    /// for the parallel block on that path.
    /// </summary>
    [HarmonyPatch(typeof(NCardRewardSelectionScreen), "OnAlternateRewardSelected")]
    internal static class NCardRewardSelectionScreen_OnAlternateRewardSelected_Prefix {
        static bool Prepare() => true;
        static bool Prefix() {
            if (_voteInProgress == 1) {
                TiLog.Info("[SlayTheStreamer2][card-vote] OnAlternateRewardSelected blocked: vote in progress");
                return false;   // skip vanilla; sub-screen stays open until vote resumes via SelectCard
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
    /// Patching an `async Task`-returning method: Harmony's Prefix runs synchronously
    /// before the async state machine spins up. Returning false skips vanilla; setting
    /// `__result = Task.CompletedTask` keeps any caller-side `await` from NREing on a
    /// null Task. TaskHelper.RunSafely is the typical caller and tolerates failures,
    /// but a completed Task is cleaner.
    /// </summary>
    [HarmonyPatch(typeof(CardReward), nameof(CardReward.Reroll))]
    internal static class CardReward_Reroll_Prefix {
        static bool Prepare() => true;
        static bool Prefix(ref System.Threading.Tasks.Task __result) {
            if (_voteInProgress == 1) {
                TiLog.Info("[SlayTheStreamer2][card-vote] CardReward.Reroll blocked: vote in progress");
                __result = System.Threading.Tasks.Task.CompletedTask;
                return false;
            }
            return true;
        }
    }
}
