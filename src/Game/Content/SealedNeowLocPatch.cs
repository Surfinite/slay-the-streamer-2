using System;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.Content;

/// <summary>Spec section 7.2 loc. Provide: the Doomed keys do not exist in any table, so
/// prefixes on GetRawText, HasEntry and GetLocString answer them (BaseLib's
/// MissingLocPatch does the same for its keys; prefixes compose). Replace: the two
/// Talisman keys read the rework text only while the run is sealed (postfix on
/// GetRawText), so the compendium and normal runs show vanilla text.</summary>
internal static class SealedNeowLocPatch {
    /// <summary>Harmony's injected-field parameter takes the field's LITERAL name after
    /// three underscores; LocTable's field is `_name`, so the parameter is `____name`
    /// (four underscores total). See the CLAUDE.md landmine.</summary>
    private static bool FieldExists() => AccessTools.Field(typeof(LocTable), "_name") is not null;

    [HarmonyPatch(typeof(LocTable), nameof(LocTable.GetRawText))]
    internal static class RawTextPatch {
        static bool Prepare(MethodBase? original) {
            if (original is not null) return true;
            if (FieldExists()) return true;
            TiLog.Error("[SlayTheStreamer2][sealed-neow] LocTable._name not found; RawTextPatch will not register");
            return false;
        }

        static bool Prefix(string key, string ____name, ref string __result) {
            try {
                if (SealedNeowLoc.TryProvide(____name, key, out var text)) { __result = text; return false; }
            } catch (Exception ex) { TiLog.Warn($"[SlayTheStreamer2][sealed-neow] loc provide failed: {ex.Message}"); }
            return true;
        }

        // High priority: this replace-before-append postfix must run before
        // LocTextPatch's remove-one append postfix (higher priority runs earlier for
        // postfixes), so an appended Sabotage suffix lands after the Talisman rework
        // text rather than after vanilla's original wording.
        [HarmonyPriority(Priority.High)]
        static void Postfix(string key, string ____name, ref string __result) {
            try {
                if (____name != "relics") return;
                if (SealedNeowLoc.TryReplace(____name, key, out var text) && SealedDeckRun.IsActive()) __result = text;
            } catch (Exception ex) { TiLog.Warn($"[SlayTheStreamer2][sealed-neow] loc replace failed: {ex.Message}"); }
        }
    }

    [HarmonyPatch(typeof(LocTable), nameof(LocTable.HasEntry))]
    internal static class HasEntryPatch {
        static bool Prefix(string key, string ____name, ref bool __result) {
            try {
                if (SealedNeowLoc.TryProvide(____name, key, out _)) { __result = true; return false; }
            } catch (Exception ex) { TiLog.Warn($"[SlayTheStreamer2][sealed-neow] loc HasEntry provide failed: {ex.Message}"); }
            return true;
        }
    }

    [HarmonyPatch(typeof(LocTable), nameof(LocTable.GetLocString))]
    internal static class GetLocStringPatch {
        static bool Prefix(string key, string ____name, ref LocString __result) {
            try {
                if (SealedNeowLoc.TryProvide(____name, key, out _)) { __result = new LocString(____name, key); return false; }
            } catch (Exception ex) { TiLog.Warn($"[SlayTheStreamer2][sealed-neow] loc GetLocString provide failed: {ex.Message}"); }
            return true;
        }
    }
}
