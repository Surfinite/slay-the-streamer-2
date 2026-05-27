using Godot;
using SlayTheStreamer2.Game.Bootstrap;
using SlayTheStreamer2.Game.DecisionVotes;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.Ui;

/// <summary>
/// Godot RichTextLabel showing the streamer's per-act card-skip budget. Mounted
/// as a child of the vanilla <c>NCardRewardSelectionScreen</c> so it appears
/// only while the streamer is viewing the choose-a-card screen — Godot's
/// natural scene-tree teardown frees the label when that screen closes.
///
/// <para>Position is polled per-frame from the vanilla Skip button's
/// <c>GlobalPosition + Size</c> so the label tracks the button across aspect
/// ratios (the button is center-anchored to the viewport, so its absolute Y
/// shifts when the window is resized — a fixed viewport-fraction anchor would
/// drift visually).</para>
///
/// <para>While a chat vote is in progress, <see cref="_Process"/> hides the
/// label so the vote popup has uncontested visual space.</para>
///
/// <para>Hidden when <c>cardSkipsPerAct == -1</c> (unlimited) or
/// <c>cardSkipsPerAct == 0</c> (strict).</para>
/// </summary>
public partial class CardSkipCounterLabel : RichTextLabel {
    // Vanilla default body font.
    private const string FontPath = "res://themes/kreon_regular_shared.tres";

    // ---- Easy-to-tweak positioning + sizing knobs ----
    // Gap (px) between the Skip button's bottom edge and the label's vertical
    // center. Positive = label sits below the button; negative = above.
    private const float GapBelowSkipButton = -80f;
    private const int FontSize = 26;
    // Half-extents of the label's layout box around the centered target point.
    // The label uses [center] BBCode to horizontally center the text within
    // this box — the box just defines an area wide enough for the longest
    // expected text ("Streamer has N card skips remaining this act").
    private const float LabelHalfWidth = 320f;
    private const float LabelHalfHeight = 28f;

    // Fallback Y anchor (viewport-relative fraction) used only if the Skip
    // button can't be resolved at attach time. Polled positioning is the
    // primary path.
    private const float FallbackVerticalAnchor = 0.83f;

    private Control? _skipButton;

    internal void UpdateText(SkipBudgetSnapshot snap) {
        if (snap.LimitThisAct <= 0) {
            Visible = false;
            return;
        }
        Visible = true;
        string noun = snap.RemainingThisAct == 1 ? "card skip" : "card skips";
        string streamerName = ModSettings.GetStreamerDisplayName();
        // [center] BBCode horizontally centers the text inside the layout box;
        // _Process positions the box itself relative to the Skip button.
        Text = $"[center]{streamerName} has {snap.RemainingThisAct} {noun} remaining this act[/center]";
    }

    public override void _Process(double delta) {
        // Once a card vote opens, the vote popup owns the visual space — hide the
        // counter so it doesn't compete.
        if (CardRewardVotePatch.VoteInProgress) {
            if (Visible) Visible = false;
            return;
        }
        if (!Visible) Visible = true;

        // Poll the Skip button each frame so the label tracks any aspect-ratio
        // change or button-position tween. The button is center-anchored, so its
        // absolute Y is not stable across viewport sizes.
        if (_skipButton is not null && GodotObject.IsInstanceValid(_skipButton)) {
            var pos = _skipButton.GlobalPosition;
            var size = _skipButton.Size * _skipButton.Scale;
            float centerX = pos.X + size.X * 0.5f;
            float centerY = pos.Y + size.Y + GapBelowSkipButton;
            PlaceLabelAt(centerX, centerY);
        }
    }

    /// <summary>
    /// Attaches the label as a child of <paramref name="parent"/> so Godot's
    /// scene-tree lifecycle frees it automatically when the parent screen
    /// closes. <paramref name="skipButton"/> is polled per-frame in
    /// <see cref="_Process"/> for positioning; pass null to use the
    /// fixed-viewport-Y fallback.
    /// </summary>
    public static CardSkipCounterLabel AttachTo(Node parent, Control? skipButton) {
        var label = new CardSkipCounterLabel { Name = "CardSkipCounterLabel" };
        label.BbcodeEnabled = true;
        label.FitContent = true;
        label._skipButton = skipButton;
        ApplyTheme(label);
        // Pass clicks through to the underlying scene so the label can't accidentally
        // swallow input on the parent's interactive controls.
        label.MouseFilter = Control.MouseFilterEnum.Ignore;

        if (skipButton is null) {
            // Fallback layout — full-width band at a fixed viewport-fraction Y.
            // Triggered if the patch couldn't resolve the Skip button (future
            // MegaCrit rename of UI/RewardAlternatives, etc.).
            TiLog.Warn("[SlayTheStreamer2][card-skip-label] no skip button reference; falling back to fixed viewport-Y placement");
            label.AnchorLeft = 0f;
            label.AnchorRight = 1f;
            label.AnchorTop = FallbackVerticalAnchor;
            label.AnchorBottom = FallbackVerticalAnchor;
        }
        // If skipButton is non-null, _Process will overwrite anchors+offsets
        // on its first tick. Initial values left at defaults (zero) — invisible
        // for at most one frame before _Process places the label correctly.

        parent.AddChild(label);
        TiLog.Info("[SlayTheStreamer2][card-skip-label] attached under choose-a-card screen");
        return label;
    }

    /// <summary>
    /// Positions the label so its center lands at viewport-space
    /// (<paramref name="centerX"/>, <paramref name="centerY"/>) using a
    /// fixed-size layout box. Mirrors the helper in CardRewardVotePopup.
    /// </summary>
    private void PlaceLabelAt(float centerX, float centerY) {
        AnchorLeft = 0; AnchorTop = 0; AnchorRight = 0; AnchorBottom = 0;
        OffsetLeft = centerX - LabelHalfWidth;
        OffsetRight = centerX + LabelHalfWidth;
        OffsetTop = centerY - LabelHalfHeight;
        OffsetBottom = centerY + LabelHalfHeight;
    }

    private static void ApplyTheme(RichTextLabel label) {
        var font = ResourceLoader.Load<Font>(FontPath);
        if (font is not null) {
            label.AddThemeFontOverride("normal_font", font);
            label.AddThemeFontOverride("bold_font", font);
            label.AddThemeFontOverride("italics_font", font);
            label.AddThemeFontOverride("bold_italics_font", font);
        }
        label.AddThemeFontSizeOverride("normal_font_size", FontSize);
        label.AddThemeFontSizeOverride("bold_font_size", FontSize);
        label.AddThemeFontSizeOverride("italics_font_size", FontSize);
        label.AddThemeFontSizeOverride("bold_italics_font_size", FontSize);
    }
}
