// src/Game/DecisionVotes/RestSiteOriginTags.cs
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>Tags every CardReward a rest-site heal produced (Dream Catcher, including
/// Dense Vegetation's mimicked rest). Postfix on the static
/// Hook.ModifyRestSiteHealRewards (sole caller HealRestSiteOption.ExecuteRestSiteHeal).
/// A registration miss silently makes Dream Catcher an unskippable event reward
/// (fail-safe but wrong), so Prepare logs an Error.</summary>
internal static class RestSiteOriginTags {
    private static readonly ConditionalWeakTable<CardReward, object> Tags = new();
    private static readonly object Marker = new();

    internal static bool IsTagged(CardReward reward) => Tags.TryGetValue(reward, out _);

    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyRestSiteHealRewards))]
    internal static class TagPatch {
        static bool Prepare(MethodBase? original) {
            if (original is not null) return true;
            if (AccessTools.Method(typeof(Hook), nameof(Hook.ModifyRestSiteHealRewards)) is null) {
                TiLog.Error("[SlayTheStreamer2][card-scope] Hook.ModifyRestSiteHealRewards not found; Dream Catcher rewards will classify as event rewards");
                return false;
            }
            return true;
        }

        static void Postfix(IRunState runState, Player player, List<Reward> rewards) {
            try {
                int tagged = 0;
                foreach (var reward in rewards) {
                    if (reward is CardReward cardReward) { Tags.GetValue(cardReward, _ => Marker); tagged++; }
                }
                if (tagged > 0) TiLog.Info($"[SlayTheStreamer2][card-scope] tagged {tagged} rest-site card reward(s)");
            } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][card-scope] rest-site tagging failed", ex); }
        }
    }
}
