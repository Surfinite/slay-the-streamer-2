using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using SlayTheStreamer2.Ti.Internal;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2.Game.Ui;

/// <summary>
/// On-screen overlay for the ancient-event vote. Renders a centered title +
/// countdown timer above the dialogue speech bubble, plus an "#N" / tally
/// pair flanking each option button. Labels live on a sibling CanvasLayer
/// and their positions are polled per-frame from the vanilla DialogueContainer
/// and OptionButtons' GlobalPosition so layout drift (animations, aspect-ratio
/// changes) doesn't desync the overlay. Mirrors CardRewardVotePopup's pattern.
/// </summary>
internal sealed partial class AncientVotePopup : Control {
    public const int LAYER_INDEX = 100;

    private const string FontPath = "res://themes/kreon_regular_shared.tres";
    private const string TitleFontPath = "res://themes/kreon_bold_shared.tres";
    private const string TimerFontPath = "res://themes/kreon_regular_glyph_space_one.tres";
    private static readonly Color TimerColor = new(0.529f, 0.808f, 0.921f, 1f);
    private static readonly Color BodyTextColor = new(1f, 0.964706f, 0.886275f, 1f);

    // Sizing — initial values; tune in place.
    private const int TitleFontSize = 40;
    private const int IndexFontSize = 36;
    private const int TallyFontSize = 34;
    private const int TimerFontSize = 40;

    // Per-anchor pixel offsets.
    //   Title above timer above dialogue container's top edge.
    //   Option #N to the left of the button; tally to the right.
    private const float TitleGapAboveDialogue = 120f;
    private const float TimerGapAboveDialogue = 50f;
    private const float OptionIndexGapLeft    = 25f;   // px left of button's left edge
    private const float OptionTallyGapRight   = 15f;   // px right of button's right edge

    private readonly VoteSession _session;
    private readonly IMainThreadDispatcher _dispatcher;
    private readonly NEventRoom _room;
    private readonly Func<bool>? _isRunDying;
    private readonly Func<bool>? _isOccludingOverlayVisible;

    private CanvasLayer? _canvasLayer;
    private Label? _titleLabel;
    private Label? _timerLabel;
    private Control? _dialogueAnchor;

    private sealed class OptionLabels {
        public required int VoteIndex;
        public required Control Anchor;
        public required Label Index;
        public required Label Tally;
    }
    private readonly List<OptionLabels> _optionLabels = new();

    private EventHandler<VoteSession>? _closedHandler;
    private EventHandler<VoteSession>? _cancelledHandler;

    private int _cachedSecondsLeft = -1;
    private int _cachedTallyVersion = -1;

    public AncientVotePopup(
            VoteSession session,
            IMainThreadDispatcher dispatcher,
            NEventRoom room,
            Func<bool>? isRunDying = null,
            Func<bool>? isOccludingOverlayVisible = null) {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _room = room ?? throw new ArgumentNullException(nameof(room));
        _isRunDying = isRunDying;
        _isOccludingOverlayVisible = isOccludingOverlayVisible;
    }

    /// <summary>
    /// Build the CanvasLayer + labels and attach under SceneTree.Root.
    /// Must be called on the main thread.
    /// </summary>
    public void Show() {
        if (_canvasLayer is not null) return;

        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree?.Root is null) {
            TiLog.Warn("[SlayTheStreamer2][ancient-vote-popup] no SceneTree.Root available; popup not shown");
            return;
        }
        if (_room.Layout is not NEventLayout layout) {
            TiLog.Warn("[SlayTheStreamer2][ancient-vote-popup] room.Layout is null; popup not shown");
            return;
        }

        _canvasLayer = new CanvasLayer {
            Name = "AncientVotePopupCanvasLayer",
            Layer = LAYER_INDEX,
            ProcessMode = ProcessModeEnum.Always,
        };

