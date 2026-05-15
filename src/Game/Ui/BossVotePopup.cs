using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using SlayTheStreamer2.Ti.Internal;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2.Game.Ui;

/// <summary>
/// Modal boss-vote popup. Self-owned CanvasLayer rooted at SceneTree.Root.
/// Renders up to N portrait columns plus a live timer and per-column tally
/// labels. Lifecycle: subscribes to session.Closed / session.Cancelled with
/// handlers marshaled through IMainThreadDispatcher to guarantee main-thread
/// QueueFree. Live tally + timer via _Process polling — NOT subscribed to
/// TallyChanged (which can fire on the chat parser's thread).
/// </summary>
internal sealed partial class BossVotePopup : Control {
    /// <summary>
    /// Layer index for the popup's CanvasLayer. Spike Step 9 verified no
    /// vanilla CanvasLayer uses 100; adjust here if a future spike finds
    /// a collision.
    /// </summary>
    public const int LAYER_INDEX = 100;

    private readonly IReadOnlyList<BossVotePopupOption> _options;
    private readonly VoteSession _session;
    private readonly IMainThreadDispatcher _dispatcher;
    /// <summary>
    /// Optional probe returning true when an in-game debug console (or any other
    /// vanilla overlay we don't want to occlude) is visible. When true, the popup
    /// hides its entire CanvasLayer so the streamer can see/use the console. The
    /// vote keeps running in the background — vote timer is real-time, not paused.
    /// MegaCrit-free seam: BossVotePatch supplies the probe, popup stays game-agnostic.
    /// </summary>
    private readonly Func<bool>? _isOccludingOverlayVisible;

    private CanvasLayer? _canvasLayer;
    private RichTextLabel? _timerLabel;
    private readonly List<RichTextLabel> _tallyLabels = new();

    private EventHandler<VoteSession>? _closedHandler;
    private EventHandler<VoteSession>? _cancelledHandler;

    private int _cachedSecondsLeft = -1;
    private int _cachedTallyVersion = -1;

    public BossVotePopup(
        IReadOnlyList<BossVotePopupOption> options,
        VoteSession session,
        IMainThreadDispatcher dispatcher,
        Func<bool>? isOccludingOverlayVisible = null) {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _isOccludingOverlayVisible = isOccludingOverlayVisible;
    }

    /// <summary>
    /// Build the CanvasLayer + backdrop + columns and add to SceneTree.Root.
    /// Subscribe to session lifecycle events. Must be called on the main thread.
    /// </summary>
    public void Show(int actNumberOneBased) {
        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree?.Root is null) {
            TiLog.Warn("[SlayTheStreamer2][boss-vote] no SceneTree.Root available; popup not shown");
            return;
        }

        _canvasLayer = new CanvasLayer {
            Name = "BossVotePopupCanvasLayer",
            Layer = LAYER_INDEX,
            ProcessMode = ProcessModeEnum.Always,   // keep updating during pause
        };

        // Backdrop — full-screen 60%-opaque black, stops mouse input.
        var backdrop = new ColorRect {
            Name = "Backdrop",
            Color = new Color(0, 0, 0, 0.6f),
            MouseFilter = MouseFilterEnum.Stop,
            AnchorLeft = 0, AnchorTop = 0, AnchorRight = 1, AnchorBottom = 1,
        };
        _canvasLayer.AddChild(backdrop);

        // Content root — VBox centered with title, timer, columns.
        var content = new VBoxContainer {
            Name = "Content",
            AnchorLeft = 0.1f, AnchorTop = 0.15f, AnchorRight = 0.9f, AnchorBottom = 0.85f,
        };
        _canvasLayer.AddChild(content);

        var title = new RichTextLabel {
            Name = "Title",
            BbcodeEnabled = true,
            FitContent = true,
            Text = $"[b]ACT {actNumberOneBased} BOSS VOTE[/b]",
        };
        content.AddChild(title);

        _timerLabel = new RichTextLabel {
            Name = "Timer",
            BbcodeEnabled = true,
            FitContent = true,
            Text = $"{(int)_session.TimeRemaining.TotalSeconds}s left",
        };
        content.AddChild(_timerLabel);

