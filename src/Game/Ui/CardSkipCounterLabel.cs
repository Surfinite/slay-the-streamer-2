using Godot;
using SlayTheStreamer2.Game.DecisionVotes;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.Ui;

/// <summary>
/// Godot RichTextLabel showing the streamer's per-act card-skip budget.
/// Parented under SceneTree.Root (z-order proven by B.1's VoteTallyLabel) and
/// positioned in screen-space near the proceed button via GlobalPosition.
/// Hidden when cardSkipsPerAct == -1 (unlimited; nothing to display).
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
    /// Attach a new label under SceneTree.Root and place it near `proceedButton` in
    /// screen-space. Root-parenting matches B.1's VoteTallyLabel pattern (proven
    /// z-order; unaffected by sub-screens overlaying NRewardsScreen).
    /// `parent` is used only as a SceneTree handle (`parent.GetTree().Root`); ownership
    /// is transferred to root, so cleanup MUST be done by the caller via
    /// `_activeLabel = null` (the AfterOverlayClosed postfix in CardRewardSkipGatePatch).
    /// </summary>
    public static CardSkipCounterLabel AttachTo(Node parent, Control? proceedButton) {
        var label = new CardSkipCounterLabel { Name = "CardSkipCounterLabel" };
        var root = parent.GetTree()?.Root;
        if (root is null) {
            TiLog.Error("[SlayTheStreamer2][card-skip-label] could not resolve SceneTree.Root; falling back to direct AddChild");
            parent.AddChild(label);
        } else {
            root.AddChild(label);
        }

        // Position in screen-space. Use GlobalPosition (not Position — which is
        // anchor-relative and can resolve to off-screen coordinates with
        // LayoutPreset.TopLeft).
        if (proceedButton is not null && GodotObject.IsInstanceValid(proceedButton)) {
            var btnGlobal = proceedButton.GlobalPosition;
            // Place 50px above the top of the proceed button, left-aligned with it.
            label.GlobalPosition = btnGlobal + new Vector2(0, -50);
            label.Size = new Vector2(360, 36);
            TiLog.Info($"[SlayTheStreamer2][card-skip-label] attached at GlobalPosition={label.GlobalPosition} (proceedButton GlobalPosition={btnGlobal})");
        } else {
            TiLog.Warn("[SlayTheStreamer2][card-skip-label] _proceedButton not found; falling back to viewport-relative top-right");
            // Top-right of viewport (vanilla 1920x1080 letterboxed; root viewport size is reliable).
            var viewportSize = (root ?? parent).GetViewport().GetVisibleRect().Size;
            label.GlobalPosition = new Vector2(viewportSize.X - 380, 60);
            label.Size = new Vector2(360, 36);
        }
        return label;
    }
}
