using System;
using Godot;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.Ui;

/// <summary>A BBCode status line in the vote countdown's slot (just above the bottom edge
/// of the vanilla "Choose a Card" banner), polled every frame (assign-on-change) from a
/// text provider so it disappears on the first frame of a vote and the countdown takes
/// its place. Parented under the screen so Godot frees it with the screen. Same Kreon
/// face as the vote title, smaller.</summary>
internal sealed partial class RemovalStatusLine : Control {
    private const string FontPath = "res://themes/kreon_bold_shared.tres";
    private const int FontSize = 28;
    // Mirrors CardRewardVotePopup.TimerGapBelowBanner: the first line is centred where the
    // countdown sits during a vote, so the two never overlap the cards.
    private const float GapBelowBanner = -20f;
    private const float FirstLineHalfHeight = 18f;
    private const float BoxHeight = 90f;            // room for the second (reroll) line
    private const float HalfWidth = 520f;
    private const float FallbackTop = 120f;
    private static readonly Color TextColor = new(1f, 0.964706f, 0.886275f, 1f);

    private RichTextLabel? _label;
    private Control? _banner;
    private Func<string>? _text;
    private string _last = "";
    private bool _refreshedOnce;

    internal static RemovalStatusLine Attach(Node parent, Control? bannerAnchor, Func<string> textProvider) {
        var line = new RemovalStatusLine {
            Name = "SlayTheStreamerRemovalStatusLine",
            MouseFilter = MouseFilterEnum.Ignore,
            AnchorLeft = 0, AnchorTop = 0, AnchorRight = 1, AnchorBottom = 1,
        };
        try {
            line._banner = bannerAnchor;
            line._text = textProvider;
            line._label = new RichTextLabel {
                BbcodeEnabled = true,
                FitContent = true,
                ScrollActive = false,
                MouseFilter = MouseFilterEnum.Ignore,
                AnchorLeft = 0, AnchorTop = 0, AnchorRight = 0, AnchorBottom = 0,
            };
            var font = ResourceLoader.Load<Font>(FontPath);
            if (font is not null) {
                line._label.AddThemeFontOverride("normal_font", font);
                line._label.AddThemeFontOverride("bold_font", font);
            }
            line._label.AddThemeColorOverride("default_color", TextColor);
            line._label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.5f));
            line._label.AddThemeConstantOverride("shadow_offset_x", 2);
            line._label.AddThemeConstantOverride("shadow_offset_y", 2);
            line._label.AddThemeFontSizeOverride("normal_font_size", FontSize);
            line._label.AddThemeFontSizeOverride("bold_font_size", FontSize);
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
            // Per frame: the provider is a few dictionary/field reads, and a vote must hide
            // this line on the frame it opens (the countdown lands in the same slot).
            string text = _text?.Invoke() ?? "";
            if (!_refreshedOnce || text != _last) {
                _refreshedOnce = true;
                _last = text;
                _label.Text = text.Length > 0 ? $"[center]{text}[/center]" : "";
            }
            Visible = _last.Length > 0;
            float cx, centerY;
            if (_banner is not null && GodotObject.IsInstanceValid(_banner)) {
                var pos = _banner.GlobalPosition; var size = _banner.Size * _banner.Scale;
                cx = pos.X + size.X * 0.5f; centerY = pos.Y + size.Y + GapBelowBanner;
            } else { cx = GetViewportRect().Size.X * 0.5f; centerY = FallbackTop; }
            _label.OffsetLeft = cx - HalfWidth; _label.OffsetRight = cx + HalfWidth;
            _label.OffsetTop = centerY - FirstLineHalfHeight; _label.OffsetBottom = centerY - FirstLineHalfHeight + BoxHeight;
        } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][remove-one] status line placement failed", ex); }
    }
}
