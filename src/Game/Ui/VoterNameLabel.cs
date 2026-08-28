using System;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Combat;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.Ui;

/// <summary>
/// voter-names: the on-screen username under an enemy's intent icons. Sibling
/// of NCreature.IntentContainer (vanilla culls the container's extra children
/// every turn — never parent inside it). Bobs with vanilla's exact intent
/// formula and phase so it moves in lockstep with the first icon, and mirrors
/// the container's Modulate/Visible each frame so it hides during attack
/// animations, fast-mode fades, and combat teardown for free.
/// </summary>
internal sealed partial class VoterNameLabel : Label {
    private const string KreonPath = "res://themes/kreon_regular_glyph_space_one.tres";
    private const int FontSize = 24;
    private const int MinFontSize = 12;

    private Control? _container;      // the creature's IntentContainer
    private float _bobPhase;
    private Vector2 _basePosition;    // set by VoterNamesPatch on every UpdateBounds
    private Font? _font;              // resolved font used for shrink-to-fit measurement

    public static VoterNameLabel? TryCreate(string decoratedName, NCreature creature) {
        try {
            var label = new VoterNameLabel {
                Name = "VoterNameLabel",
                Text = decoratedName,
                HorizontalAlignment = HorizontalAlignment.Center,
                MouseFilter = MouseFilterEnum.Ignore,
                _container = creature.IntentContainer,
                _bobPhase = (float)creature.GetHashCode() * 0.01f,   // == first intent icon's phase
            };
            label.AddThemeColorOverride("font_color", new Color(1f, 0.964706f, 0.886275f));
            label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.25098f));
            label.AddThemeConstantOverride("shadow_offset_x", 2);
            label.AddThemeConstantOverride("shadow_offset_y", 1);
            label.AddThemeFontSizeOverride("font_size", FontSize);
            if (ResourceLoader.Exists(KreonPath) && ResourceLoader.Load(KreonPath) is Font kreon) {
                label.AddThemeFontOverride("font", kreon);
                label._font = kreon;
            }
            return label;
        } catch (Exception ex) {
            TiLog.Warn($"[SlayTheStreamer2][voter-names] label create failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Called by the UpdateBounds postfix after vanilla lays the container out.</summary>
    public void SetBasePosition(Vector2 basePosition) => _basePosition = basePosition;

    /// <summary>
    /// Spec §5 shrink-to-fit: steps the font size down from <see cref="FontSize"/>
    /// toward <see cref="MinFontSize"/> until the decorated name's measured width
    /// fits maxWidth (the intent-group width the patch just assigned to Size.X).
    /// </summary>
    public void FitToWidth(float maxWidth) {
        try {
            var font = _font ?? GetThemeDefaultFont();
            if (font is null) return;

            int size = FontSize;
            while (size > MinFontSize &&
                   font.GetStringSize(Text, HorizontalAlignment.Left, -1, size).X > maxWidth) {
                size--;
            }
            AddThemeFontSizeOverride("font_size", size);
        } catch (Exception ex) {
            TiLog.Warn($"[SlayTheStreamer2][voter-names] shrink-to-fit failed: {ex.Message}");
        }
    }

    public override void _Process(double delta) {
        var container = _container;
        if (container is null || !GodotObject.IsInstanceValid(container)) return;

        // Mirror the container's presentation state (attack-hide, fast-mode fade,
        // debug intent toggle all modulate/hide the container).
        Visible = container.Visible;
        Modulate = container.Modulate;

        // Vanilla NIntent bob, verbatim constants.
        Position = _basePosition + Vector2.Up *
            (Mathf.Sin((float)Time.GetTicksMsec() * 0.001f * (float)Math.PI + _bobPhase) * 10f + 8f);
    }
}
