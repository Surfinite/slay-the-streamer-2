// src/Game/DecisionVotes/DraftOriginTags.cs
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Modifiers;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>Tags the CardRewards the Draft modifier constructs for its drafting player
/// so the classifier returns Free for them in every mode (the streamer drafts, chat
/// never votes; same rationale as Sealed Deck). Draft.OfferRewards builds each reward
/// after an await, so a push/pop around the method would only catch the first: instead
/// the Func&lt;Task&gt; that GenerateNeowOption returns is wrapped, and the tag is scoped
/// to the specific drafting Player (not a bare counter) for the wrapped task's lifetime.
/// Scoping to the player, rather than a counter, matters because a run abandoned while
/// one of the Draft picks is on screen can leave the wrapped task parked forever on the
/// card-select screen's completion source: its finally never runs, so a plain "is a
/// Draft in progress" counter would stay stuck at 1 for the rest of the process and tag
/// every later reward in every later run as Free. Scoping to the Player reference means
/// an abandoned run's stale marker can never match a fresh run's new Player instance.
/// Every branch fails open (untagged = whatever the mode says). A null EventModel.Owner
/// (never observed; Neow always sets it) skips the wrap and the picks follow the mode.</summary>
internal static class DraftOriginTags {
    private static readonly ConditionalWeakTable<CardReward, object> Tags = new();
    private static readonly object Marker = new();
    private static Player? _draftingFor;

    internal static bool IsTagged(CardReward reward) => Tags.TryGetValue(reward, out _);

    private static void TagIfDrafting(CardReward instance, Player player) {
        try {
            if (_draftingFor is null || !ReferenceEquals(player, _draftingFor)) return;
            Tags.AddOrUpdate(instance, Marker);
            TiLog.Info("[SlayTheStreamer2][card-scope] tagged Draft card reward");
        } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][card-scope] Draft tag failed", ex); }
    }

    [HarmonyPatch(typeof(Draft), nameof(Draft.GenerateNeowOption))]
    internal static class GenerateNeowOptionPatch {
        static void Postfix(EventModel eventModel, ref Func<Task> __result) {
            try {
                var inner = __result;
                if (inner is null) return;
                var player = eventModel?.Owner;
                if (player is null) {
                    TiLog.Warn("[SlayTheStreamer2][card-scope] Draft option has no owning player; picks follow the mode; in removeOne mode the Draft picks would become removal votes");
                    return;
                }
                __result = async () => {
                    _draftingFor = player;
                    try { await inner(); }
                    finally { if (ReferenceEquals(_draftingFor, player)) _draftingFor = null; }
                };
                TiLog.Info("[SlayTheStreamer2][card-scope] Draft option wrapped (Neow option generated); its picks are streamer-only");
            } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][card-scope] Draft wrap failed; picks follow the mode", ex); }
        }
    }

    [HarmonyPatch(typeof(CardReward), MethodType.Constructor,
        new[] { typeof(CardCreationOptions), typeof(int), typeof(Player), typeof(PlayerChoiceSynchronizer) })]
    internal static class CtorAPatch {
        static void Postfix(CardReward __instance, Player player) => TagIfDrafting(__instance, player);
    }

    [HarmonyPatch(typeof(CardReward), MethodType.Constructor,
        new[] { typeof(IEnumerable<CardModel>), typeof(CardCreationSource), typeof(Player), typeof(CardCreationOptions), typeof(PlayerChoiceSynchronizer) })]
    internal static class CtorBPatch {
        static void Postfix(CardReward __instance, Player player) => TagIfDrafting(__instance, player);
    }
}
