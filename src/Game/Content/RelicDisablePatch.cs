using System;
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.Content;

/// <summary>Spec section 7.3. Postfix on the base RelicModel.IsAllowed(IRunState): in a
/// sealed run, Leafy Poultice and Precarious Shears are not allowed. One predicate
/// covers the Neow page (IsAllowedAtNeow defers to IsAllowed), Neow's Bones, every grab
/// bag pull (chests, elites, Bossy Relics' expansion) and shops. Neither relic overrides
/// the predicate. Console `relic LEAFY_POULTICE` bypasses IsAllowed (informational).</summary>
[HarmonyPatch(typeof(RelicModel), nameof(RelicModel.IsAllowed))]
internal static class RelicDisablePatch {
    private static readonly HashSet<string> Disabled = new(StringComparer.Ordinal) { "LEAFY_POULTICE", "PRECARIOUS_SHEARS" };
    private static int _logged;

    static void Postfix(RelicModel __instance, IRunState runState, ref bool __result) {
        try {
            if (!__result) return;
            if (!Disabled.Contains(__instance.Id.Entry)) return;
            if (!SealedDeckRun.IsActive(runState)) return;
            __result = false;
            if (System.Threading.Interlocked.Exchange(ref _logged, 1) == 0)
                TiLog.Info("[SlayTheStreamer2][sealed-neow] sealed run: Leafy Poultice and Precarious Shears disabled");
        } catch (Exception ex) { TiLog.Warn($"[SlayTheStreamer2][sealed-neow] relic disable postfix failed: {ex.Message}"); }
    }
}
