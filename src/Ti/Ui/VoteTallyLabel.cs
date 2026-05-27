using System;
using System.Text;
using Godot;
using SlayTheStreamer2.Ti.Internal;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2.Ti.Ui;

public sealed partial class VoteTallyLabel : RichTextLabel {
    // CanvasLayer index above the boss-vote / act-variant popups (Layer=100), so this
    // tally text renders in front of those full-screen overlays.
    private const int CanvasLayerIndex = 110;

    // Vanilla default body font. Loaded via ResourceLoader so a missing resource
    // degrades silently to Godot's default font (preserves the Ti/ seam — a fork
    // for another host game with no Kreon asset just renders unstyled text).
    private const string FontPath = "res://themes/kreon_regular_shared.tres";

    // Panel anchor — corner-pinned to one viewport edge at TopAnchor's Y position;
    // the panel auto-sizes to the label content and grows inward + downward from
    // the anchor point. ScreenEdgePadding nudges the panel a few px off the literal
    // screen edge so the backdrop has visible breathing room.
    private const float TopAnchor = 0.15f;
    private const float ScreenEdgePadding = 8f;

    // Backdrop stylebox values — semi-transparent dark, soft rounded corners,
    // small internal padding so the text doesn't touch the box edge.
    private static readonly Color BackdropColor = new(0f, 0f, 0f, 0.55f);
    private const int BackdropCornerRadius = 6;
    private const int BackdropPaddingX = 12;
    private const int BackdropPaddingY = 6;

    private VoteSession? _session;
    private CanvasLayer? _canvasLayer;
    private bool _placeOnLeft;
    private Func<bool>? _isOccludingOverlayVisible;
    private EventHandler<VoteSession>? _closedHandler;
    private EventHandler<VoteSession>? _cancelledHandler;
    /// <summary>
    /// Optional probe returning true when the active run has died mid-vote
    /// (abandoned, game-over, save-quit-to-main-menu). When true, the label
    /// cancels the session so the Cancelled-event handler frees the wrapper
    /// canvas promptly — without it, the label persists until the vote timer
    /// expires, blocking subsequent runs from starting their own votes.
    /// Game-agnostic seam: <see cref="Func{Boolean}"/> only, no MegaCrit refs.
    /// </summary>
    private Func<bool>? _isRunDying;

    // Cache-invalidation triggers — rebuild Text only when one of these changes.
    private int _cachedSecondsLeft = -1;
    private int _cachedTallyVersion = -1;

    public static void AttachTo(VoteSession session, Func<bool>? isRunDying = null, bool placeOnLeft = false, Func<bool>? isOccludingOverlayVisible = null) {
        var tree = (Engine.GetMainLoop() as SceneTree);
        if (tree?.Root is null) {
            TiLog.Warn("[vote-tally-label] no SceneTree.Root available; skipping UI attach");
            return;
        }

        var canvasLayer = new CanvasLayer {
            Name = "VoteTallyLabelCanvasLayer",
            Layer = CanvasLayerIndex,
        };

        // Backdrop panel auto-sizes to the label content and grows inward from the
        // chosen screen edge. Solves readability issues where varied game art
        // (sun-flares, bright VFX, mid-luma silhouettes) defeat plain text shadows.
        var panel = new PanelContainer { Name = "VoteTallyPanel" };
        var bg = new StyleBoxFlat {
            BgColor                 = BackdropColor,
            CornerRadiusTopLeft     = BackdropCornerRadius,
            CornerRadiusTopRight    = BackdropCornerRadius,
            CornerRadiusBottomLeft  = BackdropCornerRadius,
            CornerRadiusBottomRight = BackdropCornerRadius,
            ContentMarginLeft       = BackdropPaddingX,
            ContentMarginRight      = BackdropPaddingX,
            ContentMarginTop        = BackdropPaddingY,
            ContentMarginBottom     = BackdropPaddingY,
        };
        panel.AddThemeStyleboxOverride("panel", bg);
        panel.AnchorTop = TopAnchor;
        panel.AnchorBottom = TopAnchor;
        panel.OffsetTop = 0;
        panel.OffsetBottom = 0;
        panel.GrowVertical = Control.GrowDirection.End;
        if (placeOnLeft) {
            panel.AnchorLeft = 0;
            panel.AnchorRight = 0;
            panel.OffsetLeft = ScreenEdgePadding;
            panel.OffsetRight = ScreenEdgePadding;
            panel.GrowHorizontal = Control.GrowDirection.End;
        } else {
            panel.AnchorLeft = 1;
            panel.AnchorRight = 1;
            panel.OffsetLeft = -ScreenEdgePadding;
            panel.OffsetRight = -ScreenEdgePadding;
            panel.GrowHorizontal = Control.GrowDirection.Begin;
        }

        var label = new VoteTallyLabel { Name = "VoteTallyLabel" };
        label.BbcodeEnabled = true;
        label.FitContent = true;
        // Disable wrapping so the label's natural width = longest single-line content;
        // PanelContainer then sizes the backdrop to hug that width exactly.
        label.AutowrapMode = TextServer.AutowrapMode.Off;
        ApplyKreonFont(label);
        // Slight breathing room between table cells. Defaults are 0 — text touches.
        label.AddThemeConstantOverride("table_h_separation", 14);
        label.AddThemeConstantOverride("table_v_separation", 2);
        label._session = session;
        label._canvasLayer = canvasLayer;
        label._placeOnLeft = placeOnLeft;
        label._isRunDying = isRunDying;
        label._isOccludingOverlayVisible = isOccludingOverlayVisible;
        label._closedHandler = (_, _) => label.SafeQueueFree();
        label._cancelledHandler = (_, _) => label.SafeQueueFree();
        session.Closed += label._closedHandler;
        session.Cancelled += label._cancelledHandler;

        panel.AddChild(label);
        canvasLayer.AddChild(panel);
        tree.Root.AddChild(canvasLayer);
    }

