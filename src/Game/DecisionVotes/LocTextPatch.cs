// src/Game/DecisionVotes/LocTextPatch.cs
using System;
using System.IO;
using System.Threading;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>Spec section 6.1: read-time text append. Every loc render goes
/// LocString.GetFormattedText -> LocManager.SmartFormat -> LocString.GetRawText ->
/// LocTable.GetRawText, so a postfix here covers vanilla files, modded loc files and
/// BaseLib's post-load injection alike, and survives language switches. Live check on
/// RewardAuthority.RulesActive, so flipping the checkbox hides the text at once.</summary>
[HarmonyPatch(typeof(LocTable), nameof(LocTable.GetRawText))]
internal static class LocTextPatch {
    private static int _warned;

    internal static RelicTextRegistry Registry =>
        RelicTextRegistry.Instance ??= new RelicTextRegistry(Path.Combine(OS.GetUserDataDir(), "slay_the_streamer_2.learned-relics.json"));

    static void Postfix(string key, string ___name, ref string __result) {
        try {
            if (!RewardAuthority.RulesActive) return;
            if (___name is not ("relics" or "events")) return;
            var suffix = AuthorityLoc.SuffixFor(___name, key, Registry);
            if (suffix is null) return;
            __result = AuthorityLoc.Append(__result, suffix);
        } catch (Exception ex) {
            if (Interlocked.CompareExchange(ref _warned, 1, 0) == 0) TiLog.Warn($"[SlayTheStreamer2][card-scope] loc text append failed: {ex.Message}");
        }
    }
}
