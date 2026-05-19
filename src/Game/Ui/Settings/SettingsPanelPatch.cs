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
/// Layout: NModInfoContainer is a plain Panel with absolute-positioned children
/// (ModTitle y=18–110, ModImage y=104–484, ModDescription y=493–886, container
/// is 666×901px). We absolutely position our scroll+panel to exactly overlap the
/// ModDescription region (y=493, height=393, x=17, width=618) and hide the
/// vanilla description node so there is no overlap.
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
    // Pixel bounds matching ModDescription in modding_screen.tscn (layout_mode=0).
    // ModDescription: offset_left=25, offset_top=493, offset_right=635, offset_bottom=886.
    // We use slightly tighter left/right (17/635) to align with ModTitle's left edge.
    private const float PanelLeft   = 17f;
    private const float PanelTop    = 493f;
    private const float PanelRight  = 635f;
    private const float PanelBottom = 886f;

    static void Postfix(NModInfoContainer __instance, Mod mod) {
        try {
            // 1. Defensive cleanup: remove any prior injected scroll container by name.
            var existing = __instance.GetNodeOrNull(ModConstants.SettingsPanelNodeName);
            if (existing != null) {
                existing.QueueFree();
            }

            // 2a. Always restore vanilla description visibility (in case we toggled it
            //     on a previous fill of our mod and the user switched to another mod).
            var descNode = __instance.GetNodeOrNull<RichTextLabel>("ModDescription");
            if (descNode != null) {
                descNode.Visible = true;
            }

            // 2b. Inject only when our mod is selected.
            if (mod.manifest?.id != ModConstants.ModId) return;

            var current = ChatModSettings.Current;
            if (current is null) {
                TiLog.Warn("[settings-ui] ModSettings.Current is null at panel build time; settings file missing or load failed");
                return;
            }

            // 3. Hide the vanilla description — our panel replaces it in that region.
            if (descNode != null) {
                descNode.Visible = false;
            }

            // 4. Build the settings content VBoxContainer (no scroll yet — just the inner widget tree).
            var debouncer = new SettingsSaveDebouncer();
            var content = SettingsPanelBuilder.Build(current, debouncer);
            content.AddChild(debouncer);

            // 5. Wrap in a ScrollContainer so the content can exceed the available height.
            var scroll = new ScrollContainer {
                Name = ModConstants.SettingsPanelNodeName,
                // Absolute position inside the Panel, matching ModDescription region.
                Position = new Vector2(PanelLeft, PanelTop),
                Size     = new Vector2(PanelRight - PanelLeft, PanelBottom - PanelTop),
                // Let horizontal scroll be off — content should fit the width.
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            };
            scroll.AddChild(content);

            __instance.AddChild(scroll);
            TiLog.Info($"[settings-ui] settings panel injected (scroll region y={PanelTop}–{PanelBottom}, {PanelRight - PanelLeft}px wide)");
        } catch (System.Exception ex) {
            TiLog.Error("[settings-ui] SettingsPanelPatch.Postfix threw — vanilla mod manager continues", ex);
        }
    }
}