    private static void ApplyKreonFont(RichTextLabel label) {
        var font = ResourceLoader.Load<Font>(FontPath);
        if (font is null) return;
        label.AddThemeFontOverride("normal_font", font);
        label.AddThemeFontOverride("bold_font", font);
        label.AddThemeFontOverride("italics_font", font);
        label.AddThemeFontOverride("bold_italics_font", font);
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

        // Cancel the session immediately if the run died mid-vote. Without this
        // the label hangs around until the vote timer expires, blocking the next
        // run from starting its own vote. session.Cancel triggers Cancelled →
        // _cancelledHandler → SafeQueueFree → wrapper canvas freed at next frame.
        bool runDying = false;
        try { runDying = _isRunDying?.Invoke() ?? false; }
        catch { /* probe must never crash _Process */ }
        if (runDying) {
            try { _session.Cancel(); } catch { /* swallow — session may already be closing */ }
            return;
        }

        // Yield the screen to an occluding overlay (pause menu, dev console,
        // settings submenu). Hide the wrapper CanvasLayer; the vote keeps
        // running and chat votes still tally — only the visual is suppressed.
        bool occluded = false;
        try { occluded = _isOccludingOverlayVisible?.Invoke() ?? false; }
        catch { /* probe must never crash _Process */ }
        if (_canvasLayer is not null && _canvasLayer.Visible == occluded) {
            _canvasLayer.Visible = !occluded;
        }
        if (occluded) return;   // skip text rebuild while hidden

        var secondsLeft = Math.Max(0, (int)_session.TimeRemaining.TotalSeconds);
        var tallyVersion = _session.TallyVersion;

        if (secondsLeft == _cachedSecondsLeft &&
            tallyVersion == _cachedTallyVersion) {
            return;
        }

        _cachedSecondsLeft = secondsLeft;
        _cachedTallyVersion = tallyVersion;

        var sb = new StringBuilder();
        // Vote-ID in header so YT viewers (who don't see Twitch receipts) can use the !NN syntax.
        if (_session.ShowTag) {
            sb.AppendLine($"Chat voting [{_session.VoteId:D2}] — {secondsLeft}s left, type #N (or #N!{_session.VoteId:D2})");
        } else {
            sb.AppendLine($"Chat voting — {secondsLeft}s left, type #N");
        }

        var perPlatform = _session.TalliesByPlatform;
        if (perPlatform is null) {
            // Single-platform — keep the original prose rendering with option names.
            // No table needed for a single tally column.
            for (int i = 0; i < _session.Options.Count; i++) {
                _session.Tallies.TryGetValue(i, out var count);
                sb.AppendLine($"#{i} {_session.Options[i].Label}: {count}");
            }
        } else {
            // Multi-platform — BBCode [table] for fixed-column alignment. Columns:
            //   [option index] [platform 1] [platform 2] ...
            // ConfiguredPlatforms drives the column count; absent platforms (no YT
            // connection) naturally drop their column.
            var platforms = _session.ConfiguredPlatforms;
            int numCols = 1 + platforms.Count;
            sb.Append($"[table={numCols}]");
            // Header row: empty index cell, then platform abbreviations.
            sb.Append("[cell][/cell]");
            foreach (var platform in platforms) {
                sb.Append($"[cell]{AbbreviatePlatform(platform)}[/cell]");
            }
            // Body rows: #N + count per platform.
            for (int i = 0; i < _session.Options.Count; i++) {
                sb.Append($"[cell]#{i}[/cell]");
                foreach (var platform in platforms) {
                    perPlatform.TryGetValue((platform, i), out var count);
                    sb.Append($"[cell]{count}[/cell]");
                }
            }
            sb.Append("[/table]");
        }
        // The PanelContainer auto-sizes to the WIDEST line — usually the header
        // ("Chat voting [01] — Xs left, type #N ..."). Narrower lines (the
        // BBCode table) then float left within that wider box. When the panel
        // is anchored to the right side of the screen, the table looks like
        // it's drifted away from the panel's right edge — fix is to right-align
        // the whole content via BBCode wrap so every row hugs the panel's
        // right edge. Left-side placement keeps default left alignment.
        Text = _placeOnLeft ? sb.ToString() : $"[right]{sb}[/right]";
    }

    /// <summary>
    /// Short header label for a platform in the multi-platform table. Known
    /// platforms get curated abbreviations; unknown platforms fall back to
    /// the first two characters Title-Cased so future platform additions
    /// degrade to something readable without a code change.
    /// </summary>
    private static string AbbreviatePlatform(string platform) => platform switch {
        "twitch" => "Tw",
        "youtube" => "YT",
        _ => platform.Length switch {
            0 => "",
            1 => platform.ToUpperInvariant(),
            _ => char.ToUpperInvariant(platform[0]) + platform.Substring(1, 1),
        },
    };

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
        // Free the CanvasLayer wrapper — freeing the layer cascades to its label child.
        // Falls back to freeing just the label if the wrapper isn't tracked (e.g. legacy
        // direct-attach path).
        if (_canvasLayer is not null
                && GodotObject.IsInstanceValid(_canvasLayer)
                && !_canvasLayer.IsQueuedForDeletion()) {
            _canvasLayer.QueueFree();
            return;
        }
        if (GodotObject.IsInstanceValid(this) && !IsQueuedForDeletion()) {
            QueueFree();
        }
    }
}
