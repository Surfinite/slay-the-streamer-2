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

    private CanvasLayer? _canvasLayer;
    private Label[] _tallyLabels = Array.Empty<Label>();
    private bool _userAbandoned;   // Task 11 will set this; declared here so the fields are stable
    private int _cachedTallyVersion = -1;   // -1 sentinel — first poll always updates labels

    private const int CanvasLayerIndex = 100;
    private const float BackdropAlpha = 0.6f;

    public ActVariantVotePopup(
            IReadOnlyList<ActVariantOption> candidates,
            IReadOnlyList<Func<Node>?> factories,
            ActVariantPopupMode mode,
            VoteSession session,
            IMainThreadDispatcher dispatcher,
            Func<bool> shouldCancel,
            Action onUserAbandoned) {
        _options = candidates ?? throw new ArgumentNullException(nameof(candidates));
        _factories = factories ?? throw new ArgumentNullException(nameof(factories));
        if (factories.Count != candidates.Count)
            throw new ArgumentException("factories must be parallel to candidates", nameof(factories));
        _mode = mode;
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _shouldCancel = shouldCancel ?? throw new ArgumentNullException(nameof(shouldCancel));
        _onUserAbandoned = onUserAbandoned ?? throw new ArgumentNullException(nameof(onUserAbandoned));
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
            _session.Closed += OnClosed;
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

        _tallyLabels = new Label[_options.Count];

        for (int i = 0; i < _options.Count; i++) {
            var column = BuildColumn(_options[i], _factories[i], i);
            hbox.AddChild(column);
        }

        return layer;
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
                    if (visual is Control ctrl) {
                        ctrl.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
                    }
                    free.AddChild(visual);
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

        // Tally label — text is updated by _Process tally-version polling.
        var tally = new Label {
            Text = $"#{option.Index} — 0 votes",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        tally.SetAnchorsAndOffsetsPreset(LayoutPreset.CenterBottom);
        tally.OffsetTop = -80;
        tally.OffsetLeft = -150;
        tally.OffsetRight = 150;
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

        var title = new Label {
            Text = option.Title,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        title.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
        title.OffsetLeft = -200;
        title.OffsetRight = 200;
        title.OffsetTop = -30;
        title.OffsetBottom = 30;
        parent.AddChild(title);
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
                    _tallyLabels[i].Text = $"#{_options[i].Index} — {count} votes";
                }
            } catch (Exception ex) {
                TiLog.Warn($"[SlayTheStreamer2][act-variant-vote] tally update threw: {ex.Message}");
            }
        }
    }

    public override void _Input(InputEvent @event) {
        // _Input fires BEFORE _UnhandledInput — guarantees popup gets ESC even
        // if a parent control would have consumed it. Per v3 spec S5.
        if (_userAbandoned) return;
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

    private void OnClosed(object? sender, VoteSession session) {
        try {
            _session.Closed -= OnClosed;
        } catch { /* swallow */ }
        if (_canvasLayer is not null && GodotObject.IsInstanceValid(_canvasLayer)) {
            _canvasLayer.QueueFree();
            _canvasLayer = null;
        }
    }
}
