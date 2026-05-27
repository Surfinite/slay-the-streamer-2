using System;
using System.Collections.Generic;
using Godot;
using SlayTheStreamer2.Ti.Internal;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2.Game.Ui;

/// <summary>
/// Vertical 50/50 split popup for the act-variant vote. Renders L1 (full
/// layered combat backdrop scenes via injected factory closures) or L3
/// (hex-color rects + title labels) based on the mode parameter from
/// PreWarmAssets.
///
/// Fully MegaCrit-free at the public interface; the cancellation probe and
/// no-votes side-channel are Func/Action callbacks injected from the patch
/// (mirroring BossVotePopup's isOccludingOverlayVisible/isRunDying pattern).
///
/// Live tally via `_Process` polling of `_session.TallyVersion` — NOT
/// subscribed to `TallyChanged` (which can fire on the chat parser's
/// thread). Mirrors BossVotePopup's threading-safety pattern.
/// </summary>
internal sealed partial class ActVariantVotePopup : Control {
    private readonly IReadOnlyList<ActVariantOption> _options;
    private readonly IReadOnlyList<Func<Node>?> _factories;
    private readonly ActVariantPopupMode _mode;
    private readonly VoteSession _session;
    private readonly IMainThreadDispatcher _dispatcher;
    private readonly Func<bool> _shouldCancel;
    private readonly Action _onUserAbandoned;
    private readonly Func<bool>? _isOccludingOverlayVisible;

    private CanvasLayer? _canvasLayer;
    private Label[] _indexLabels = Array.Empty<Label>();
    private Label[] _tallyLabels = Array.Empty<Label>();
    private Label? _voteTitleLabel;
    private Label? _timerLabel;
    private bool _userAbandoned;   // Task 11 will set this; declared here so the fields are stable
    private int _cachedTallyVersion = -1;   // -1 sentinel — first poll always updates labels
    private int _cachedSecondsLeft = -1;

    // Stored event handlers so we can unsubscribe. Lambdas inline at += time
    // would be non-removable. Mirrors BossVotePopup.cs:82-83.
    private EventHandler<VoteSession>? _closedHandler;
    private EventHandler<VoteSession>? _cancelledHandler;

    private const int CanvasLayerIndex = 100;
    private const float BackdropAlpha = 0.6f;

    // Vanilla act-banner styling (decompiled/sts2-assets/scenes/ui/act_banner.tscn).
    // Kept as field-style constants so the values are easy to spot-check against
    // a fresh decompile after a game update.
    // "BannerTitle" = the per-column act-variant name (Underdocks / Overgrowth);
    // "VoteTitle"   = the top-of-screen "Vote on the act variant" header that mirrors
    //                 the other vote popups (boss / card-reward / ancient).
    private const string BannerTitleFontPath = "res://themes/spectral_bold_shared.tres";
    private const string ActNumberFontPath = "res://themes/kreon_regular_glyph_space_one.tres";
    private const string TallyFontPath = "res://themes/kreon_regular_shared.tres";
    private const string VoteTitleFontPath = "res://themes/kreon_bold_shared.tres";
    private static readonly Color BannerTitleColor = new(0.937f, 0.784f, 0.318f, 1f);    // gold
    private static readonly Color ActNumberColor = new(0.529f, 0.808f, 0.921f, 1f); // light blue
    private static readonly Color BannerStripColor = new(0f, 0f, 0f, 0.25f);
    private static readonly Color BodyTextColor = new(1f, 0.964706f, 0.886275f, 1f);
    private const float BannerAnchorTop = 0.398f;
    private const float BannerAnchorBottom = 0.583f;

    // ---- Easy-to-tweak positioning + sizing knobs ----
    // Vote title (top-of-screen "Vote on the act variant" header) — y offset
    // from viewport top.
    private const float VoteTitleOffsetY = 160f;
    private const int VoteTitleFontSize = 40;

    // Per-column #N + tally — both anchored to each column's CenterBottom;
    // negative Y offsets move them upward toward the column center. Tweak
    // these two numbers to shift the index/count pair vertically.
    private const float ColumnIndexOffsetY = -400f;
    private const float ColumnTallyOffsetY = -300f;
    private const int IndexFontSize = 40;
    private const int TallyFontSize = 32;

