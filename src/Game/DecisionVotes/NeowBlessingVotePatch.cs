using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Internal;
using SlayTheStreamer2.Ti.Ui;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2.Game.DecisionVotes;

[HarmonyPatch(typeof(NEventRoom), nameof(NEventRoom.OptionButtonClicked))]
internal static class NeowBlessingVotePatch {
    private static int _voteInProgress;
    private static int _resumeInProgress;
    private static int _multiplayerWarnFired;
    private static readonly Lazy<FieldInfo?> _eventField =
        new(() => AccessTools.Field(typeof(NEventRoom), "_event"));

    internal static bool RunIdGuardEnabled { get; private set; } = true;

    static bool Prepare(MethodBase? original) {
        if (original is null) {
            if (_eventField.Value is null) {
                TiLog.Error("[SlayTheStreamer2][neow-vote] NEventRoom._event field not found; patch will not function");
                return false;
            }

            // Soft check: run-id accessor. Failure logs Warn but does NOT abort patch registration.
            try {
                var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                if (rm == null) {
                    TiLog.Warn("[SlayTheStreamer2][neow-vote] run-ID guard degraded:RunManager.Instance not reachable");
                    RunIdGuardEnabled = false;
                } else {
                    // Verify the type shape: RunManager exposes DebugOnlyGetState; deeper Rng.StringSeed
                    // shape is verified at runtime (state may be null at Prepare — no run in progress).
                    var rmType = rm.GetType();
                    var stateMethod = rmType.GetMethod("DebugOnlyGetState");
                    if (stateMethod == null) {
                        TiLog.Warn("[SlayTheStreamer2][neow-vote] run-ID guard degraded:DebugOnlyGetState() not found");
                        RunIdGuardEnabled = false;
                    }
                }
            } catch (Exception ex) {
                TiLog.Warn($"[SlayTheStreamer2][neow-vote] run-ID guard degraded:Prepare soft check threw: {ex.Message}");
                RunIdGuardEnabled = false;
            }

            return true;
        }

        var parameters = original.GetParameters();
        if (parameters.Length != 2 ||
            parameters[0].ParameterType != typeof(EventOption) ||
            parameters[1].ParameterType != typeof(int)) {
            TiLog.Error($"[SlayTheStreamer2][neow-vote] target signature mismatch: {original.DeclaringType?.FullName}.{original.Name}({string.Join(", ", parameters.Select(p => p.ParameterType.Name))})");
            return false;
        }
        TiLog.Info($"[SlayTheStreamer2][neow-vote] target resolved: {original.DeclaringType?.FullName}.{original.Name}");
        return true;
    }

    static bool Prefix(NEventRoom __instance, EventOption option, int index) {
        if (_resumeInProgress == 1) return true;
        if (!IsNeowEvent(__instance)) return true;
        if (option.IsLocked || option.IsProceed) return true;

        if (TryGetEventOwnerPlayerCount(__instance) is int playerCount && playerCount > 1) {
            if (Interlocked.CompareExchange(ref _multiplayerWarnFired, 1, 0) == 0) {
                TiLog.Warn("[SlayTheStreamer2][neow-vote] multiplayer detected (Players.Count > 1); bailing to vanilla (further bail-outs at Debug)");
            } else {
                TiLog.Debug("[SlayTheStreamer2][neow-vote] multiplayer bail-out");
            }
            return true;
        }

        var coordinator = Voter.Default;
        if (coordinator is null) return true;
        if (coordinator.Chat.State is not ChatConnectionState.ConnectedReadWrite) {
            TiLog.Debug($"[SlayTheStreamer2][neow-vote] chat not in ConnectedReadWrite (state={coordinator.Chat.State}); bailing to vanilla");
            return true;
        }

        string? runIdAtStart = null;
        if (RunIdGuardEnabled) {
            try {
                var state = MegaCrit.Sts2.Core.Runs.RunManager.Instance?.DebugOnlyGetState();
                runIdAtStart = state?.Rng?.StringSeed;
                if (runIdAtStart == null) {
                    TiLog.Warn("[SlayTheStreamer2][neow-vote] run-ID guard degraded for this vote:null state or null seed at start");
                }
            } catch (Exception ex) {
                TiLog.Warn($"[SlayTheStreamer2][neow-vote] run-ID guard degraded for this vote:{ex.Message}");
            }
        }

        if (Interlocked.CompareExchange(ref _voteInProgress, 1, 0) != 0) {
            TiLog.Debug("[SlayTheStreamer2][neow-vote] repeat click during open vote; suppressed");
            return false;
        }

        var liveOptions = GetCurrentOptions(__instance);
        if (liveOptions is null || liveOptions.Count == 0) {
            Interlocked.Exchange(ref _voteInProgress, 0);
            return true;
        }
        var optionsSnapshot = liveOptions.ToList();
        var labels = optionsSnapshot.Select(o => o.Title.GetFormattedText()).ToList();

        VoteSession session;
        try {
            session = coordinator.Start("Neow's Bonus", labels, TimeSpan.FromSeconds(30));
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][neow-vote] Voter.Default.Start threw; falling back to vanilla", ex);
            Interlocked.Exchange(ref _voteInProgress, 0);
            return true;
        }

        try {
            __instance.Layout?.DisableEventOptions();
        } catch (Exception ex) {
            TiLog.Warn($"[SlayTheStreamer2][neow-vote] DisableEventOptions threw (continuing): {ex.Message}");
        }

