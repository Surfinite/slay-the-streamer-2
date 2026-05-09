using System;
using System.Text;
using Godot;
using SlayTheStreamer2.Ti.Internal;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2.Ti.Ui;

public sealed partial class VoteTallyLabel : RichTextLabel {
    private VoteSession? _session;
    private EventHandler<VoteSession>? _closedHandler;
    private EventHandler<VoteSession>? _cancelledHandler;

    public static void AttachTo(VoteSession session) {
        var tree = (Engine.GetMainLoop() as SceneTree);
        if (tree?.Root is null) {
            TiLog.Warn("[vote-tally-label] no SceneTree.Root available; skipping UI attach");
            return;
        }

        var label = new VoteTallyLabel { Name = "VoteTallyLabel" };
        label.BbcodeEnabled = true;
        label.FitContent = true;
        // Anchor top-right by default; Surfinite will adjust positioning during polish.
        label.AnchorLeft = 0.6f; label.AnchorTop = 0.05f;
        label.AnchorRight = 0.98f; label.AnchorBottom = 0.4f;
        label._session = session;
        label._closedHandler = (_, _) => label.SafeQueueFree();
        label._cancelledHandler = (_, _) => label.SafeQueueFree();
        session.Closed += label._closedHandler;
        session.Cancelled += label._cancelledHandler;

        // Direct attachment under root. If z-order issues surface in operator-validation,
        // switch to creating/finding a CanvasLayer named "SlayTheStreamerOverlayLayer" under root and attaching there instead.
        tree.Root.AddChild(label);
    }

    /// <summary>
    /// Per-frame poll for tally + time remaining. Intentionally polling-based
    /// for B.1's minimal label — the cost is negligible for a 4-line text node,
    /// and it sidesteps the cleanup complexity of multiple event subscriptions.
    /// B.2's polished VoteOverlayControl should subscribe to TallyChanged instead.
    /// </summary>
    public override void _Process(double delta) {
        if (!GodotObject.IsInstanceValid(this) || _session is null) return;
        if (_session.State is VoteSessionState.Closed
                              or VoteSessionState.Cancelled
                              or VoteSessionState.Disposed) return;

        var sb = new StringBuilder();
        var secondsLeft = Math.Max(0, (int)_session.TimeRemaining.TotalSeconds);
        sb.AppendLine($"Chat voting — {secondsLeft}s left");
        for (int i = 0; i < _session.Options.Count; i++) {
            _session.Tallies.TryGetValue(i, out var count);
            sb.AppendLine($"#{i} {_session.Options[i].Label}: {count}");
        }
        Text = sb.ToString();
    }

    public override void _ExitTree() {
        if (_session is not null) {
            if (_closedHandler is not null) _session.Closed -= _closedHandler;
            if (_cancelledHandler is not null) _session.Cancelled -= _cancelledHandler;
            if (_session.State is VoteSessionState.Open) {
                try { _session.Cancel(); }
                catch (Exception ex) { TiLog.Warn($"[vote-tally-label] session.Cancel threw on _ExitTree: {ex.Message}"); }
            }
        }
        _session = null;
        _closedHandler = null;
        _cancelledHandler = null;
        base._ExitTree();
    }

    private void SafeQueueFree() {
        if (GodotObject.IsInstanceValid(this) && !IsQueuedForDeletion()) {
            QueueFree();
        }
    }
}
