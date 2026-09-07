using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using SlayTheStreamer2.Ti.Internal;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2.Game.Ui;

/// <summary>
/// On-screen overlay for the card-reward vote. Renders a centered countdown
/// timer, plus an "#N" / tally pair beside each card and the Skip button.
/// Labels are children of a sibling CanvasLayer; positions are polled each
/// frame from the vanilla NCardHolder / Skip button GlobalPosition so we
/// follow hover-scale tweens without touching vanilla's scene tree.
/// </summary>
internal sealed partial class CardRewardVotePopup : Control {
    public const int LAYER_INDEX = 100;

    // Theming — match the boss-vote conventions.
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

    // Pixel offsets from each anchor control's bounding box.
    //   Cards: index sits above the card top, tally sits below the card bottom.
    //   Skip:  index sits to the left of the button, tally to the right.
    private const float CardIndexAboveY = -190f;
    private const float CardTallyBelowY = 190f;
    private const float SkipIndexLeftX = -45f;
    private const float SkipTallyRightX = 30f;

    // Gap (in px) between the bottom of the vanilla "Choose a Card" banner and
    // the timer label's vertical center. The timer's centered horizontally on
    // the banner regardless of viewport aspect ratio because we poll the
    // banner's GlobalPosition + Size each frame (banner anchors to viewport
    // center, so its absolute position shifts when aspect changes).
    private const float TimerGapBelowBanner = -20f;

    // Gap (in px) between the top of the vanilla "Choose a Card" banner and
    // the title label's vertical center — same banner-polling rationale as
    // the timer gap, just above the banner instead of below.
    private const float TitleGapAboveBanner = 15f;

    // Fallback Y-offset from the viewport top — only used if the banner node
    // can't be resolved at popup-show time (e.g., MegaCrit rename in a future
    // build). Polled banner-anchored positioning is the primary path.
    private const float TimerFallbackOffsetTop = 200f;

    private readonly VoteSession _session;
    private readonly IMainThreadDispatcher _dispatcher;
    private readonly Node _screen;                         // was NCardRewardSelectionScreen
    private readonly IReadOnlyList<Control> _cardHolders;
    private readonly Control? _skipControl;
    private readonly bool _includeSkip;
    private readonly bool _removeMode;
    private readonly Func<bool>? _isRunDying;
    private readonly Func<bool>? _isOccludingOverlayVisible;

    private CanvasLayer? _canvasLayer;
    private Label? _titleLabel;
    private Label? _timerLabel;
    private readonly Control? _bannerAnchor;

    private sealed class OptionLabels {
        public required int VoteIndex;
        public required Control Anchor;
        public required Label Index;
        public required Label Tally;
        public required bool IsSkip;
    }
    private readonly List<OptionLabels> _optionLabels = new();

    private EventHandler<VoteSession>? _closedHandler;
    private EventHandler<VoteSession>? _cancelledHandler;

    private int _cachedSecondsLeft = -1;
    private int _cachedTallyVersion = -1;

    private static readonly Lazy<FieldInfo?> _cardRowField =
        new(() => AccessTools.Field(typeof(NCardRewardSelectionScreen), "_cardRow"));

    /// <summary>Card-reward screen convenience: resolves holders (sorted by X), the Skip
    /// alternative button (first child of UI/RewardAlternatives) and the UI/Banner anchor.</summary>
    public CardRewardVotePopup(
            VoteSession session, IMainThreadDispatcher dispatcher, NCardRewardSelectionScreen screen,
            bool includeSkip, Func<bool>? isRunDying = null, Func<bool>? isOccludingOverlayVisible = null)
        : this(session, dispatcher, screen,
               ResolveCardHolders(screen), TryFindSkipButton(screen), screen.GetNodeOrNull<Control>("UI/Banner"),
               includeSkip, removeMode: false, isRunDying, isOccludingOverlayVisible) { }

    /// <summary>General form, used by both screen families (spec section 4).</summary>
    public CardRewardVotePopup(
            VoteSession session, IMainThreadDispatcher dispatcher, Node screen,
            IReadOnlyList<Control> cardHolders, Control? skipControl, Control? bannerAnchor,
            bool includeSkip, bool removeMode,
            Func<bool>? isRunDying = null, Func<bool>? isOccludingOverlayVisible = null) {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _screen = screen ?? throw new ArgumentNullException(nameof(screen));
        _cardHolders = cardHolders;
        _skipControl = skipControl;
        _bannerAnchor = bannerAnchor;
        _includeSkip = includeSkip;
        _removeMode = removeMode;
        _isRunDying = isRunDying;
        _isOccludingOverlayVisible = isOccludingOverlayVisible;
    }

