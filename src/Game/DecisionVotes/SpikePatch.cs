using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.DecisionVotes;

[HarmonyPatch(typeof(NRewardsScreen), "_Ready")]
internal static class SpikeReadyPatch {
    static void Postfix() => TiLog.Info("[SlayTheStreamer2][spike] NRewardsScreen._Ready fired");
}

[HarmonyPatch(typeof(NRewardsScreen), "_ExitTree")]
internal static class SpikeExitTreePatch {
    static void Postfix() => TiLog.Info("[SlayTheStreamer2][spike] NRewardsScreen._ExitTree fired");
}
