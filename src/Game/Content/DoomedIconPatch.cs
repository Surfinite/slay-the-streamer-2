using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.Content;

/// <summary>Ruling 8: the card badge uses EnchantmentModel.IconPath (non-virtual getter,
/// private _iconPath cache, missing-glyph fallback). For StreamerDoomed return the Doom
/// power's own 256 px icon; the badge TextureRect scales mixed sizes already (vanilla
/// badges are 64 and 128 px). Falls back to vanilla's answer if the resource is absent.</summary>
[HarmonyPatch(typeof(EnchantmentModel), nameof(EnchantmentModel.IconPath), MethodType.Getter)]
internal static class DoomedIconPatch {
    private const string DoomIcon = "res://images/powers/doom_power.png";
    private static bool? _exists;

    static void Postfix(EnchantmentModel __instance, ref string __result) {
        try {
            if (__instance is not StreamerDoomed) return;
            _exists ??= ResourceLoader.Exists(DoomIcon);
            if (_exists == true) __result = DoomIcon;
        } catch (Exception ex) { TiLog.Warn($"[SlayTheStreamer2][sealed-neow] doomed icon override failed: {ex.Message}"); }
    }
}