    public ActVariantVotePopup(
            IReadOnlyList<ActVariantOption> candidates,
            IReadOnlyList<Func<Node>?> factories,
            ActVariantPopupMode mode,
            VoteSession session,
            IMainThreadDispatcher dispatcher,
            Func<bool> shouldCancel,
            Action onUserAbandoned,
            Func<bool>? isOccludingOverlayVisible = null) {
        _options = candidates ?? throw new ArgumentNullException(nameof(candidates));
        _factories = factories ?? throw new ArgumentNullException(nameof(factories));
        if (factories.Count != candidates.Count)
            throw new ArgumentException("factories must be parallel to candidates", nameof(factories));
        _mode = mode;
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _shouldCancel = shouldCancel ?? throw new ArgumentNullException(nameof(shouldCancel));
        _onUserAbandoned = onUserAbandoned ?? throw new ArgumentNullException(nameof(onUserAbandoned));
        _isOccludingOverlayVisible = isOccludingOverlayVisible;
    }

    /// <summary>
    /// Builds the CanvasLayer tree and parents it to the gameplay-area
    /// surface ((Engine.GetMainLoop() as SceneTree).Root, mirroring
    /// BossVotePopup). Must be called on the Godot main thread.
    /// </summary>
    public void Open() {
        try {
            _canvasLayer = BuildNodeTree();
            var sceneTree = Engine.GetMainLoop() as SceneTree;
            if (sceneTree?.Root is null) {
                TiLog.Error("[SlayTheStreamer2][act-variant-vote] popup Open: SceneTree.Root is null; cannot parent");
                return;
            }
            sceneTree.Root.AddChild(_canvasLayer);
            // The popup Control itself must be in the tree for Godot to invoke
            // _Process and _Input callbacks. _canvasLayer was constructed fresh
            // by BuildNodeTree and only contains the backdrop + columns; adding
            // `this` as a child of the layer wires the lifecycle. Mirrors
            // BossVotePopup.Show() at BossVotePopup.cs:223.
            _canvasLayer.AddChild(this);
            // Subscribe to BOTH terminal events: Closed fires on natural
            // expiry (timer ran out), Cancelled fires on Cancel() (ESC, run
            // shutdown). VoteSession does NOT fire Closed from Cancel — they
            // are independent — so we'd leak the popup if we only listened
            // to one. Marshal through dispatcher because Cancelled may fire
            // from the chat-parser thread (on connection drop). Mirrors
            // BossVotePopup.Show() at BossVotePopup.cs:217-220.
            _closedHandler = (s, v) => _dispatcher.Post(() => SafeTeardown(s, v));
            _cancelledHandler = (s, v) => _dispatcher.Post(() => SafeTeardown(s, v));
            _session.Closed += _closedHandler;
            _session.Cancelled += _cancelledHandler;
            TiLog.Debug($"[SlayTheStreamer2][act-variant-vote] popup opened (mode={_mode})");
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][act-variant-vote] popup Open threw", ex);
        }
    }

    private CanvasLayer BuildNodeTree() {
        var layer = new CanvasLayer { Layer = CanvasLayerIndex };

        var backdrop = new ColorRect {
            Color = new Color(0f, 0f, 0f, BackdropAlpha),
            MouseFilter = MouseFilterEnum.Stop,
        };
        backdrop.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        layer.AddChild(backdrop);

        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 0);
        hbox.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        layer.AddChild(hbox);

        _indexLabels = new Label[_options.Count];
        _tallyLabels = new Label[_options.Count];

        for (int i = 0; i < _options.Count; i++) {
            var column = BuildColumn(_options[i], _factories[i], i);
            hbox.AddChild(column);
        }

        // Vote title — top-of-screen header mirroring the other vote popups.
        // ShowTag prefixes the title with the live vote ID so YT viewers (who
        // don't see Twitch receipts) can disambiguate against the open vote.
        // "Type #N" instruction dropped after moderator feedback — see the
        // matching comment in BossVotePopup.Show().
        string voteHint = _session.ShowTag
            ? $"[{_session.VoteId:D2}] — "
            : "";
        _voteTitleLabel = new Label {
            Text = $"{voteHint}Pick the most difficult act variant.",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _voteTitleLabel.SetAnchorsAndOffsetsPreset(LayoutPreset.CenterTop);
        _voteTitleLabel.OffsetLeft = -500;
        _voteTitleLabel.OffsetRight = 500;
        _voteTitleLabel.OffsetTop = VoteTitleOffsetY;
        _voteTitleLabel.OffsetBottom = VoteTitleOffsetY + 60;
        ApplyVoteTitleTheme(_voteTitleLabel);
        layer.AddChild(_voteTitleLabel);

        // Countdown timer — full-screen-centered text positioned just above the
        // banner strip, occupying the same on-screen slot the vanilla act banner
        // uses for its "Act N" label. Styled to match (Kreon Regular, light blue).
        _timerLabel = new Label {
            Text = FormatTimer(_session.TimeRemaining),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _timerLabel.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
        _timerLabel.OffsetLeft = -200;
        _timerLabel.OffsetRight = 200;
        _timerLabel.OffsetTop = -100;
        _timerLabel.OffsetBottom = -46;
        ApplyActNumberTheme(_timerLabel);
        layer.AddChild(_timerLabel);

        return layer;
    }

    private static string FormatTimer(TimeSpan remaining) {
        return $"{Math.Max(0, (int)remaining.TotalSeconds)}s left";
    }

    private PanelContainer BuildColumn(ActVariantOption option, Func<Node>? factory, int columnIndex) {
        var column = new PanelContainer {
            ClipContents = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };

        // Free-positioning Control opts out of PanelContainer's sequential
        // layout so overlay children (background, banner, tally) can stack
        // freely.
        var free = new Control { MouseFilter = MouseFilterEnum.Ignore };
        free.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        column.AddChild(free);

        bool useL1 = _mode == ActVariantPopupMode.L1Textures && factory is not null;

        if (useL1) {
            try {
                var visual = factory!();
                if (visual is not null) {
                    // Vanilla combat parents NCombatBackground under BgContainer, which is
                    // a Center-anchored zero-size Control (combat_room.tscn:41 — anchors_preset=8
                    // with cancelling offsets). The Layer_NN offsets inside the .tscn are
                    // calibrated for that "origin at viewport center" frame, NOT
                    // "origin at top-left". Anchoring the visual to FullRect (a top-left frame)
                    // shifts the texture's center off-screen and clips most of it. Mirror
                    // vanilla: wrap in a center-anchored bgHolder so the visual's (0,0) lands
                    // at the column's visual center. ClipContents on the PanelContainer
                    // belts texture overflow into the adjacent column.
                    var bgHolder = new Control { MouseFilter = MouseFilterEnum.Ignore };
                    bgHolder.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
                    free.AddChild(bgHolder);
                    bgHolder.AddChild(visual);
                } else {
                    TiLog.Warn($"[SlayTheStreamer2][act-variant-vote] factory for {option.Key} returned null; degrading column to L3");
                    AddL3Fallback(free, option);
                }
            } catch (Exception ex) {
                TiLog.Warn($"[SlayTheStreamer2][act-variant-vote] factory for {option.Key} threw; degrading column to L3: {ex.Message}");
                AddL3Fallback(free, option);
            }
        } else {
            AddL3Fallback(free, option);
        }

        // Banner — dim horizontal strip (full column width) + gold title text,
        // styled to mirror vanilla act_banner.tscn. Per-column strips appear
        // continuous across the screen seam because they're aligned vertically
        // and have no horizontal padding.
        AddBanner(free, option.Title);

        // Two stacked labels per column: #N on top, count beneath. Both anchored
        // to the column's CenterBottom; tune ColumnIndexOffsetY / ColumnTallyOffsetY
        // (top of file) to nudge their vertical positions.
        var indexLabel = new Label {
            Text = $"#{option.Index}",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        indexLabel.SetAnchorsAndOffsetsPreset(LayoutPreset.CenterBottom);
        indexLabel.OffsetTop = ColumnIndexOffsetY;
        indexLabel.OffsetBottom = ColumnIndexOffsetY + 50;
        indexLabel.OffsetLeft = -150;
        indexLabel.OffsetRight = 150;
        ApplyIndexTheme(indexLabel);
        free.AddChild(indexLabel);
        _indexLabels[columnIndex] = indexLabel;

        var tally = new Label {
            Text = "0",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        tally.SetAnchorsAndOffsetsPreset(LayoutPreset.CenterBottom);
        tally.OffsetTop = ColumnTallyOffsetY;
        tally.OffsetBottom = ColumnTallyOffsetY + 50;
        tally.OffsetLeft = -150;
        tally.OffsetRight = 150;
        ApplyTallyTheme(tally);
        free.AddChild(tally);
        _tallyLabels[columnIndex] = tally;

        return column;
    }

    private void AddL3Fallback(Control parent, ActVariantOption option) {
        var rect = new ColorRect {
            Color = ParseHex(option.FallbackColorHex),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        rect.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        parent.AddChild(rect);
    }

    private static void AddBanner(Control parent, string title) {
        // Dim horizontal strip — matches vanilla act_banner.tscn's Banner ColorRect
        // (modulate 0,0,0,0.25 spanning ~39.8%–58.3% of viewport height).
        var strip = new ColorRect {
            Color = BannerStripColor,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        strip.AnchorLeft = 0;
        strip.AnchorRight = 1;
        strip.AnchorTop = BannerAnchorTop;
        strip.AnchorBottom = BannerAnchorBottom;
        strip.OffsetLeft = 0;
        strip.OffsetRight = 0;
        strip.OffsetTop = 0;
        strip.OffsetBottom = 0;
        parent.AddChild(strip);

        // Title text — Spectral Bold + StS2's gold, centered in the strip.
        var label = new Label {
            Text = title,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        label.AnchorLeft = 0;
        label.AnchorRight = 1;
        label.AnchorTop = BannerAnchorTop;
        label.AnchorBottom = BannerAnchorBottom;
        label.OffsetLeft = 0;
        label.OffsetRight = 0;
        label.OffsetTop = 0;
        label.OffsetBottom = 0;
        ApplyBannerTitleTheme(label);
        parent.AddChild(label);
    }

    private static void ApplyBannerTitleTheme(Label label) {
        // Mirror vanilla act_banner.tscn _actName styling for each act-variant
        // banner (Underdocks / Overgrowth). Font size scaled down from vanilla's
        // 120 to 80 — each column is half-screen wide, so 120 would overflow on
        // narrow windows.
        var font = ResourceLoader.Load<Font>(BannerTitleFontPath);
        if (font is not null) label.AddThemeFontOverride("font", font);
        label.AddThemeColorOverride("font_color", BannerTitleColor);
        label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.05f));
        label.AddThemeConstantOverride("shadow_offset_x", 4);
        label.AddThemeConstantOverride("shadow_offset_y", 3);
        label.AddThemeFontSizeOverride("font_size", 80);
    }

    private static void ApplyVoteTitleTheme(Label label) {
        // Mirrors the other vote popups (BossVotePopup / CardRewardVotePopup /
        // AncientVotePopup) — Kreon Bold, cream body color, soft drop shadow.
        var font = ResourceLoader.Load<Font>(VoteTitleFontPath);
        if (font is not null) label.AddThemeFontOverride("font", font);
        label.AddThemeColorOverride("font_color", BodyTextColor);
        label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.5f));
        label.AddThemeConstantOverride("shadow_offset_x", 3);
        label.AddThemeConstantOverride("shadow_offset_y", 2);
        label.AddThemeFontSizeOverride("font_size", VoteTitleFontSize);
    }

    private static void ApplyIndexTheme(Label label) {
        var font = ResourceLoader.Load<Font>(TallyFontPath);
        if (font is not null) label.AddThemeFontOverride("font", font);
        label.AddThemeColorOverride("font_color", BodyTextColor);
        label.AddThemeFontSizeOverride("font_size", IndexFontSize);
    }

    private static void ApplyActNumberTheme(Label label) {
        // Mirror vanilla act_banner.tscn _actNumber styling. Used here for the
        // countdown timer to keep it visually consistent with the gold title.
        var font = ResourceLoader.Load<Font>(ActNumberFontPath);
        if (font is not null) label.AddThemeFontOverride("font", font);
        label.AddThemeColorOverride("font_color", ActNumberColor);
        label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.05f));
        label.AddThemeConstantOverride("shadow_offset_x", 3);
        label.AddThemeConstantOverride("shadow_offset_y", 2);
        label.AddThemeFontSizeOverride("font_size", 40);
    }

    private static void ApplyTallyTheme(Label label) {
        var font = ResourceLoader.Load<Font>(TallyFontPath);
        if (font is not null) label.AddThemeFontOverride("font", font);
        label.AddThemeColorOverride("font_color", BodyTextColor);
        label.AddThemeFontSizeOverride("font_size", TallyFontSize);
    }

    private static Color ParseHex(string rrggbb) {
        // RRGGBB format only — no '#', no alpha. Per ActVariantOption doc.
        if (string.IsNullOrEmpty(rrggbb) || rrggbb.Length != 6)
            return new Color(0.5f, 0.5f, 0.5f, 1f);
        try {
            int r = Convert.ToInt32(rrggbb.Substring(0, 2), 16);
            int g = Convert.ToInt32(rrggbb.Substring(2, 2), 16);
            int b = Convert.ToInt32(rrggbb.Substring(4, 2), 16);
            return new Color(r / 255f, g / 255f, b / 255f, 1f);
        } catch {
            return new Color(0.5f, 0.5f, 0.5f, 1f);
        }
    }

    // Lifecycle: _Process polls TallyVersion + cancellation; _Input handles
    // ESC; OnClosed cleans up the CanvasLayer.

    public override void _Process(double delta) {
        if (_userAbandoned) return;

        // Yield the screen to an occluding overlay (e.g., the dev console) so it
        // isn't covered by the popup. Vote machinery keeps running in the background.
        // Mirrors BossVotePopup._Process at BossVotePopup.cs:335.
        if (_canvasLayer is not null && _isOccludingOverlayVisible is not null) {
            bool occluded = false;
            try { occluded = _isOccludingOverlayVisible(); }
            catch { /* probe must never crash _Process */ }
            if (_canvasLayer.Visible == occluded) {
                _canvasLayer.Visible = !occluded;
            }
            if (occluded) return;
        }

        // Cancel polling.
        bool shouldCancel = false;
        try { shouldCancel = _shouldCancel(); }
        catch (Exception ex) {
            TiLog.Warn($"[SlayTheStreamer2][act-variant-vote] shouldCancel probe threw: {ex.Message}");
        }
        if (shouldCancel) {
            TryFireCancellation();
            return;  // don't read tally if cancelling
        }

        // Tally version polling — TallyChanged fires from chat-parser thread,
        // so we poll from _Process (main thread) instead of subscribing.
        // Mirrors BossVotePopup's pattern (see BossVotePopup.cs:17-18 class doc).
        int tallyVersion = _session.TallyVersion;
        if (tallyVersion != _cachedTallyVersion) {
            _cachedTallyVersion = tallyVersion;
            try {
                var tallies = _session.Tallies;
                for (int i = 0; i < _options.Count && i < _tallyLabels.Length; i++) {
                    int count = tallies.TryGetValue(_options[i].Index, out var c) ? c : 0;
                    _tallyLabels[i].Text = count.ToString();
                }
            } catch (Exception ex) {
                TiLog.Warn($"[SlayTheStreamer2][act-variant-vote] tally update threw: {ex.Message}");
            }
        }

        // Countdown timer polling.
        if (_timerLabel is not null) {
            int secondsLeft = Math.Max(0, (int)_session.TimeRemaining.TotalSeconds);
            if (secondsLeft != _cachedSecondsLeft) {
                _cachedSecondsLeft = secondsLeft;
                _timerLabel.Text = $"{secondsLeft}s left";
            }
        }
    }

    public override void _Input(InputEvent @event) {
        // _Input fires BEFORE _UnhandledInput — guarantees popup gets ESC even
        // if a parent control would have consumed it. Per v3 spec S5.
        if (_userAbandoned) return;
        // When an occluding overlay (dev console) is visible, the popup is hidden;
        // pass ESC through to the overlay's own handler instead of cancelling the vote.
        if (_isOccludingOverlayVisible is not null) {
            try { if (_isOccludingOverlayVisible()) return; } catch { /* swallow */ }
        }
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape }) {
            TryFireCancellation();
            try { GetViewport().SetInputAsHandled(); } catch { /* swallow */ }
        }
    }

    private void TryFireCancellation() {
        _userAbandoned = true;
        try { _onUserAbandoned(); }
        catch (Exception ex) {
            TiLog.Warn($"[SlayTheStreamer2][act-variant-vote] onUserAbandoned threw: {ex.Message}");
        }
        try { _session.Cancel(); }
        catch (Exception ex) {
            TiLog.Warn($"[SlayTheStreamer2][act-variant-vote] session.Cancel threw: {ex.Message}");
        }
    }

    private void SafeTeardown(object? sender, VoteSession session) {
        // Idempotent: both Closed and Cancelled call this; whichever fires
        // first does the unsubscribe + QueueFree, the second is a no-op.
        try { if (_closedHandler is not null) _session.Closed -= _closedHandler; } catch { /* swallow */ }
        try { if (_cancelledHandler is not null) _session.Cancelled -= _cancelledHandler; } catch { /* swallow */ }
        _closedHandler = null;
        _cancelledHandler = null;
        if (_canvasLayer is not null && GodotObject.IsInstanceValid(_canvasLayer)) {
            _canvasLayer.QueueFree();
            _canvasLayer = null;
        }
    }
}
