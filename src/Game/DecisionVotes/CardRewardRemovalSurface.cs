// src/Game/DecisionVotes/CardRewardRemovalSurface.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Rewards;
using SlayTheStreamer2.Game.Ui;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>IRemovalSurface over NCardRewardSelectionScreen (spec section 3). The record
/// key is the CardReward, so ESC-and-reopen keeps the removal. The options snapshot is
/// the CardCreationResult array: a Driftwood reroll rebuilds it, which invalidates the
/// record and lets the next click start a fresh vote.</summary>
internal sealed class CardRewardRemovalSurface : IRemovalSurface {
    private static readonly Lazy<FieldInfo?> OptionsField = new(() => AccessTools.Field(typeof(NCardRewardSelectionScreen), "_options"));
    private static readonly Lazy<FieldInfo?> CardRowField = new(() => AccessTools.Field(typeof(NCardRewardSelectionScreen), "_cardRow"));
    private static readonly Lazy<FieldInfo?> ExtraOptionsField = new(() => AccessTools.Field(typeof(NCardRewardSelectionScreen), "_extraOptions"));

    private readonly NCardRewardSelectionScreen _screen;
    private readonly CardReward _reward;

    internal CardRewardRemovalSurface(NCardRewardSelectionScreen screen, CardReward reward) { _screen = screen; _reward = reward; }

    internal static CardRewardRemovalSurface? For(NCardRewardSelectionScreen screen) {
        var reward = CombatOriginTags.TryGetActiveReward();
        return reward is null ? null : new CardRewardRemovalSurface(screen, reward);
    }

    public Node ScreenNode => _screen;
    public object RecordKey => _reward;
    public string LogTag => "card-remove";
    public bool HasSkip => SkipAltIndex() is not null;

    internal IReadOnlyList<CardCreationResult> Options() =>
        OptionsField.Value?.GetValue(_screen) as IReadOnlyList<CardCreationResult> ?? Array.Empty<CardCreationResult>();

    public IReadOnlyList<string> CardTitles() => Options().Select(o => o.Card.Title).ToList();

    public IReadOnlyList<Control> CardHolders() {
        if (CardRowField.Value?.GetValue(_screen) is not Node row) return Array.Empty<Control>();
        return row.GetChildren().OfType<NCardHolder>().OrderBy(h => h.Position.X).Cast<Control>().ToList();
    }

    internal IReadOnlyList<CardRewardAlternative> Alternatives() =>
        ExtraOptionsField.Value?.GetValue(_screen) as IReadOnlyList<CardRewardAlternative> ?? Array.Empty<CardRewardAlternative>();

    internal int? SkipAltIndex() {
        var alts = Alternatives();
        for (int i = 0; i < alts.Count; i++) if (alts[i]?.OptionId == "Skip") return i;
        return null;
    }

    internal bool HasReroll() => Alternatives().Any(a => a?.OptionId == "REROLL");

    public Control? SkipControl() {
        try {
            if (SkipAltIndex() is not int i) return null;
            var container = _screen.GetNodeOrNull<Control>("UI/RewardAlternatives");
            return container is not null && i < container.GetChildCount() ? container.GetChild(i) as Control : null;
        } catch { return null; }
    }

    public Control? BannerAnchor() => _screen.GetNodeOrNull<Control>("UI/Banner");

    public object? SnapshotOptions() => Options().ToArray();

    public bool OptionsMatch(object? snapshot) {
        if (snapshot is not CardCreationResult[] snap) return false;
        var current = Options();
        if (current.Count != snap.Length) return false;
        for (int i = 0; i < snap.Length; i++) if (!ReferenceEquals(current[i], snap[i])) return false;
        return true;
    }

    /// <summary>Holder index for a clicked holder, or null.</summary>
    internal int? IndexOf(NCardHolder holder) {
        var holders = CardHolders();
        for (int i = 0; i < holders.Count; i++) if (holders[i] == holder) return i;
        return null;
    }

    /// <summary>Attaches the status line and re-applies an existing record when the
    /// screen (re)opens under RemoveOne. Runs after CombatOriginTags' OnSelect capture
    /// (the screen is instantiated synchronously inside CardReward.OnSelect).</summary>
    [HarmonyPatch(typeof(NCardRewardSelectionScreen), "_Ready")]
    internal static class ReadyPresenterPatch {
        static void Postfix(NCardRewardSelectionScreen __instance) {
            try {
                if (RewardAuthority.ModeOfActiveReward() != AuthorityMode.RemoveOne) return;
                var surface = For(__instance);
                if (surface is null) return;
                RemovalStatusLine.Attach(__instance, surface.BannerAnchor(), () => RemovalVoteFlow.StatusText(surface, surface.HasReroll()));
                var record = RemovalVoteFlow.EffectiveRecord(surface);
                if (record is not null) {
                    // Holders tween into place over 0.5 s on show; paint them next frame so the
                    // holder list is populated and sorted.
                    Callable.From(() => RemovalVoteFlow.ApplyRecordVisuals(surface, record)).CallDeferred();
                }
            } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][card-remove] ready presenter failed", ex); }
        }
    }
}
