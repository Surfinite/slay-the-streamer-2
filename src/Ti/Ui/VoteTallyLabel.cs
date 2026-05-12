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

    // Cache-invalidation triggers — rebuild Text only when one of these changes.
    private int _cachedSecondsLeft = -1;
    private int _cachedTallyVersion = -1;
    private bool _cachedVoteEchoActive;

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
    /// Per-frame poll for tally + time remaining. Cached on (secondsLeft, tallyVersion, voteEchoActive)
    /// so the StringBuilder/Dictionary path runs only when output would actually change. Renders
    /// single-platform or split per-platform rows depending on <c>VoteSession.TalliesByPlatform</c>.
    /// </summary>
    public override void _Process(double delta) {
        if (!GodotObject.IsInstanceValid(this) || _session is null) return;
        if (_session.State is VoteSessionState.Closed
                              or VoteSessionState.Cancelled
                              or VoteSessionState.Disposed) return;

        var secondsLeft = Math.Max(0, (int)_session.TimeRemaining.TotalSeconds);
        var tallyVersion = _session.TallyVersion;
        var echoActive = ComputeEchoActive();

        if (secondsLeft == _cachedSecondsLeft &&
            tallyVersion == _cachedTallyVersion &&
            echoActive == _cachedVoteEchoActive) {
            return;
        }

        _cachedSecondsLeft = secondsLeft;
        _cachedTallyVersion = tallyVersion;
        _cachedVoteEchoActive = echoActive;

        var sb = new StringBuilder();
        // Vote-ID in header so YT viewers (who don't see Twitch receipts) can use the !NN syntax.
        sb.AppendLine($"Chat voting [{_session.VoteId:D2}] — {secondsLeft}s left, type #N (or #N!{_session.VoteId:D2})");

        var perPlatform = _session.TalliesByPlatform;
        if (perPlatform is null) {
            // Single-platform — original rendering path.
            for (int i = 0; i < _session.Options.Count; i++) {
                _session.Tallies.TryGetValue(i, out var count);
                sb.AppendLine($"#{i} {_session.Options[i].Label}: {count}");
            }
        } else {
            // Multi-platform — iterate ConfiguredPlatforms in registration order so configured-but-silent
            // platforms still render their zero rows (no mid-vote rendering snap).
            foreach (var platform in _session.ConfiguredPlatforms) {
                sb.Append($"{Capitalize(platform)}: ");
                for (int i = 0; i < _session.Options.Count; i++) {
                    perPlatform.TryGetValue((platform, i), out var count);
                    if (i > 0) sb.Append(", ");
                    sb.Append($"{i}={count}");
                }
                if (IsVoteEchoVisible(platform)) {
                    sb.Append(" ◀ just now");
                }
                sb.AppendLine();
            }
        }
        Text = sb.ToString();
    }

    private bool ComputeEchoActive() {
        if (_session is null) return false;
        foreach (var platform in _session.ConfiguredPlatforms) {
            if (IsVoteEchoVisible(platform)) return true;
        }
        return false;
    }

    private bool IsVoteEchoVisible(string platform) {
        if (_session is null) return false;
        return _session.LastVoteByPlatform.TryGetValue(platform, out var lastVote)
               && DateTimeOffset.UtcNow - lastVote < TimeSpan.FromSeconds(3);
    }

    private static string Capitalize(string s) {
        if (string.IsNullOrEmpty(s)) return s;
        if (char.IsUpper(s[0])) return s;
        return char.ToUpperInvariant(s[0]) + s.Substring(1);
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
