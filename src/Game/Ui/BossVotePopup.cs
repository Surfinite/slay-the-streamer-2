using System;
using System.Collections.Generic;
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

    // Vanilla default body font + variants used for per-label theming.
    private const string FontPath = "res://themes/kreon_regular_shared.tres";
    private const string TitleFontPath = "res://themes/kreon_bold_shared.tres";
    private const string TimerFontPath = "res://themes/kreon_regular_glyph_space_one.tres";
    // Match ActVariantVotePopup's countdown timer light-blue color.
    private static readonly Color TimerColor = new(0.529f, 0.808f, 0.921f, 1f);
    // Warm cream used by vanilla body text (top bar, settings, vfx damage/heal numbers,
    // proceed button, etc.). Softer than pure white against dark backdrops.
    private static readonly Color BodyTextColor = new(1f, 0.964706f, 0.886275f, 1f);

    /// <summary>
    /// Slot size for each per-column animated portrait. Layered evolution:
    ///   - 256×256 (B.3 PNG era, static portraits)
    ///   - 384×384 (B.3.1 Round 1 — 9-reviewer consensus that 256 felt cramped)
    ///   - 448×448 (B.3.1 gate-1 follow-up — operator validation confirmed plenty
    ///     of horizontal room on standard widescreen, 3 × 448 = 1344 px with
    ///     ~580 px breathing room at 1920 wide; only 4:3 test windows feel tight
    ///     and nobody plays at 4:3).
    ///   - 448×640 (v0.106.1-compat tuning, 2026-05-25 — taller slot to suit
    ///     bosses whose visible body is tall-narrow rather than square; the
    ///     extra vertical room also gives the safety margin below more headroom,
    ///     letting it return to the original 0.92 multiplier).
    /// </summary>
    private static readonly Vector2 PortraitSlotSize = new(448, 640);

    /// <summary>
    /// Gap (in px) between the portrait slot bottom and the first text line (#N).
    /// Negative pulls the text up into the slot's lower transparent area, bringing
    /// the label block visually closer to the animated boss.
    /// </summary>
    private const int PortraitToTextGap = -60;

    /// <summary>
    /// Gap (in px) between each of the three per-column text lines (#N / boss
    /// name / tally count). Negative pulls them together; 0 is tight stacking
    /// driven only by each Label's intrinsic font line-height.
    /// </summary>
    private const int TextLineSpacing = 8;

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
    private Label? _timerLabel;
    private readonly List<Label> _tallyLabels = new();

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
    /// <paramref name="coreTitle"/> is the round-specific headline (e.g.
    /// "Pick the deadliest Act 3 boss (1 of 2)."); the popup prepends the
    /// optional vote-ID tag.
    /// </summary>
    public void Show(string coreTitle) {
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
            Color = new Color(0, 0, 0, 0.75f),
            MouseFilter = MouseFilterEnum.Stop,
            AnchorLeft = 0, AnchorTop = 0, AnchorRight = 1, AnchorBottom = 1,
        };
        _canvasLayer.AddChild(backdrop);

        // Content root — VBox centered with title, timer, columns.
        var content = new VBoxContainer {
            Name = "Content",
            AnchorLeft = 0.1f, AnchorTop = 0.1f, AnchorRight = 0.9f, AnchorBottom = 0.85f,
        };
        _canvasLayer.AddChild(content);

        // ShowTag prefixes the title with the live vote ID so YT viewers (who don't
        // see Twitch receipts) can disambiguate against the currently-open vote.
        // The literal "Type #N" instruction was dropped after chat-moderator
        // feedback that repeating it every vote turned into a parroted joke
        // ("I typed #N and nothing happened"). Newcomers still see the syntax
        // in the persistent corner VoteTallyLabel.
        string voteHint = _session.ShowTag
            ? $"[{_session.VoteId:D2}] — "
            : "";
        var title = new Label {
            Name = "Title",
            Text = $"{voteHint}{coreTitle}",
            HorizontalAlignment = HorizontalAlignment.Center,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        ApplyTitleTheme(title);
        content.AddChild(title);

        _timerLabel = new Label {
            Name = "Timer",
            Text = $"{(int)_session.TimeRemaining.TotalSeconds}s left",
            HorizontalAlignment = HorizontalAlignment.Center,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        ApplyTimerTheme(_timerLabel);
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
            // Outer separation: gap between portrait slot and the text block below.
            // Negative pulls the text up into the slot's transparent lower area.
            col.AddThemeConstantOverride("separation", PortraitToTextGap);
            columns.AddChild(col);

            // Portrait slot: sized Control parents the factory-produced Node2D.
            // ClipContents = true belts any sprite that draws beyond Bounds.
            // ProcessMode is Inherit by default; the occlusion handler toggles
            // Inherit↔Disabled to drive the Spine-playback freeze via cascade.
            var slot = new Control {
                Name = "PortraitSlot",
                CustomMinimumSize = PortraitSlotSize,
                ClipContents = true,
                // Hold the slot at PortraitSlotSize.X (448) and center it within the
                // column. Without ShrinkCenter the slot would Fill the column width,
                // and ApplyPortraitFit's centering math (which uses PortraitSlotSize
                // as the reference frame, not slot.Size) would anchor the portrait
                // at column-LEFT — visually misaligning it with the column-centered
                // labels on wide aspect ratios / fewer-column votes (e.g. A10+ acts
                // where the boss vote drops to 2 columns).
                SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
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

            // Inner VBox holds the three text lines so they can have their own
            // line-to-line separation independent of the outer portrait→text gap.
            var textBlock = new VBoxContainer {
                Name = "TextBlock",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            textBlock.AddThemeConstantOverride("separation", TextLineSpacing);
            col.AddChild(textBlock);

            var indexLabel = new Label {
                Name = "Index",
                Text = $"#{opt.Index}",
                HorizontalAlignment = HorizontalAlignment.Center,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            ApplyIndexTheme(indexLabel);
            textBlock.AddChild(indexLabel);

            var nameLabel = new Label {
                Name = "Name",
                Text = opt.Title,
                HorizontalAlignment = HorizontalAlignment.Center,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            ApplyNameTheme(nameLabel);
            textBlock.AddChild(nameLabel);

            // 2b: mark the round-1 winner when it is re-offered in round 2 so chat
            // understands that re-picking it fights the same boss twice.
            if (opt.MarkPriorWinner) {
                var badge = new Label {
                    Name = "PriorWinnerBadge",
                    Text = "★ won round 1 — pick again for a double",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                };
                ApplyBadgeTheme(badge);
                textBlock.AddChild(badge);
            }

            var tally = new Label {
                Name = "Tally",
                Text = "0",
                HorizontalAlignment = HorizontalAlignment.Center,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            ApplyTallyTheme(tally);
            textBlock.AddChild(tally);
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
            // measurement. B.3.1 operator validation set this to 0.88 after Soul Fysh
            // grazed the slot edge at 0.92 — that grazing turned out to come from the
            // wrong animation playing (B.3.1 was setting "idle_loop" unconditionally;
            // Soul Fysh's bestiary idle has a tighter rest pose). v0.106.1-compat fixed
            // the animation routing so each boss plays its correct bestiary idle, and
            // 0.92 is comfortable again — confirmed Soul Fysh fits 2026-05-25. The
            // taller 448×640 slot also gives more vertical headroom for the small
            // residual oscillation, so 4% inset per side is plenty.
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

    private static void ApplyTitleTheme(Label label) {
        var font = ResourceLoader.Load<Font>(TitleFontPath);
        if (font is not null) label.AddThemeFontOverride("font", font);
        label.AddThemeColorOverride("font_color", BodyTextColor);
        label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.5f));
        label.AddThemeConstantOverride("shadow_offset_x", 3);
        label.AddThemeConstantOverride("shadow_offset_y", 2);
        label.AddThemeFontSizeOverride("font_size", 40);
    }

    private static void ApplyTimerTheme(Label label) {
        // Mirrors ActVariantVotePopup.ApplyActNumberTheme — Kreon glyph_space_one,
        // light-blue color, faint drop shadow, size 40.
        var font = ResourceLoader.Load<Font>(TimerFontPath);
        if (font is not null) label.AddThemeFontOverride("font", font);
        label.AddThemeColorOverride("font_color", TimerColor);
        label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.05f));
        label.AddThemeConstantOverride("shadow_offset_x", 3);
        label.AddThemeConstantOverride("shadow_offset_y", 2);
        label.AddThemeFontSizeOverride("font_size", 40);
    }

    private static void ApplyIndexTheme(Label label) {
        var font = ResourceLoader.Load<Font>(FontPath);
        if (font is not null) label.AddThemeFontOverride("font", font);
        label.AddThemeColorOverride("font_color", BodyTextColor);
        label.AddThemeFontSizeOverride("font_size", 32);
    }

    private static void ApplyNameTheme(Label label) {
        var font = ResourceLoader.Load<Font>(FontPath);
        if (font is not null) label.AddThemeFontOverride("font", font);
        label.AddThemeColorOverride("font_color", new Color(0.937f, 0.784f, 0.5f, 1f));
        label.AddThemeFontSizeOverride("font_size", 24);
    }

    private static void ApplyBadgeTheme(Label label) {
        var font = ResourceLoader.Load<Font>(FontPath);
        if (font is not null) label.AddThemeFontOverride("font", font);
        // Amber highlight so the "won round 1" marker reads as a callout, distinct
        // from the gold boss-name line above it.
        label.AddThemeColorOverride("font_color", new Color(1f, 0.66f, 0.26f, 1f));
        label.AddThemeFontSizeOverride("font_size", 18);
    }

    private static void ApplyTallyTheme(Label label) {
        var font = ResourceLoader.Load<Font>(FontPath);
        if (font is not null) label.AddThemeFontOverride("font", font);
        label.AddThemeColorOverride("font_color", BodyTextColor);
        label.AddThemeFontSizeOverride("font_size", 30);
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
            _tallyLabels[i].Text = count.ToString();
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
