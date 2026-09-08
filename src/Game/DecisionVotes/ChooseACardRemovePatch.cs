// src/Game/DecisionVotes/ChooseACardRemovePatch.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Runs;
using SlayTheStreamer2.Game.Ui;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>Spec section 4: the removal vote on NChooseACardSelectionScreen (Hefty
/// Tablet, Lead Paperweight, any mod relic using CardSelectCmd.FromChooseACardScreen
/// out of combat with 3+ options counting Skip). A prefix on FromChooseACardScreen opens
/// a context; the static ShowScreen postfix binds the screen when its _cards list
/// reference-equals the context's; prefixes on SelectHolder and OnSkipButtonReleased
/// run the same click state machine as the card-reward screen. Inside vanilla's 350 ms
/// open debounce the prefixes return true so vanilla drops the click itself.</summary>
[HarmonyPatch]
internal static class ChooseACardRemovePatch {
    private static readonly Lazy<FieldInfo?> CardsField = new(() => AccessTools.Field(typeof(NChooseACardSelectionScreen), "_cards"));
    private static readonly Lazy<FieldInfo?> CardRowField = new(() => AccessTools.Field(typeof(NChooseACardSelectionScreen), "_cardRow"));
    private static readonly Lazy<FieldInfo?> SkipField = new(() => AccessTools.Field(typeof(NChooseACardSelectionScreen), "_skipButton"));
    private static readonly Lazy<FieldInfo?> OpenedField = new(() => AccessTools.Field(typeof(NChooseACardSelectionScreen), "_openedTicks"));
    private const ulong DebounceMsec = 350;

    private static int _voteInProgress;
    internal static bool VoteInProgress => _voteInProgress == 1;

    /// <summary>Id of the relic whose obtain opened the current context (learner input).</summary>
    internal static string? LastOpenedRelicId { get; private set; }

    private sealed class Context {
        public required IReadOnlyList<CardModel> Cards;
        public required bool CanSkip;
        public NChooseACardSelectionScreen? Screen;
    }
    private static Context? _current;

    /// <summary>IRemovalSurface over the bound screen. Option list never changes, so the
    /// snapshot is the context itself.</summary>
    private sealed class Surface : IRemovalSurface {
        private readonly Context _ctx;
        private readonly NChooseACardSelectionScreen _screen;
        public Surface(Context ctx, NChooseACardSelectionScreen screen) { _ctx = ctx; _screen = screen; }
        public Node ScreenNode => _screen;
        public object RecordKey => _ctx;
        public string LogTag => "choose-remove";
        public bool HasSkip => _ctx.CanSkip;
        public IReadOnlyList<string> CardTitles() => _ctx.Cards.Select(c => c.Title).ToList();
        public IReadOnlyList<Control> CardHolders() =>
            CardRowField.Value?.GetValue(_screen) is Node row
                ? row.GetChildren().OfType<NCardHolder>().OrderBy(h => h.Position.X).Cast<Control>().ToList()
                : new List<Control>();
        public Control? SkipControl() => SkipField.Value?.GetValue(_screen) as Control;
        public Control? BannerAnchor() => _screen.GetNodeOrNull<Control>("Banner") ?? _screen.GetNodeOrNull<Control>("UI/Banner");
        public object? SnapshotOptions() => _ctx;
        public bool OptionsMatch(object? snapshot) => ReferenceEquals(snapshot, _ctx);
        internal int? IndexOf(NCardHolder holder) {
            for (int i = 0; i < _ctx.Cards.Count; i++) if (ReferenceEquals(_ctx.Cards[i], holder.CardModel)) return i;
            return null;
        }
    }

    private static Surface? BoundSurface(NChooseACardSelectionScreen screen) =>
        _current is { Screen: { } s } ctx && ReferenceEquals(s, screen) ? new Surface(ctx, screen) : null;

    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromChooseACardScreen))]
    [HarmonyPrefix]
    private static void OpenPrefix(IReadOnlyList<CardModel> cards, Player player, bool canSkip) {
        try {
            if (!RewardAuthority.RulesActive) return;
            if (CombatManager.Instance?.IsInProgress ?? false) return;             // mid-combat pickers stay the streamer's
            if (RunManager.Instance?.DebugOnlyGetState()?.Players?.Count is int n && n > 1) return;
            if (cards.Count + (canSkip ? 1 : 0) < 3) return;                        // N >= 3 counting Skip
            if (_current is not null) {
                RemovalRecords.Clear(_current);
                Interlocked.Exchange(ref _voteInProgress, 0);
                TiLog.Warn("[SlayTheStreamer2][choose-remove] a previous choose-a-card context was still open; replaced");
            }
            _current = new Context { Cards = cards, CanSkip = canSkip };
            LastOpenedRelicId = RelicOriginTags.CurrentObtaining?.Id.Entry;
            if (LastOpenedRelicId is { } rid) LocTextPatch.Registry.Learn(rid, AuthorityMode.RemoveOne);
            TiLog.Info($"[SlayTheStreamer2][choose-remove] context open cards={cards.Count} canSkip={canSkip} relic={LastOpenedRelicId ?? "none"}");
        } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][choose-remove] open prefix failed", ex); }
    }

    [HarmonyPatch(typeof(NChooseACardSelectionScreen), nameof(NChooseACardSelectionScreen.ShowScreen))]
    [HarmonyPostfix]
    private static void ShowScreenPostfix(NChooseACardSelectionScreen? __result) {
        try {
            if (__result is null || _current is null) return;
            if (!ReferenceEquals(CardsField.Value?.GetValue(__result), _current.Cards)) return;   // combat pickers never match
            _current.Screen = __result;
            var surface = new Surface(_current, __result);
            RemovalStatusLine.Attach(__result, surface.BannerAnchor(), () => RemovalVoteFlow.StatusText(surface, hasReroll: false));
            TiLog.Info("[SlayTheStreamer2][choose-remove] screen bound");
        } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][choose-remove] screen bind failed", ex); }
    }

    [HarmonyPatch(typeof(NChooseACardSelectionScreen), nameof(NChooseACardSelectionScreen._ExitTree))]
    [HarmonyPostfix]
    private static void ExitTreePostfix(NChooseACardSelectionScreen __instance) {
        try {
            if (_current is { Screen: { } s } && ReferenceEquals(s, __instance)) {
                RemovalRecords.Clear(_current);
                _current = null;
                Interlocked.Exchange(ref _voteInProgress, 0);
            }
        } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][choose-remove] exit-tree cleanup failed", ex); }
    }

    private static bool InDebounce(NChooseACardSelectionScreen s) =>
        OpenedField.Value?.GetValue(s) is ulong t && Time.GetTicksMsec() - t <= DebounceMsec;

    [HarmonyPatch(typeof(NChooseACardSelectionScreen), "SelectHolder")]
    [HarmonyPrefix]
    private static bool SelectHolderPrefix(NChooseACardSelectionScreen __instance, NCardHolder cardHolder) {
        try {
            if (!GodotObject.IsInstanceValid(__instance) || !GodotObject.IsInstanceValid(cardHolder)) return true;
            var surface = BoundSurface(__instance);
            if (surface is null) return true;
            if (InDebounce(__instance)) return true;                                // vanilla drops it; never latch here
            int? clicked = surface.IndexOf(cardHolder);
            if (clicked is null) return true;                                       // unresolvable holder: vanilla
            return Handle(surface, clicked.Value, _current!.Cards[clicked.Value].Title);
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][choose-remove] SelectHolder prefix threw; vanilla proceeds", ex);
            return true;
        }
    }

    [HarmonyPatch(typeof(NChooseACardSelectionScreen), "OnSkipButtonReleased")]
    [HarmonyPrefix]
    private static bool SkipPrefix(NChooseACardSelectionScreen __instance) {
        try {
            if (!GodotObject.IsInstanceValid(__instance)) return true;
            var surface = BoundSurface(__instance);
            if (surface is null) return true;
            return Handle(surface, RemovalClickRules.SkipIndex, CardRewardOptionLabels.SkipLabel);
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][choose-remove] OnSkipButtonReleased prefix threw; vanilla proceeds", ex);
            return true;
        }
    }

    /// <summary>Same state machine as the card-reward screen: click-to-start, override
    /// during the vote, verdicts after the removal. Returns the prefix result.</summary>
    private static bool Handle(Surface surface, int clicked, string label) {
        try {
            if (_voteInProgress == 1) {
                if (RemovalVoteFlow.TryOverrideDuringVote(label)) { Interlocked.Exchange(ref _voteInProgress, 0); return true; }
                TiLog.Debug("[SlayTheStreamer2][choose-remove] click during removal vote; suppressed");
                return false;
            }
            var record = RemovalVoteFlow.EffectiveRecord(surface);
            if (record is null) {
                if (Interlocked.CompareExchange(ref _voteInProgress, 1, 0) != 0) return false;
                if (!RemovalVoteFlow.TryStart(surface, () => Interlocked.Exchange(ref _voteInProgress, 0))) {
                    Interlocked.Exchange(ref _voteInProgress, 0);
                    return true;
                }
                return false;
            }
            return CardRewardVotePatch.JudgeRemovalClick(clicked, record, label, "choose-remove");
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][choose-remove] click handling threw; vanilla proceeds", ex);
            Interlocked.Exchange(ref _voteInProgress, 0);
            return true;
        }
    }
}
