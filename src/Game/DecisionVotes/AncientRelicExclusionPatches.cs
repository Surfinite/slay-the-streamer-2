using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models.Events;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// Per-pool Harmony postfixes that strip specific relics from ancient-event
/// option pools so they never appear as a player/chat choice.
///
/// To add another exclusion:
///   1. Find the ancient + pool getter that contains the relic in
///      <c>decompiled/sts2/MegaCrit.Sts2.Core.Models.Events/&lt;Ancient&gt;.cs</c>.
///   2. Add a sibling patch class below following the same shape.
///   3. If the pool has multiple relics and only one needs excluding, filter
///      <c>__result</c> by inspecting <c>EventOption.TextKey</c> (the L10N
///      key, contains the relic id) instead of clearing the whole list.
///
/// Do NOT patch <c>EventRelicPool.GenerateAllRelics</c>. That's the master
/// 140-relic registry referenced by run-history, achievements, and other
/// audits — exclusions there have unknown side-effects.
/// </summary>
[HarmonyPatch(typeof(Pael), "OptionPool2", MethodType.Getter)]
internal static class PaelOptionPool2NoWingPatch {
    private static int _firstFireLogged;

    /// <summary>
    /// Pael's <c>OptionPool2</c> is a 1-element list containing only PaelsWing
    /// in vanilla; clearing it leaves the middle option slot to be filled by
    /// PaelsClaw / PaelsTooth (deck-conditional) and PaelsGrowth (always
    /// appended). When neither deck condition is met the slot becomes a
    /// deterministic PaelsGrowth — accepted as the cost of removing Wing.
    /// </summary>
    static void Postfix(ref List<EventOption> __result) {
        __result = new List<EventOption>();
        if (System.Threading.Interlocked.Exchange(ref _firstFireLogged, 1) == 0) {
            TiLog.Info("[SlayTheStreamer2][ancient-exclusion] Pael.OptionPool2 cleared (PaelsWing removed)");
        }
    }
}
