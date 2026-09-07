using System;
using Godot;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.Ui;

/// <summary>A one-line status label under the vanilla "Choose a Card" banner, polled
/// per frame (assign-on-change) from a text provider. Parented under the screen so
/// Godot frees it with the screen. Same Kreon face as the vote title, smaller.</summary>
internal sealed partial class RemovalStatusLine : Control {
    private const string FontPath = "res://themes/kreon_bold_shared.tres";
    private const int FontSize = 28;
    private const float GapBelowBanner = 70f;
    private const float FallbackTop = 260f;
    private static readonly Color TextColor = new(1f, 0.964706f, 0.886275f, 1f);

    private Label? _label;
    private Control? _banner;
    private Func<string>? _text;
    private string _last = "";

    internal static RemovalStatusLine Attach(Node parent, Control? bannerAnchor, Func<string> textProvider) {
        var line = new RemovalStatusLine {
            Name = "SlayTheStreamerRemovalStatusLine",
            MouseFilter = MouseFilterEnum.Ignore,
            AnchorLeft = 0, AnchorTop = 0, AnchorRight = 1, AnchorBottom = 1,
        };
        try {
            line._banner = bannerAnchor;
            line._text = textProvider;
            line._label = new Label {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MouseFilter = MouseFilterEnum.Ignore,
                AnchorLeft = 0, AnchorTop = 0, AnchorRight = 0, AnchorBottom = 0,
            };
            var font = ResourceLoader.Load<Font>(FontPath);
            if (font is not null) line._label.AddThemeFontOverride("font", font);
            line._label.AddThemeColorOverride("font_color", TextColor);
            line._label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.5f));
            line._label.AddThemeConstantOverride("shadow_offset_x", 2);
            line._label.AddThemeConstantOverride("shadow_offset_y", 2);
            line._label.AddThemeFontSizeOverride("font_size", FontSize);
            line.AddChild(line._label);
            if (!GodotObject.IsInstanceValid(parent)) {
                TiLog.Warn("[SlayTheStreamer2][remove-one] status line attach: parent invalid");
            } else {
                parent.AddChild(line);
            }
        } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][remove-one] status line attach failed", ex); }
        return line;
    }

    internal void Detach() {
        if (GodotObject.IsInstanceValid(this) && !IsQueuedForDeletion()) QueueFree();
    }

    public override void _Process(double delta) {
        try {
            if (_label is null) return;
            string text = _text?.Invoke() ?? "";
            if (text != _last) { _last = text; _label.Text = text; }
            Visible = text.Length > 0;
            float cx, top;
            if (_banner is not null && GodotObject.IsInstanceValid(_banner)) {
                var pos = _banner.GlobalPosition; var size = _banner.Size * _banner.Scale;
                cx = pos.X + size.X * 0.5f; top = pos.Y + size.Y + GapBelowBanner;
            } else { cx = GetViewportRect().Size.X * 0.5f; top = FallbackTop; }
            _label.OffsetLeft = cx - 520f; _label.OffsetRight = cx + 520f;
            _label.OffsetTop = top; _label.OffsetBottom = top + 44f;
        } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][remove-one] status line placement failed", ex); }
    }
}
