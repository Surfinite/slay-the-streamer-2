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
        // DIAGNOSTIC: oversized + bright magenta to confirm rendering pipeline works
        // at all. Strip back to defaults once visibility is confirmed.
        label.AddThemeFontSizeOverride("normal_font_size", 80);
        label.AddThemeColorOverride("default_color", new Color(1f, 0f, 1f));   // magenta
        // Anchored just below VoteTallyLabel (which uses 0.05-0.4 vertically in the
        // top-right region). This puts the skip counter in the 0.4-0.5 vertical band,
        // visible without overlap. Spatial co-location with the Proceed button is a
        // polish item once we know the rendering pipeline works at all.
        label.AnchorLeft = 0.6f;
        label.AnchorTop = 0.4f;
        label.AnchorRight = 0.98f;
        label.AnchorBottom = 0.5f;

        tree.Root.AddChild(label);
        TiLog.Info("[SlayTheStreamer2][card-skip-label] attached under SceneTree.Root with anchor 0.6,0.4,0.98,0.5; DIAGNOSTIC font=80 magenta");
        return label;
    }
}
