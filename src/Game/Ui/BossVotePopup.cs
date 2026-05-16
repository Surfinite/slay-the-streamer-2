using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Combat;
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

    /// <summary>
    /// Slot size for each per-column animated portrait. 384×384 was chosen
    /// to give combat-idle sprites (~500–800px native) reasonable visual
    /// impact at fit-scale ≈ 0.5–0.77. Bumped from 256×256 (B.3 era PNG
    /// portraits) after 9-reviewer consensus that 256² felt cramped.
    /// </summary>
    private static readonly Vector2 PortraitSlotSize = new(384, 384);

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

    /// <summary>
    /// Optional probe returning true when the active run has died mid-vote
    /// (abandoned, game-over, save-quit-to-main-menu). When true, the popup
    /// cancels the session so the Cancelled-event handler frees this popup
    /// promptly — without it, the popup persists until the 30s vote timer
    /// expires, blocking the game-over Continue button and overlaying the
    /// main-menu screen. MegaCrit-free seam: probe supplied by BossVotePatch.
    /// </summary>
    private readonly Func<bool>? _isRunDying;

    private CanvasLayer? _canvasLayer;
    private RichTextLabel? _timerLabel;
    private readonly List<RichTextLabel> _tallyLabels = new();

    /// <summary>
    /// Slot Controls (one per column) — Godot type only, no MegaCrit refs.
    /// Iterated by the _Process occlusion handler to toggle ProcessMode
    /// (Disabled when occluded; Inherit otherwise). Setting Disabled on
    /// the slot cascades to NCreatureVisuals children via Godot's
    /// ProcessMode.Inherit semantics, freezing Spine playback.
    /// </summary>
    private readonly List<Control> _portraitSlots = new();

    /// <summary>
    /// Pairs of (slot, factory-produced visuals) queued during column build.
    /// Dispatched as fire-and-forget ApplyPortraitFit tasks AFTER the canvas
    /// layer is added to the scene tree, so GetTree() is non-null inside the
    /// async helper. Cleared after dispatch.
    /// </summary>
    private readonly List<(Control Slot, Node2D Visuals)> _pendingFits = new();

    private EventHandler<VoteSession>? _closedHandler;
    private EventHandler<VoteSession>? _cancelledHandler;

    private int _cachedSecondsLeft = -1;
    private int _cachedTallyVersion = -1;

    public BossVotePopup(
        IReadOnlyList<BossVotePopupOption> options,
        VoteSession session,
        IMainThreadDispatcher dispatcher,
        Func<bool>? isOccludingOverlayVisible = null,
        Func<bool>? isRunDying = null) {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _isOccludingOverlayVisible = isOccludingOverlayVisible;
        _isRunDying = isRunDying;
    }

    /// <summary>
    /// Build the CanvasLayer + backdrop + columns and add to SceneTree.Root.
    /// Subscribe to session lifecycle events. Must be called on the main thread.
    /// </summary>
    public void Show(int actNumberOneBased) {
        // Idempotency: Show() must be called at most once per instance.
        // The patch's _voteInProgress flag already guards against double-fire,
        // but this explicit guard makes the invariant locally visible and
        // prevents silent leaks (slot accumulation, double-subscribe of
        // session handlers) if the upstream guard ever weakens.
        if (_canvasLayer is not null) return;

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

            // Portrait slot: sized Control parents the factory-produced Node2D.
            // ClipContents = true belts any sprite that draws beyond Bounds.
            // ProcessMode is Inherit by default; the occlusion handler toggles
            // Inherit↔Disabled to drive the Spine-playback freeze via cascade.
            var slot = new Control {
                Name = "PortraitSlot",
                CustomMinimumSize = PortraitSlotSize,
                ClipContents = true,
            };
            col.AddChild(slot);
            _portraitSlots.Add(slot);

            if (opt.VisualsFactory is not null) {
                try {
                    var visuals = opt.VisualsFactory.Invoke();
                    slot.AddChild(visuals);
                    // Queue — actual measurement/fit happens after the canvas is
                    // added to SceneTree.Root (see end of Show()), so GetTree()
                    // is non-null when ApplyPortraitFit's await fires.
                    _pendingFits.Add((slot, visuals));
                } catch (Exception ex) {
                    TiLog.Warn($"[SlayTheStreamer2][boss-vote] visuals factory threw for column {opt.Index}: {ex.Message}");
                }
            }

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

        // Now that the popup is in the tree, GetTree() returns non-null.
        // Dispatch queued fits as fire-and-forget tasks; each has its own
        // try/catch so unobserved exceptions don't surface unpredictably.
        foreach (var (slot, visuals) in _pendingFits) {
            _ = ApplyPortraitFit(slot, visuals);
        }
        _pendingFits.Clear();
    }

    /// <summary>
    /// Defers Bounds.Size measurement by one process frame (Spine atlas
    /// measurement is typically lazy) and applies a fit-scale + center
    /// position to the visuals inside the slot. Fire-and-forget; wraps the
    /// body in try/catch so exceptions are logged, not unobserved.
    /// </summary>
    private async Task ApplyPortraitFit(Control slot, Node2D visuals) {
        try {
            // Safe: this runs only AFTER Show() added the canvas to the tree.
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!GodotObject.IsInstanceValid(visuals) || !GodotObject.IsInstanceValid(slot)) return;

            var boundsRect = GetVisualBoundsRect(visuals);
            if (boundsRect.Size == Vector2.Zero) {
                // Spine atlas measurement may take >1 frame on some hardware; non-Spine
                // creatures (HasSpineAnimation = false) also return zero bounds from our
                // GetVisualBoundsRect helper. PortraitFit's Mathf.Max floor returns fit=1.0
                // here, ClipContents=true on the slot belts overflow rendering. Logged
                // at Info (not Warn) because the non-Spine path is expected for any
                // future static-pose boss; surfaces in godot.log for observability
                // without crying wolf.
                TiLog.Info($"[SlayTheStreamer2][boss-vote] Bounds.Size zero (Spine measurement pending or non-Spine creature); fit=1.0 + clip applied");
            }

            // Use the design-intent slot size (PortraitSlotSize) rather than slot.Size for
            // fit + centering math. The slot Control's actual .Size grows past CustomMinimumSize
            // when its parent VBoxContainer expands to fill the popup's column area, which
            // would inflate the fit-scale and push the sprite far above where it belongs.
            // PortraitSlotSize is the consistent reference frame for both calculations.
            var fitSlot = PortraitSlotSize;

            // PortraitFit uses System.Numerics.Vector2 (the test csproj is non-Godot,
            // so the helper had to be BCL-typed to be unit-testable). Tiny conversion
            // here keeps the helper testable without forcing the popup to be too.
            var fit = PortraitFit.ComputeFitScale(
                new System.Numerics.Vector2(boundsRect.Size.X, boundsRect.Size.Y),
                new System.Numerics.Vector2(fitSlot.X, fitSlot.Y));

            // Safety margin: idle animations can oscillate beyond the rest-pose Bounds
            // measurement (Soul Fysh is the observed case in B.3.1 operator validation).
            // 0.92 inset (4% per side) eliminates the rare animation clip without a
            // visible size change on bosses whose motion is contained within Bounds.
            // Tuned during gate 1; revisit if a future boss's motion exceeds 8%.
            fit *= 0.92f;

            ApplyScaleAndHue(visuals, fit);

            // Bounds-aware centering: place sprite's local origin so the *visible body*
            // (Bounds.Position to Bounds.Position + Bounds.Size, scaled by fit) is centered
            // in the slot. Without this, the origin (typically at the creature's feet)
            // would land at the slot center and the body would float upward into the
            // column header or out of the slot entirely.
            var boundsCenter = boundsRect.Position + boundsRect.Size * 0.5f;
            visuals.Position = fitSlot * 0.5f - boundsCenter * fit;
        } catch (Exception ex) {
            // Fire-and-forget exception observability — matches the slice's "degrade
            // silently, log, never crash" principle. The popup remains valid; just this
            // column's fit pass failed.
            TiLog.Warn($"[SlayTheStreamer2][boss-vote] ApplyPortraitFit failed: {ex.Message}");
        }
    }

    // Private static helpers: pattern-match-and-cast Node2D → NCreatureVisuals locally.
    // The cast never escapes the popup's public API. A TI-extraction fork would replace
    // these two helper bodies with the new host game's equivalents. Verified APIs:
    // NCreatureVisuals.Bounds (Control) — populated in _Ready, NCreatureVisuals.cs:140.
    // NCreatureVisuals.SetUpSkin(MonsterModel) — at NCreatureVisuals.cs:178.
    // NCreatureVisuals.SetScaleAndHue(float scale, float hue) — at NCreatureVisuals.cs:190.
    private static Rect2 GetVisualBoundsRect(Node2D visuals) {
        if (visuals is NCreatureVisuals cv && cv.Bounds is not null) {
            return new Rect2(cv.Bounds.Position, cv.Bounds.Size);
        }
        return new Rect2(Vector2.Zero, Vector2.Zero);
    }

    private static void ApplyScaleAndHue(Node2D visuals, float scale) {
        if (visuals is NCreatureVisuals cv) cv.SetScaleAndHue(scale, 0f);
    }

    public override void _Process(double delta) {
        if (_session.State is VoteSessionState.Closed
                              or VoteSessionState.Cancelled
                              or VoteSessionState.Disposed) return;
        if (_timerLabel is null) return;

        // Cancel the session immediately if the run died mid-vote. Without this,
        // the popup hangs around through scene transitions to the game-over
        // screen and main menu until the 30s vote timer fires naturally.
        // session.Cancel triggers the Cancelled event → _cancelledHandler →
        // dispatcher.Post(SafeQueueFree) → popup is freed at next frame end.
        bool runDying = false;
        try { runDying = _isRunDying?.Invoke() ?? false; }
        catch { /* probe must never crash _Process */ }
        if (runDying) {
            try { _session.Cancel(); } catch { /* swallow — session may already be closing */ }
            return;
        }

        // Yield the screen to an occluding overlay (e.g., the dev console) so it
        // isn't dimmed by our backdrop. Vote machinery keeps running.
        if (_canvasLayer is not null) {
            bool occluded = false;
            try { occluded = _isOccludingOverlayVisible?.Invoke() ?? false; }
            catch { /* probe must never crash _Process */ }
            if (_canvasLayer.Visible == occluded) {
                _canvasLayer.Visible = !occluded;
                // REUSABLE PATTERN — Spine freeze via Godot's ProcessMode cascade:
                //   ProcessMode.Disabled on a parent Control halts _Process on all
                //   children whose ProcessMode is Inherit (the default). For
                //   Spine-rendered children, this freezes playback without touching
                //   MegaSpine's animation state.
                //   Per CLAUDE.md Tier 4: SceneTree.Paused is never toggled by
                //   StS2's pause menu, so Godot's native Pausable/WhenPaused modes
                //   don't help — drive the freeze from our occlusion probe instead.
                //   See Plan B in v3 spec Open Risks if gate 7 reveals this cascade
                //   doesn't freeze MegaSpine playback.
                foreach (var slot in _portraitSlots) {
                    slot.ProcessMode = occluded ? ProcessModeEnum.Disabled : ProcessModeEnum.Inherit;
                }
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
        // Swallow ui_accept (Enter / Space / gamepad-A) to prevent accidental
        // confirmation of the chest room's Proceed button while the vote runs.
        // ui_cancel (ESC) is deliberately NOT swallowed — the streamer must
        // remain able to open the pause menu (Give Up, Save & Quit, settings).
        // When the pause menu opens, SceneTree.Paused goes true and the
        // isOccludingOverlayVisible probe hides this popup so the menu is usable.
        if (@event.IsActionPressed("ui_accept")) {
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
