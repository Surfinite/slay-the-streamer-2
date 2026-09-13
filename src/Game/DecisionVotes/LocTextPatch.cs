// src/Game/DecisionVotes/LocTextPatch.cs
using System;
using System.IO;
using System.Reflection;
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

    // Harmony's injected-field parameter takes the field's LITERAL name after three
    // underscores; LocTable's field is `_name`, so the parameter is `____name` (four
    // underscores total). See the CLAUDE.md landmine.
    static bool Prepare(MethodBase? original) {
        if (original is not null) return true;
        if (AccessTools.Field(typeof(LocTable), "_name") is not null) return true;
        TiLog.Error("[SlayTheStreamer2][card-scope] LocTable._name not found; LocTextPatch will not register");
        return false;
    }

    static void Postfix(string key, string ____name, ref string __result) {
        try {
            if (!RewardAuthority.RulesActive) return;
            if (____name is not ("relics" or "events")) return;
            var suffix = AuthorityLoc.SuffixFor(____name, key, Registry, RewardAuthority.Mode);
            if (suffix is null) return;
            __result = AuthorityLoc.Append(__result, suffix);
        } catch (Exception ex) {
            if (Interlocked.CompareExchange(ref _warned, 1, 0) == 0) TiLog.Warn($"[SlayTheStreamer2][card-scope] loc text append failed: {ex.Message}");
        }
    }
}
