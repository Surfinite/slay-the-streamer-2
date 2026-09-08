using System;
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
    [HarmonyPatch(typeof(LocTable), nameof(LocTable.GetRawText))]
    internal static class RawTextPatch {
        static bool Prefix(string key, string ___name, ref string __result) {
            try {
                if (SealedNeowLoc.TryProvide(___name, key, out var text)) { __result = text; return false; }
            } catch (Exception ex) { TiLog.Warn($"[SlayTheStreamer2][sealed-neow] loc provide failed: {ex.Message}"); }
            return true;
        }

        static void Postfix(string key, string ___name, ref string __result) {
            try {
                if (___name != "relics") return;
                if (SealedNeowLoc.TryReplace(___name, key, out var text) && SealedDeckRun.IsActive) __result = text;
            } catch (Exception ex) { TiLog.Warn($"[SlayTheStreamer2][sealed-neow] loc replace failed: {ex.Message}"); }
        }
    }

    [HarmonyPatch(typeof(LocTable), nameof(LocTable.HasEntry))]
    internal static class HasEntryPatch {
        static bool Prefix(string key, string ___name, ref bool __result) {
            try {
                if (SealedNeowLoc.TryProvide(___name, key, out _)) { __result = true; return false; }
            } catch (Exception ex) { TiLog.Warn($"[SlayTheStreamer2][sealed-neow] loc HasEntry provide failed: {ex.Message}"); }
            return true;
        }
    }

    [HarmonyPatch(typeof(LocTable), nameof(LocTable.GetLocString))]
    internal static class GetLocStringPatch {
        static bool Prefix(string key, string ___name, ref LocString __result) {
            try {
                if (SealedNeowLoc.TryProvide(___name, key, out _)) { __result = new LocString(___name, key); return false; }
            } catch (Exception ex) { TiLog.Warn($"[SlayTheStreamer2][sealed-neow] loc GetLocString provide failed: {ex.Message}"); }
            return true;
        }
    }
}
