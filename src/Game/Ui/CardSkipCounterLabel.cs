using Godot;
using SlayTheStreamer2.Game.DecisionVotes;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.Ui;

/// <summary>
/// Godot RichTextLabel showing the streamer's per-act card-skip budget.
/// Parented under NRewardsScreen near the proceed button. Hidden when
/// cardSkipsPerAct == -1 (unlimited; nothing to display).
/// </summary>
public partial class CardSkipCounterLabel : RichTextLabel {
    private const int FontSize = 18;
    private static readonly Color DefaultColor = new(0.95f, 0.85f, 0.5f);

    public override void _Ready() {
        BbcodeEnabled = true;
        FitContent = true;
        ScrollActive = false;
        AddThemeFontSizeOverride("normal_font_size", FontSize);
        AddThemeColorOverride("default_color", DefaultColor);
    }

    internal void UpdateText(SkipBudgetSnapshot snap) {
        if (snap.LimitThisAct < 0) {
            Visible = false;
            return;
        }
        Visible = true;
        Text = $"Card skips: {snap.RemainingThisAct}/{snap.LimitThisAct} act";
    }

    /// <summary>
    /// Attach a new label as a child of `parent`, positioned near `proceedButton`
    /// (offset above-and-left). If `proceedButton` is null, falls back to the
    /// parent's top-right with a Warn log.
    /// </summary>
    public static CardSkipCounterLabel AttachTo(Node parent, Control? proceedButton) {
        var label = new CardSkipCounterLabel { Name = "CardSkipCounterLabel" };
        parent.AddChild(label);
        if (proceedButton is not null && GodotObject.IsInstanceValid(proceedButton)) {
            // Anchor above-and-left of the proceed button.
            label.SetAnchorsAndOffsetsPreset(LayoutPreset.TopLeft);
            label.Position = proceedButton.Position + new Vector2(0, -40);
            label.Size = new Vector2(300, 30);
        } else {
            TiLog.Warn("[SlayTheStreamer2][card-skip-label] _proceedButton not found; falling back to top-right of parent");
            label.SetAnchorsAndOffsetsPreset(LayoutPreset.TopRight);
            label.Position = new Vector2(-310, 10);
            label.Size = new Vector2(300, 30);
        }
        return label;
    }
}
