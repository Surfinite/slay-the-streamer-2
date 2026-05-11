using Godot;
using SlayTheStreamer2.Game.DecisionVotes;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.Ui;

/// <summary>
/// Godot RichTextLabel showing the streamer's per-act card-skip budget.
/// Mirrors B.1's VoteTallyLabel pattern (root-parented, anchor-based positioning,
/// no theme overrides) — known to render reliably. For v0.1 diagnostic placement,
/// anchored just below the vote tally region (top-right). Spatial co-location with
/// the proceed button is deferred until we have a working baseline rendering.
/// Hidden when cardSkipsPerAct == -1 (unlimited; nothing to display).
/// </summary>
public partial class CardSkipCounterLabel : RichTextLabel {
    internal void UpdateText(SkipBudgetSnapshot snap) {
        if (snap.LimitThisAct < 0) {
            Visible = false;
            return;
        }
        Visible = true;
        Text = $"Card skips: {snap.RemainingThisAct}/{snap.LimitThisAct} act";
    }

    public static CardSkipCounterLabel AttachTo(Node parent, Control? proceedButton) {
        var tree = parent.GetTree();
        if (tree?.Root is null) {
            TiLog.Warn("[SlayTheStreamer2][card-skip-label] no SceneTree.Root available; skipping UI attach");
            // Return a free-standing label so the caller's contract is preserved;
            // it will never render but won't crash anything either.
            return new CardSkipCounterLabel { Name = "CardSkipCounterLabel" };
        }

        var label = new CardSkipCounterLabel { Name = "CardSkipCounterLabel" };
        label.BbcodeEnabled = true;
        label.FitContent = true;
        // Pass clicks through to the underlying scene. The anchor band overlaps the
        // proceed/skip button area; default Control.MouseFilter is Stop, which would
        // swallow clicks on the bottom half of that button. Ignore lets the click
        // reach the button beneath.
        label.MouseFilter = Control.MouseFilterEnum.Ignore;
        // Right-aligned, vertically positioned just above the proceed button area.
        // Proceed button observed at GlobalPosition.Y ~= 0.83 (1920x1080); anchor band
        // 0.74-0.82 puts the label directly above it without overlapping the rewards
        // screen panel (which ends around Y=0.74). Root-parented for z-order above
        // sibling overlays. Diagnostic earlier proved rendering works in the 0.4-0.5
        // band; this placement is the spec-preferred "near proceed button" location.
        label.AnchorLeft = 0.62f;
        label.AnchorTop = 0.74f;
        label.AnchorRight = 0.98f;
        label.AnchorBottom = 0.82f;

        tree.Root.AddChild(label);
        TiLog.Info("[SlayTheStreamer2][card-skip-label] attached under SceneTree.Root");
        return label;
    }
}
