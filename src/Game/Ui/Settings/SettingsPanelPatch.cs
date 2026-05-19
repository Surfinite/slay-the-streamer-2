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
/// When our mod is selected we:
///   1. Hide ModImage (no mod_image.png shipped — it occupies 380px for nothing).
///   2. Move ModDescription up to y=120 (just below title) and shrink to DescHeight.
///   3. Strip the blank line vanilla inserts between Version and the description text.
///   4. Place our settings panel immediately below the description.
/// When the user switches to a different mod we restore all three to vanilla values.
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
    // Vanilla ModDescription occupies y=493–886 (393px tall).
    private const float DescBottom = 886f;

    // When our mod is selected we move ModDescription up to just below the title.
    private const float DescTopOurs  = 120f;  // just below ModTitle (y=18–110)
    private const float DescHeight   = 130f;  // ~5 lines: Author + Version + 2-3 desc lines
    private const float Padding      = 8f;

    private const float PanelLeft  = 17f;
    private const float PanelRight = 635f;

    // y-top and height for our scroll container
    private static float PanelTop    => DescTopOurs + DescHeight + Padding;
    private static float PanelHeight => DescBottom - PanelTop;

    // Saved vanilla values — set on first injection, used on restore.
    private static Vector2? _savedDescSize;
    private static Vector2? _savedDescPosition;
    private static bool     _savedImageVisible = true;

    static void Postfix(NModInfoContainer __instance, Mod mod) {
        try {
            // 1. Defensive cleanup: remove any prior injected scroll container by name.
            var existing = __instance.GetNodeOrNull(ModConstants.SettingsPanelNodeName);
            if (existing != null) {
                existing.QueueFree();
            }

            // 2. Always restore vanilla ModDescription and ModImage to their original
            //    values in case we mutated them on a previous fill of our mod.
            var descNode  = __instance.GetNodeOrNull<RichTextLabel>("ModDescription");
            var imageNode = __instance.GetNodeOrNull<Control>("ModImage");

            if (descNode != null) {
                if (_savedDescSize.HasValue)     descNode.Size     = _savedDescSize.Value;
                if (_savedDescPosition.HasValue) descNode.Position = _savedDescPosition.Value;
            }
            if (imageNode != null && _savedImageVisible) {
                imageNode.Visible = _savedImageVisible;
            }

            // 3. Inject only when our mod is selected.
            if (mod.manifest?.id != ModConstants.ModId) return;

            var current = ChatModSettings.Current;
            if (current is null) {
                TiLog.Warn("[settings-ui] ModSettings.Current is null at panel build time; settings file missing or load failed");
                return;
            }

            // 4. Hide ModImage — we ship no mod_image.png so it just occupies 380px.
            if (imageNode != null) {
                _savedImageVisible   = imageNode.Visible;
                imageNode.Visible    = false;
            }

            // 5. Move ModDescription up into the reclaimed ModImage space; shrink it.
            if (descNode != null) {
                if (!_savedDescSize.HasValue)     _savedDescSize     = descNode.Size;
                if (!_savedDescPosition.HasValue) _savedDescPosition = descNode.Position;

                descNode.Position = new Vector2(descNode.Position.X, DescTopOurs);
                descNode.Size     = new Vector2(descNode.Size.X, DescHeight);

                // Strip the blank line vanilla inserts between Version and the description
                // sentence (StringBuilder.AppendLine() at NModInfoContainer.cs:76 adds \n
                // after Version, then the description adds another \n, producing "\n\n").
                // We collapse any double-newline to a single newline.
                var text = descNode.Text;
                while (text.Contains("\n\n")) text = text.Replace("\n\n", "\n");
                descNode.Text = text;
            }

            // 6. Build the settings content VBoxContainer (inner widget tree).
            var debouncer = new SettingsSaveDebouncer();
            var content = SettingsPanelBuilder.Build(current, debouncer);
            content.AddChild(debouncer);

            // 7. Wrap in a ScrollContainer below the (repositioned) description.
            var scroll = new ScrollContainer {
                Name = ModConstants.SettingsPanelNodeName,
                Position = new Vector2(PanelLeft, PanelTop),
                Size     = new Vector2(PanelRight - PanelLeft, PanelHeight),
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                VerticalScrollMode   = ScrollContainer.ScrollMode.Auto,
            };
            scroll.AddChild(content);

            __instance.AddChild(scroll);
            TiLog.Info($"[settings-ui] settings panel injected — desc y={DescTopOurs}–{DescTopOurs + DescHeight}, panel y={PanelTop:F0}–{DescBottom} ({PanelHeight:F0}px tall, {PanelRight - PanelLeft}px wide)");
        } catch (System.Exception ex) {
            TiLog.Error("[settings-ui] SettingsPanelPatch.Postfix threw — vanilla mod manager continues", ex);
        }
    }
}