        // ShowTag prefixes the title with the live vote ID so YT viewers (who
        // don't see Twitch receipts) can disambiguate against the open vote.
        // "Type #N" instruction dropped after moderator feedback — see the
        // matching comment in BossVotePopup.Show().
        string voteHint = _session.ShowTag
            ? $"[{_session.VoteId:D2}] — "
            : "";
        _titleLabel = new Label {
            Name = "Title",
            Text = $"{voteHint}Pick the least useful ancient relic.",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        ApplyTitleTheme(_titleLabel);
        _canvasLayer.AddChild(_titleLabel);

        _timerLabel = new Label {
            Name = "Timer",
            Text = $"{Math.Max(0, (int)_session.TimeRemaining.TotalSeconds)}s left",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        ApplyTimerTheme(_timerLabel);
        _canvasLayer.AddChild(_timerLabel);

        // Dialogue (speech bubble) anchor — title and timer follow its top edge.
        // Ancient layout uses %DialogueContainer; non-ancient layouts use
        // %EventDescription. Fall back to either so a future build that retags
        // the node degrades to "no dialogue anchor" rather than crashing.
        _dialogueAnchor = layout.GetNodeOrNull<Control>("%DialogueContainer")
                       ?? layout.GetNodeOrNull<Control>("%EventDescription");
        if (_dialogueAnchor is null) {
            TiLog.Warn("[SlayTheStreamer2][ancient-vote-popup] %DialogueContainer / %EventDescription not found; title+timer positioning fall back to viewport-relative");
        }

        // Option buttons — vote indices map 1:1 to OptionButtons in order.
        int voteIndex = 0;
        foreach (var btn in layout.OptionButtons) {
            var indexLabel = new Label {
                Name = $"Index{voteIndex}",
                Text = $"#{voteIndex}",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            ApplyIndexTheme(indexLabel);
            _canvasLayer.AddChild(indexLabel);

            var tallyLabel = new Label {
                Name = $"Tally{voteIndex}",
                Text = "0",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            ApplyTallyTheme(tallyLabel);
            _canvasLayer.AddChild(tallyLabel);

            _optionLabels.Add(new OptionLabels {
                VoteIndex = voteIndex,
                Anchor = btn,
                Index = indexLabel,
                Tally = tallyLabel,
            });
            voteIndex++;
        }

        _closedHandler = (_, _) => _dispatcher.Post(SafeQueueFree);
        _cancelledHandler = (_, _) => _dispatcher.Post(SafeQueueFree);
        _session.Closed += _closedHandler;
        _session.Cancelled += _cancelledHandler;

        tree.Root.AddChild(_canvasLayer);
        _canvasLayer.AddChild(this);
    }

    public override void _Process(double delta) {
        if (_canvasLayer is null) return;
        if (_session.State is VoteSessionState.Closed
                              or VoteSessionState.Cancelled
                              or VoteSessionState.Disposed) return;

        // Run-death — cancel session so Cancelled fires → SafeQueueFree.
        bool runDying = false;
        try { runDying = _isRunDying?.Invoke() ?? false; } catch { /* swallow */ }
        if (runDying) {
            try { _session.Cancel(); } catch { /* swallow */ }
            return;
        }

        // Yield the screen to an occluding overlay (pause menu, dev console,
        // settings submenu). Vote machinery keeps running in the background;
        // only the visual is hidden. Mirrors BossVotePopup's behaviour.
        bool occluded = false;
        try { occluded = _isOccludingOverlayVisible?.Invoke() ?? false; }
        catch { /* probe must never crash _Process */ }
        if (_canvasLayer.Visible == occluded) _canvasLayer.Visible = !occluded;
        if (occluded) return;   // skip the per-frame position + tally work

        // Anchor title + timer to the dialogue container's top edge, centered on
        // the dialogue container horizontally.
        if (_dialogueAnchor is not null && GodotObject.IsInstanceValid(_dialogueAnchor)) {
            var dPos = _dialogueAnchor.GlobalPosition;
            var dSize = _dialogueAnchor.Size * _dialogueAnchor.Scale;
            float centerX = dPos.X + dSize.X * 0.5f;
            if (_titleLabel is not null) {
                PlaceLabel(_titleLabel,
                    new Vector2(centerX, dPos.Y - TitleGapAboveDialogue),
                    halfWidth: 400f, halfHeight: 40f);
            }
            if (_timerLabel is not null) {
                PlaceLabel(_timerLabel,
                    new Vector2(centerX, dPos.Y - TimerGapAboveDialogue),
                    halfWidth: 250f, halfHeight: 40f);
            }
        }

        // Anchor #N / tally to each option button — index to the left, tally to
        // the right, both vertically centered on the button.
        foreach (var lbl in _optionLabels) {
            if (!GodotObject.IsInstanceValid(lbl.Anchor)) continue;
            var bPos = lbl.Anchor.GlobalPosition;
            var bSize = lbl.Anchor.Size * lbl.Anchor.Scale;
            float centerY = bPos.Y + bSize.Y * 0.5f;
            PlaceLabel(lbl.Index,
                new Vector2(bPos.X - OptionIndexGapLeft, centerY),
                halfWidth: 50f, halfHeight: 30f);
            PlaceLabel(lbl.Tally,
                new Vector2(bPos.X + bSize.X + OptionTallyGapRight, centerY),
                halfWidth: 80f, halfHeight: 30f);
        }

        // Update text only when tally / timer values actually change.
        int secondsLeft = Math.Max(0, (int)_session.TimeRemaining.TotalSeconds);
        int tallyVersion = _session.TallyVersion;
        if (secondsLeft == _cachedSecondsLeft && tallyVersion == _cachedTallyVersion) return;

        if (secondsLeft != _cachedSecondsLeft && _timerLabel is not null) {
            _cachedSecondsLeft = secondsLeft;
            _timerLabel.Text = $"{secondsLeft}s left";
        }
        if (tallyVersion != _cachedTallyVersion) {
            _cachedTallyVersion = tallyVersion;
            foreach (var lbl in _optionLabels) {
                _session.Tallies.TryGetValue(lbl.VoteIndex, out int count);
                lbl.Tally.Text = count.ToString();
            }
        }
    }

    private static void PlaceLabel(Label label, Vector2 center, float halfWidth = 120f, float halfHeight = 30f) {
        label.AnchorLeft = 0; label.AnchorTop = 0; label.AnchorRight = 0; label.AnchorBottom = 0;
        label.OffsetLeft = center.X - halfWidth;
        label.OffsetRight = center.X + halfWidth;
        label.OffsetTop = center.Y - halfHeight;
        label.OffsetBottom = center.Y + halfHeight;
    }

    // Dark warm outline matching vanilla settings labels / VAKUU ancient-name banner.
    // Saves readability against high-luma backgrounds like the Neow cyan scene where
    // both the cream title and the light-blue timer would otherwise wash out.
    private static readonly Color TextOutlineColor = new(0.29f, 0.235f, 0.165f, 0.75f);
    private const int TextOutlineSize = 12;

    private static void ApplyTitleTheme(Label label) {
        var font = ResourceLoader.Load<Font>(TitleFontPath);
        if (font is not null) label.AddThemeFontOverride("font", font);
        label.AddThemeColorOverride("font_color", BodyTextColor);
        label.AddThemeColorOverride("font_outline_color", TextOutlineColor);
        label.AddThemeConstantOverride("outline_size", TextOutlineSize);
        label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.5f));
        label.AddThemeConstantOverride("shadow_offset_x", 3);
        label.AddThemeConstantOverride("shadow_offset_y", 2);
        label.AddThemeFontSizeOverride("font_size", TitleFontSize);
    }

    private static void ApplyTimerTheme(Label label) {
        var font = ResourceLoader.Load<Font>(TimerFontPath);
        if (font is not null) label.AddThemeFontOverride("font", font);
        label.AddThemeColorOverride("font_color", TimerColor);
        label.AddThemeColorOverride("font_outline_color", TextOutlineColor);
        label.AddThemeConstantOverride("outline_size", TextOutlineSize);
        label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.05f));
        label.AddThemeConstantOverride("shadow_offset_x", 3);
        label.AddThemeConstantOverride("shadow_offset_y", 2);
        label.AddThemeFontSizeOverride("font_size", TimerFontSize);
    }

    private static void ApplyIndexTheme(Label label) {
        var font = ResourceLoader.Load<Font>(FontPath);
        if (font is not null) label.AddThemeFontOverride("font", font);
        label.AddThemeColorOverride("font_color", BodyTextColor);
        label.AddThemeColorOverride("font_outline_color", TextOutlineColor);
        label.AddThemeConstantOverride("outline_size", TextOutlineSize);
        label.AddThemeFontSizeOverride("font_size", IndexFontSize);
    }

    private static void ApplyTallyTheme(Label label) {
        var font = ResourceLoader.Load<Font>(FontPath);
        if (font is not null) label.AddThemeFontOverride("font", font);
        label.AddThemeColorOverride("font_color", BodyTextColor);
        label.AddThemeColorOverride("font_outline_color", TextOutlineColor);
        label.AddThemeConstantOverride("outline_size", TextOutlineSize);
        label.AddThemeFontSizeOverride("font_size", TallyFontSize);
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
