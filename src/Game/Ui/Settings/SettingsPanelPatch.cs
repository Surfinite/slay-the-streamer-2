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
/// is 666×901px).
///
/// When our mod is selected we shrink ModDescription to DescHeight px so the
/// author/version/description text remains visible, then place our scroll panel
/// immediately below it. When the user switches to a different mod we restore
/// ModDescription to its original size.
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
    // ModDescription occupies y=493–886 in modding_screen.tscn (393px total).
    // We keep the top DescHeight px for the description text, and give the rest
    // (plus a small padding gap) to the settings scroll panel.
    private const float DescTop    = 493f;
    private const float DescBottom = 886f;
    private const float DescHeight = 150f;   // visible description lines (~4–5)
    private const float Padding    = 8f;

    private const float PanelLeft  = 17f;
    private const float PanelRight = 635f;

    // y-top and height for our scroll container
    private static float PanelTop    => DescTop + DescHeight + Padding;
    private static float PanelHeight => DescBottom - PanelTop;

    // Original ModDescription size, saved the first time we shrink it so we
    // can restore it when the user selects a different mod.
    private static Vector2? _savedDescSize;

    static void Postfix(NModInfoContainer __instance, Mod mod) {
        try {
            // 1. Defensive cleanup: remove any prior injected scroll container by name.
            var existing = __instance.GetNodeOrNull(ModConstants.SettingsPanelNodeName);
            if (existing != null) {
                existing.QueueFree();
            }

            // 2. Always restore vanilla ModDescription to its original size/position
            //    (in case we shrunk it on a previous fill of our mod and the user
            //    has now selected a different mod).
            var descNode = __instance.GetNodeOrNull<RichTextLabel>("ModDescription");
            if (descNode != null && _savedDescSize.HasValue) {
                descNode.Size = _savedDescSize.Value;
            }

            // 3. Inject only when our mod is selected.
            if (mod.manifest?.id != ModConstants.ModId) return;

            var current = ChatModSettings.Current;
            if (current is null) {
                TiLog.Warn("[settings-ui] ModSettings.Current is null at panel build time; settings file missing or load failed");
                return;
            }

            // 4. Shrink ModDescription to make room for our settings panel below it.
            if (descNode != null) {
                if (!_savedDescSize.HasValue) {
                    _savedDescSize = descNode.Size;
                }
                descNode.Size = new Vector2(descNode.Size.X, DescHeight);
            }

            // 5. Build the settings content VBoxContainer (no scroll yet — just the inner widget tree).
            var debouncer = new SettingsSaveDebouncer();
            var content = SettingsPanelBuilder.Build(current, debouncer);
            content.AddChild(debouncer);

            // 6. Wrap in a ScrollContainer below the description region.
            var scroll = new ScrollContainer {
                Name = ModConstants.SettingsPanelNodeName,
                Position = new Vector2(PanelLeft, PanelTop),
                Size     = new Vector2(PanelRight - PanelLeft, PanelHeight),
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            };
            scroll.AddChild(content);

            __instance.AddChild(scroll);
            TiLog.Info($"[settings-ui] settings panel injected (scroll region y={PanelTop:F0}–{DescBottom}, {PanelRight - PanelLeft}px wide)");
        } catch (System.Exception ex) {
            TiLog.Error("[settings-ui] SettingsPanelPatch.Postfix threw — vanilla mod manager continues", ex);
        }
    }
}
