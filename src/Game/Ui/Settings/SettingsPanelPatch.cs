using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using SlayTheStreamer2.Ti.Internal;
using ChatModSettings = SlayTheStreamer2.Game.Bootstrap.ModSettings;

namespace SlayTheStreamer2.Game.Ui.Settings;

/// <summary>
/// Harmony postfix on NModInfoContainer.Fill that injects our settings panel
/// when the selected mod row is ours. Defensive cleanup of any prior injected
/// panel runs first (Fill is re-entrant — vanilla doesn't clear arbitrary children).
///
/// Save lifecycle:
///   - control change → SettingsSaveDebouncer.MarkDirtyAndRestart (500ms timer)
///   - timer fire → SettingsWriter.Write
///   - panel free → SettingsSaveDebouncer.FlushNow via _ExitTree (belt-and-braces)
///
/// The debouncer is attached as a child of the injected panel, so it shares
/// the panel's lifetime and gets freed automatically when the panel is replaced.
/// </summary>
[HarmonyPatch(typeof(NModInfoContainer), nameof(NModInfoContainer.Fill))]
internal static class SettingsPanelPatch {
    static void Postfix(NModInfoContainer __instance, Mod mod) {
        try {
            // 1. Defensive cleanup: remove any prior injected panel by name.
            var existing = __instance.GetNodeOrNull(ModConstants.SettingsPanelNodeName);
            if (existing != null) {
                existing.QueueFree();
            }

            // 2. Inject only when our mod is selected.
            if (mod.manifest?.id != ModConstants.ModId) return;

            var current = ChatModSettings.Current;
            if (current is null) {
                TiLog.Warn("[settings-ui] ModSettings.Current is null at panel build time; settings file missing or load failed");
                return;
            }

            // 3. Build the debouncer + panel.
            var debouncer = new SettingsSaveDebouncer();
            var panel = SettingsPanelBuilder.Build(current, debouncer);
            panel.AddChild(debouncer);

            __instance.AddChild(panel);
            TiLog.Info("[settings-ui] settings panel injected under NModInfoContainer");
        } catch (System.Exception ex) {
            TiLog.Error("[settings-ui] SettingsPanelPatch.Postfix threw — vanilla mod manager continues", ex);
        }
    }
}
