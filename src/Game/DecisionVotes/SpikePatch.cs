using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.DecisionVotes;

[HarmonyPatch(typeof(NRewardsScreen), "_Ready")]
internal static class SpikeReadyPatch {
    static void Postfix() => TiLog.Info("[SlayTheStreamer2][spike] NRewardsScreen._Ready fired");
}

// _ExitTree is NOT declared on NRewardsScreen (inherited from Godot.Node base);
// Harmony can't resolve it by name on the derived type. Use AfterOverlayClosed
// instead — declared directly on NRewardsScreen, called during overlay teardown
// before QueueFreeSafely. Confirmed by FATAL Init exception on the first spike build.
[HarmonyPatch(typeof(NRewardsScreen), "AfterOverlayClosed")]
internal static class SpikeAfterOverlayClosedPatch {
    static void Postfix() => TiLog.Info("[SlayTheStreamer2][spike] NRewardsScreen.AfterOverlayClosed fired");
}
