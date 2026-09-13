using System;
using Godot;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.Ui;

/// <summary>"Every card reward must be taken here." ABOVE the "Loot!" banner (the loot
/// column leaves no room below it), in the vote-title theme, positioned per frame from
/// the header label, never from GetGlobalRect in _Ready. The header is resolved lazily
/// via a provider delegate, not captured once at Attach time, because the caller may
/// run before vanilla assigns its own header field.</summary>
internal sealed partial class RewardsHeaderSubLabel : Control {
    private const string FontPath = "res://themes/kreon_bold_shared.tres";
    private const int FontSize = 29;
    private const float LineHeight = 44f;
    private static readonly Color TextColor = new(1f, 0.964706f, 0.886275f, 1f);

    private Func<Control?>? _headerProvider;
    private Func<bool>? _isVisible;
    private Label? _label;

    /// <param name="isVisible">Optional per-frame probe; false hides the line (the
    /// unskippable header goes away once every unskippable card reward is taken).</param>
    internal static void Attach(Node screen, Func<Control?> headerProvider, string text, Func<bool>? isVisible = null) {
        var sub = new RewardsHeaderSubLabel {
            Name = "SlayTheStreamerUnskippableHeader",
            MouseFilter = MouseFilterEnum.Ignore,
            AnchorLeft = 0, AnchorTop = 0, AnchorRight = 1, AnchorBottom = 1,
        };
        try {
            sub._headerProvider = headerProvider;
            sub._isVisible = isVisible;
            sub._label = new Label {
                Text = text, HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore,
                AnchorLeft = 0, AnchorTop = 0, AnchorRight = 0, AnchorBottom = 0,
            };
            var font = ResourceLoader.Load<Font>(FontPath);
            if (font is not null) sub._label.AddThemeFontOverride("font", font);
            sub._label.AddThemeColorOverride("font_color", TextColor);
            sub._label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.5f));
            sub._label.AddThemeConstantOverride("shadow_offset_x", 3);
            sub._label.AddThemeConstantOverride("shadow_offset_y", 2);
            sub._label.AddThemeFontSizeOverride("font_size", FontSize);
            sub.AddChild(sub._label);
            if (!GodotObject.IsInstanceValid(screen)) {
                TiLog.Warn("[SlayTheStreamer2][unskip] header sub-label attach: screen invalid");
            } else {
                screen.AddChild(sub);
            }
        } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][unskip] header sub-label attach failed", ex); }
    }

    public override void _Process(double delta) {
        try {
            if (_label is null) return;
            bool show = true;
            try { show = _isVisible?.Invoke() ?? true; } catch { /* probe must never break placement */ }
            if (Visible != show) Visible = show;
            if (!show) return;
            float cx, top;
            var header = _headerProvider?.Invoke();
            if (header is not null && GodotObject.IsInstanceValid(header)) {
                var pos = header.GlobalPosition; var size = header.Size * header.Scale;
                cx = pos.X + size.X * 0.5f; top = pos.Y - LineHeight - 4f;
            } else { cx = GetViewportRect().Size.X * 0.5f; top = 110f; }
            _label.OffsetLeft = cx - 400f; _label.OffsetRight = cx + 400f;
            _label.OffsetTop = top; _label.OffsetBottom = top + LineHeight;
        } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][unskip] header sub-label placement failed", ex); }
    }
}