    private static IReadOnlyList<Control> ResolveCardHolders(NCardRewardSelectionScreen screen) {
        var cardRow = _cardRowField.Value?.GetValue(screen) as Control;
        if (cardRow is null) {
            TiLog.Warn("[SlayTheStreamer2][card-vote-popup] _cardRow field not found on screen; per-card indicators omitted");
            return Array.Empty<Control>();
        }
        // Sort by Position.X so vote indices align with visual left-to-right order
        // (chat expects #1 = leftmost card). Vanilla NGridCardHolder.OnFocus calls
        // MoveToFrontSafely(), which reorders Godot's child list when a card gets
        // focus — without this sort, the focused/clicked card lands at the end of
        // GetChildren() and the labels mis-align. Mirrors GetCurrentHolders in
        // CardRewardVotePatch.cs.
        return cardRow.GetChildren().OfType<NCardHolder>().OrderBy(h => h.Position.X).Cast<Control>().ToList();
    }

    /// <summary>
    /// First child of "UI/RewardAlternatives" is the Skip alternative button per
    /// vanilla <c>CardRewardAlternative.Generate</c> ordering. Returns null if the
    /// container is absent or empty (e.g., CanSkip=false rewards).
    /// </summary>
    private static Control? TryFindSkipButton(NCardRewardSelectionScreen screen) {
        try {
            var container = screen.GetNodeOrNull<Control>("UI/RewardAlternatives");
            if (container is null || container.GetChildCount() == 0) return null;
            return container.GetChild(0) as Control;
        } catch (Exception ex) {
            TiLog.Warn($"[SlayTheStreamer2][card-vote-popup] TryFindSkipButton threw: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Build the CanvasLayer + labels and attach under SceneTree.Root.
    /// Must be called on the main thread.
    /// </summary>
    public void Show() {
        if (_canvasLayer is not null) return;

        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree?.Root is null) {
            TiLog.Warn("[SlayTheStreamer2][card-vote-popup] no SceneTree.Root available; popup not shown");
            return;
        }

        _canvasLayer = new CanvasLayer {
            Name = "CardRewardVotePopupCanvasLayer",
            Layer = LAYER_INDEX,
            ProcessMode = ProcessModeEnum.Always,
        };

        // Tag-aware vote hint mirrors BossVotePopup — ShowTag appends the !NN
        // suffix so YT viewers (who don't see Twitch receipts) can disambiguate
        // against the currently-open vote.
        string voteHint = _session.ShowTag
            ? $"[{_session.VoteId:D2}] — "
            : "";
        string titleBody = _removeMode ? "Chat is choosing which option to remove" : "Pick the worst option.";
        _titleLabel = new Label {
            Name = "Title",
            Text = $"{voteHint}{titleBody}",
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
        _timerLabel.SetAnchorsAndOffsetsPreset(LayoutPreset.CenterTop);
        _timerLabel.OffsetLeft = -250;
        _timerLabel.OffsetRight = 250;
        _timerLabel.OffsetTop = TimerFallbackOffsetTop;
        _timerLabel.OffsetBottom = TimerFallbackOffsetTop + 60;
        ApplyTimerTheme(_timerLabel);
        _canvasLayer.AddChild(_timerLabel);

        // Banner anchor — polled in _Process so the timer follows the vanilla
        // "Choose a Card" banner across aspect-ratio changes. Banner is at
        // UI/Banner with center anchors, so its absolute Y shifts when the
        // viewport size changes; a fixed offset_top would drift visually.
        if (_bannerAnchor is null) {
            TiLog.Warn("[SlayTheStreamer2][card-vote-popup] UI/Banner not found; timer falls back to fixed-Y placement");
        }

        foreach (var (anchor, voteIndex, isSkip) in ResolveAnchors()) {
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
                Anchor = anchor,
                Index = indexLabel,
                Tally = tallyLabel,
                IsSkip = isSkip,
            });
        }

        _closedHandler = (_, _) => _dispatcher.Post(() => { PaintWinnerIfRemoveMode(); SafeQueueFree(); });
        _cancelledHandler = (_, _) => _dispatcher.Post(SafeQueueFree);
        _session.Closed += _closedHandler;
        _session.Cancelled += _cancelledHandler;

        tree.Root.AddChild(_canvasLayer);
        _canvasLayer.AddChild(this);
    }

    /// <summary>
    /// Build the ordered (anchor, voteIndex, isSkip) list. Vote-index #0 is Skip
    /// when <see cref="_includeSkip"/> is true; otherwise #0 is the first card.
    /// </summary>
    private IEnumerable<(Control Anchor, int VoteIndex, bool IsSkip)> ResolveAnchors() {
        var results = new List<(Control, int, bool)>();
        int voteCursor = 0;
        if (_includeSkip) {
            if (_skipControl is not null) results.Add((_skipControl, voteCursor, true));
            else TiLog.Warn("[SlayTheStreamer2][card-vote-popup] no Skip control; #0 indicator omitted");
            voteCursor++;
        }
        foreach (var holder in _cardHolders) { results.Add((holder, voteCursor, false)); voteCursor++; }
        return results;
    }

    private void PaintWinnerIfRemoveMode() {
        if (!_removeMode) return;
        try {
            if (_session.WinnerIndex is not int winner) return;
            foreach (var lbl in _optionLabels) {
                if (lbl.VoteIndex == winner) { RemovalVisuals.PaintRemoved(lbl.Anchor, _screen); return; }
            }
        } catch (Exception ex) { TiLog.Warn($"[SlayTheStreamer2][card-vote-popup] winner paint failed: {ex.Message}"); }
    }

    public override void _Process(double delta) {
        if (_canvasLayer is null) return;
        if (_session.State is VoteSessionState.Closed
                              or VoteSessionState.Cancelled
                              or VoteSessionState.Disposed) return;

        // Run-death — cancel the session so Cancelled fires → SafeQueueFree.
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

        // Anchor the title + timer to the banner's GlobalPosition each frame. The
        // vanilla banner is center-anchored to the viewport, so its absolute Y
        // shifts when the aspect ratio changes — a fixed-offset overlay would
        // drift away from it.
        if (_bannerAnchor is not null && GodotObject.IsInstanceValid(_bannerAnchor)) {
            var bPos = _bannerAnchor.GlobalPosition;
            var bSize = _bannerAnchor.Size * _bannerAnchor.Scale;
            float centerX = bPos.X + bSize.X * 0.5f;
            if (_titleLabel is not null) {
                float titleY = bPos.Y - TitleGapAboveBanner;
                PlaceLabel(_titleLabel, new Vector2(centerX, titleY), halfWidth: 400f, halfHeight: 40f);
            }
            if (_timerLabel is not null) {
                float timerY = bPos.Y + bSize.Y + TimerGapBelowBanner;
                PlaceLabel(_timerLabel, new Vector2(centerX, timerY), halfWidth: 250f, halfHeight: 40f);
            }
        }

        // Re-position labels each frame so hover-scale animations on cards
        // don't desync the overlay. Cost is trivial (≤8 anchors typical).
        foreach (var lbl in _optionLabels) {
            if (!GodotObject.IsInstanceValid(lbl.Anchor)) continue;
            var pos = lbl.Anchor.GlobalPosition;
            var size = lbl.Anchor.Size * lbl.Anchor.Scale;
            if (lbl.IsSkip) {
                float centerY = pos.Y + size.Y * 0.5f;
                PlaceLabel(lbl.Index, new Vector2(pos.X + SkipIndexLeftX, centerY));
                PlaceLabel(lbl.Tally, new Vector2(pos.X + size.X + SkipTallyRightX, centerY));
            } else {
                float centerX = pos.X + size.X * 0.5f;
                PlaceLabel(lbl.Index, new Vector2(centerX, pos.Y + CardIndexAboveY));
                PlaceLabel(lbl.Tally, new Vector2(centerX, pos.Y + size.Y + CardTallyBelowY));
            }
        }

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

    /// <summary>
    /// Centers <paramref name="label"/> on viewport-space point <paramref name="center"/>
    /// using a fixed-size box around it; label's HorizontalAlignment.Center then
    /// centers the text within that box.
    /// </summary>
    private static void PlaceLabel(Label label, Vector2 center, float halfWidth = 120f, float halfHeight = 30f) {
        label.AnchorLeft = 0; label.AnchorTop = 0; label.AnchorRight = 0; label.AnchorBottom = 0;
        label.OffsetLeft = center.X - halfWidth;
        label.OffsetRight = center.X + halfWidth;
        label.OffsetTop = center.Y - halfHeight;
        label.OffsetBottom = center.Y + halfHeight;
    }

    private static void ApplyTitleTheme(Label label) {
        // Mirrors BossVotePopup.ApplyTitleTheme — Kreon Bold, cream body color,
        // soft drop shadow for legibility above the banner.
        var font = ResourceLoader.Load<Font>(TitleFontPath);
        if (font is not null) label.AddThemeFontOverride("font", font);
        label.AddThemeColorOverride("font_color", BodyTextColor);
        label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.5f));
        label.AddThemeConstantOverride("shadow_offset_x", 3);
        label.AddThemeConstantOverride("shadow_offset_y", 2);
        label.AddThemeFontSizeOverride("font_size", TitleFontSize);
    }

    private static void ApplyTimerTheme(Label label) {
        // Mirrors BossVotePopup.ApplyTimerTheme — Kreon glyph_space_one, light blue,
        // faint drop shadow.
        var font = ResourceLoader.Load<Font>(TimerFontPath);
        if (font is not null) label.AddThemeFontOverride("font", font);
        label.AddThemeColorOverride("font_color", TimerColor);
        label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.05f));
        label.AddThemeConstantOverride("shadow_offset_x", 3);
        label.AddThemeConstantOverride("shadow_offset_y", 2);
        label.AddThemeFontSizeOverride("font_size", TimerFontSize);
    }

    private static void ApplyIndexTheme(Label label) {
        var font = ResourceLoader.Load<Font>(FontPath);
        if (font is not null) label.AddThemeFontOverride("font", font);
        label.AddThemeColorOverride("font_color", BodyTextColor);
        label.AddThemeFontSizeOverride("font_size", IndexFontSize);
    }

    private static void ApplyTallyTheme(Label label) {
        var font = ResourceLoader.Load<Font>(FontPath);
        if (font is not null) label.AddThemeFontOverride("font", font);
        label.AddThemeColorOverride("font_color", BodyTextColor);
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