        TiLog.Info($"[SlayTheStreamer2][neow-vote] opening vote for {optionsSnapshot.Count} options; player clicked #{index}");
        _ = HandleVoteAsync(coordinator, __instance, session, optionsSnapshot, index, runIdAtStart);
        return false;
    }

    private static async Task HandleVoteAsync(VoteCoordinator coordinator, NEventRoom room,
                                              VoteSession session, IReadOnlyList<EventOption> snapshot,
                                              int playerClickIndex, string? runIdAtStart) {
        try {
            coordinator.Dispatcher.Post(() => VoteTallyLabel.AttachTo(session));

            int winnerIndex;
            try {
                winnerIndex = await session.AwaitWinnerAsync();
            } catch (Exception ex) {
                TiLog.Error("[SlayTheStreamer2][neow-vote] AwaitWinnerAsync threw; falling back to player click", ex);
                winnerIndex = playerClickIndex;
            }

            if (winnerIndex < 0 || winnerIndex >= snapshot.Count) {
                TiLog.Warn($"[SlayTheStreamer2][neow-vote] winnerIndex {winnerIndex} out of snapshot range; using player click");
                winnerIndex = playerClickIndex;
            }

            TiLog.Info($"[SlayTheStreamer2][neow-vote] resume: applying winner #{winnerIndex} on main thread");
            coordinator.Dispatcher.Post(() => ResumeOnMainThread(room, snapshot, winnerIndex, playerClickIndex, runIdAtStart));
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][neow-vote] HandleVoteAsync threw; attempting fallback resume with player click", ex);
            try {
                coordinator.Dispatcher.Post(() => ResumeOnMainThread(room, snapshot, playerClickIndex, playerClickIndex, runIdAtStart));
            } catch (Exception postEx) {
                TiLog.Error("[SlayTheStreamer2][neow-vote] fallback resume Post itself threw; resetting flags", postEx);
                Interlocked.Exchange(ref _resumeInProgress, 0);
                Interlocked.Exchange(ref _voteInProgress, 0);
            }
        }
    }

    private static void ResumeOnMainThread(NEventRoom room, IReadOnlyList<EventOption> snapshot,
                                           int preferredIndex, int playerClickIndex, string? runIdAtStart) {
        Interlocked.Exchange(ref _resumeInProgress, 1);
        try {
            if (!GodotObject.IsInstanceValid(room)) {
                TiLog.Warn("[SlayTheStreamer2][neow-vote] resume: room no longer valid; dropping resume");
                return;
            }
            if (!IsNeowEvent(room)) {
                TiLog.Warn("[SlayTheStreamer2][neow-vote] resume: active event is no longer Neow; dropping resume");
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
                var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                if (rm is null) {
                    TiLog.Warn("[SlayTheStreamer2][neow-vote] resume aborted: RunManager.Instance is null");
                    return;
                }
                if (rm.IsAbandoned) {
                    TiLog.Warn("[SlayTheStreamer2][neow-vote] resume aborted: run was abandoned during vote");
                    return;
                }
                var currentState = rm.DebugOnlyGetState();
                if (currentState is null) {
                    TiLog.Warn("[SlayTheStreamer2][neow-vote] resume aborted: run state is gone");
                    return;
                }
                if (currentState.IsGameOver) {
                    TiLog.Warn("[SlayTheStreamer2][neow-vote] resume aborted: run is over (player dead)");
                    return;
                }
                if (runIdAtStart != null) {
                    string? currentRunId = currentState.Rng?.StringSeed;
                    if (currentRunId != runIdAtStart) {
                        TiLog.Warn("[SlayTheStreamer2][neow-vote] resume aborted: run changed during vote");
                        return;
                    }
                }
            } catch (Exception ex) {
                TiLog.Warn($"[SlayTheStreamer2][neow-vote] resume aborted: liveness check threw ({ex.Message})");
                return;
            }
            var currentOptions = GetCurrentOptions(room)?.ToList();
            if (currentOptions is null || currentOptions.Count == 0) {
                TiLog.Warn("[SlayTheStreamer2][neow-vote] resume: no current options; dropping");
                return;
            }

            int applyIndex = preferredIndex;
            if (applyIndex < 0 || applyIndex >= currentOptions.Count) {
                TiLog.Warn($"[SlayTheStreamer2][neow-vote] resume: preferred index {applyIndex} out of range; falling back to player click");
                applyIndex = playerClickIndex;
            }
            if (applyIndex < 0 || applyIndex >= currentOptions.Count) {
                TiLog.Warn($"[SlayTheStreamer2][neow-vote] resume: neither preferred nor player index valid (options now {currentOptions.Count}); dropping");
                return;
            }

            var winnerOption = currentOptions[applyIndex];
            room.OptionButtonClicked(winnerOption, applyIndex);
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][neow-vote] resume threw", ex);
        } finally {
            Interlocked.Exchange(ref _resumeInProgress, 0);
            Interlocked.Exchange(ref _voteInProgress, 0);
        }
    }

    private static bool IsNeowEvent(NEventRoom room) {
        var eventModel = _eventField.Value?.GetValue(room);
        return eventModel is Neow;
    }

    private static IReadOnlyList<EventOption>? GetCurrentOptions(NEventRoom room) {
        var eventModel = _eventField.Value?.GetValue(room) as EventModel;
        return eventModel?.CurrentOptions;
    }

    private static int? TryGetEventOwnerPlayerCount(NEventRoom room) {
        var eventModel = _eventField.Value?.GetValue(room) as EventModel;
        return eventModel?.Owner?.RunState?.Players?.Count;
    }
}