        var columns = new HBoxContainer {
            Name = "Columns",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        content.AddChild(columns);

        foreach (var opt in _options) {
            var col = new VBoxContainer {
                Name = $"Column{opt.Index}",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            columns.AddChild(col);

            // Portrait — defensive load.
            var portrait = new TextureRect {
                Name = "Portrait",
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = new Vector2(256, 256),
            };
            if (!string.IsNullOrEmpty(opt.PortraitPath)) {
                // Pre-check existence so missing assets (e.g., Spine-only bosses
                // like Ceremonial Beast that ship no PNG fallback) don't trigger
                // engine-level "No loader found" / "Error loading resource"
                // spam in godot.log. ResourceLoader.Load returns null on miss
                // but Godot still prints the errors before returning.
                if (ResourceLoader.Exists(opt.PortraitPath)) {
                    try {
                        var tex = ResourceLoader.Load<Texture2D>(opt.PortraitPath);
                        if (tex is not null) portrait.Texture = tex;
                    } catch (Exception ex) {
                        TiLog.Warn($"[SlayTheStreamer2][boss-vote] portrait load failed for {opt.PortraitPath}: {ex.Message}");
                    }
                } else {
                    TiLog.Info($"[SlayTheStreamer2][boss-vote] no PNG portrait available for {opt.Title} (path: {opt.PortraitPath}); showing empty");
                }
            }
            col.AddChild(portrait);

            var nameLabel = new RichTextLabel {
                Name = "Name",
                BbcodeEnabled = true,
                FitContent = true,
                Text = $"#{opt.Index} {opt.Title}",
            };
            col.AddChild(nameLabel);

            var tally = new RichTextLabel {
                Name = "Tally",
                BbcodeEnabled = true,
                FitContent = true,
                Text = "0",
            };
            col.AddChild(tally);
            _tallyLabels.Add(tally);
        }

        // Lifecycle hooks: marshal cleanup through the dispatcher to guarantee
        // main-thread context (Closed/Cancelled may fire from the chat parser
        // thread or the timer callback).
        _closedHandler = (_, _) => _dispatcher.Post(SafeQueueFree);
        _cancelledHandler = (_, _) => _dispatcher.Post(SafeQueueFree);
        _session.Closed += _closedHandler;
        _session.Cancelled += _cancelledHandler;

        tree.Root.AddChild(_canvasLayer);
        _canvasLayer.AddChild(this);   // popup Control is parented under the layer
    }

    public override void _Process(double delta) {
        if (_session.State is VoteSessionState.Closed
                              or VoteSessionState.Cancelled
                              or VoteSessionState.Disposed) return;
        if (_timerLabel is null) return;

        // Yield the screen to an occluding overlay (e.g., the dev console) so it
        // isn't dimmed by our backdrop. Vote machinery keeps running.
        if (_canvasLayer is not null) {
            bool occluded = false;
            try { occluded = _isOccludingOverlayVisible?.Invoke() ?? false; }
            catch { /* probe must never crash _Process */ }
            if (_canvasLayer.Visible == occluded) {
                _canvasLayer.Visible = !occluded;
            }
            if (occluded) return;   // skip rebuilding label text while hidden
        }

        int secondsLeft = Math.Max(0, (int)_session.TimeRemaining.TotalSeconds);
        int tallyVersion = _session.TallyVersion;

        if (secondsLeft == _cachedSecondsLeft && tallyVersion == _cachedTallyVersion) return;
        _cachedSecondsLeft = secondsLeft;
        _cachedTallyVersion = tallyVersion;

        _timerLabel.Text = $"{secondsLeft}s left";

        for (int i = 0; i < _options.Count; i++) {
            var opt = _options[i];
            _session.Tallies.TryGetValue(opt.Index, out int count);
            var bar = new StringBuilder();
            for (int b = 0; b < count && b < 20; b++) bar.Append('▮');
            _tallyLabels[i].Text = $"{bar} {count}";
        }
    }

    public override void _UnhandledInput(InputEvent @event) {
        if (@event.IsActionPressed("ui_accept") || @event.IsActionPressed("ui_cancel")) {
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _ExitTree() {
        if (_closedHandler is not null) _session.Closed -= _closedHandler;
        if (_cancelledHandler is not null) _session.Cancelled -= _cancelledHandler;
        _closedHandler = null;
        _cancelledHandler = null;
        base._ExitTree();
    }

    private void SafeQueueFree() {
        if (_canvasLayer is not null
                && GodotObject.IsInstanceValid(_canvasLayer)
                && !_canvasLayer.IsQueuedForDeletion()) {
            _canvasLayer.QueueFree();
        }
    }
}
