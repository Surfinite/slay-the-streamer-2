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
        // Stub — fills in next tasks (33).
        return true;
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
