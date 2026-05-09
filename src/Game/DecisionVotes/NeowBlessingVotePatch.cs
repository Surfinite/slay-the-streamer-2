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

    static bool Prepare(MethodBase? original) {
        if (original is null) {
            if (_eventField.Value is null) {
                TiLog.Error("[neow-vote] NEventRoom._event field not found; patch will not function");
                return false;
            }
            return true;
        }

        var parameters = original.GetParameters();
        if (parameters.Length != 2 ||
            parameters[0].ParameterType != typeof(EventOption) ||
            parameters[1].ParameterType != typeof(int)) {
            TiLog.Error($"[neow-vote] target signature mismatch: {original.DeclaringType?.FullName}.{original.Name}({string.Join(", ", parameters.Select(p => p.ParameterType.Name))})");
            return false;
        }
        TiLog.Info($"[neow-vote] target resolved: {original.DeclaringType?.FullName}.{original.Name}");
        return true;
    }

    static bool Prefix(NEventRoom __instance, EventOption option, int index) {
        if (_resumeInProgress == 1) return true;
        if (!IsNeowEvent(__instance)) return true;
        if (option.IsLocked || option.IsProceed) return true;

        if (TryGetEventOwnerPlayerCount(__instance) is int playerCount && playerCount > 1) {
            if (Interlocked.CompareExchange(ref _multiplayerWarnFired, 1, 0) == 0) {
                TiLog.Warn("[neow-vote] multiplayer detected (Players.Count > 1); bailing to vanilla (further bail-outs at Debug)");
            } else {
                TiLog.Debug("[neow-vote] multiplayer bail-out");
            }
            return true;
        }

        var coordinator = Voter.Default;
        if (coordinator is null) return true;
        if (coordinator.Chat.State is not ChatConnectionState.ConnectedReadWrite) {
            TiLog.Debug($"[neow-vote] chat not in ConnectedReadWrite (state={coordinator.Chat.State}); bailing to vanilla");
            return true;
        }

        if (Interlocked.CompareExchange(ref _voteInProgress, 1, 0) != 0) {
            TiLog.Debug("[neow-vote] repeat click during open vote — suppressed");
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
            TiLog.Error("[neow-vote] Voter.Default.Start threw; falling back to vanilla", ex);
            Interlocked.Exchange(ref _voteInProgress, 0);
            return true;
        }

        try {
            __instance.Layout?.DisableEventOptions();
        } catch (Exception ex) {
            TiLog.Warn($"[neow-vote] DisableEventOptions threw (continuing): {ex.Message}");
        }

        TiLog.Info($"[neow-vote] opening vote for {optionsSnapshot.Count} options; player clicked #{index}");
        _ = HandleVoteAsync(coordinator, __instance, session, optionsSnapshot, index);
        return false;
    }

    private static async Task HandleVoteAsync(VoteCoordinator coordinator, NEventRoom room,
                                              VoteSession session, IReadOnlyList<EventOption> snapshot,
                                              int playerClickIndex) {
        try {
            coordinator.Dispatcher.Post(() => VoteTallyLabel.AttachTo(session));

            int winnerIndex;
            try {
                winnerIndex = await session.AwaitWinnerAsync();
            } catch (Exception ex) {
                TiLog.Error("[neow-vote] AwaitWinnerAsync threw; falling back to player click", ex);
                winnerIndex = playerClickIndex;
            }

            if (winnerIndex < 0 || winnerIndex >= snapshot.Count) {
                TiLog.Warn($"[neow-vote] winnerIndex {winnerIndex} out of snapshot range; using player click");
                winnerIndex = playerClickIndex;
            }

            TiLog.Info($"[neow-vote] resume: applying winner #{winnerIndex} on main thread");
            coordinator.Dispatcher.Post(() => ResumeOnMainThread(room, snapshot, winnerIndex, playerClickIndex));
        } catch (Exception ex) {
            TiLog.Error("[neow-vote] HandleVoteAsync threw; attempting fallback resume with player click", ex);
            try {
                coordinator.Dispatcher.Post(() => ResumeOnMainThread(room, snapshot, playerClickIndex, playerClickIndex));
            } catch (Exception postEx) {
                TiLog.Error("[neow-vote] fallback resume Post itself threw; resetting flags", postEx);
                Interlocked.Exchange(ref _resumeInProgress, 0);
                Interlocked.Exchange(ref _voteInProgress, 0);
            }
        }
    }

    private static void ResumeOnMainThread(NEventRoom room, IReadOnlyList<EventOption> snapshot,
                                           int preferredIndex, int playerClickIndex) {
        Interlocked.Exchange(ref _resumeInProgress, 1);
        try {
            if (!GodotObject.IsInstanceValid(room)) {
                TiLog.Warn("[neow-vote] resume: room no longer valid; dropping resume");
                return;
            }
            if (!IsNeowEvent(room)) {
                TiLog.Warn("[neow-vote] resume: active event is no longer Neow; dropping resume");
                return;
            }
            var currentOptions = GetCurrentOptions(room)?.ToList();
            if (currentOptions is null || currentOptions.Count == 0) {
                TiLog.Warn("[neow-vote] resume: no current options; dropping");
                return;
            }

            int applyIndex = preferredIndex;
            if (applyIndex < 0 || applyIndex >= currentOptions.Count) {
                TiLog.Warn($"[neow-vote] resume: preferred index {applyIndex} out of range; falling back to player click");
                applyIndex = playerClickIndex;
            }
            if (applyIndex < 0 || applyIndex >= currentOptions.Count) {
                TiLog.Warn($"[neow-vote] resume: neither preferred nor player index valid (options now {currentOptions.Count}); dropping");
                return;
            }

            var winnerOption = currentOptions[applyIndex];
            room.OptionButtonClicked(winnerOption, applyIndex);
        } catch (Exception ex) {
            TiLog.Error("[neow-vote] resume threw", ex);
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
