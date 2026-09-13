// src/Game/DecisionVotes/DraftOriginTags.cs
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
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

/// <summary>Tags the ten CardRewards the Draft modifier constructs so the classifier
/// returns Free for them in every mode (the streamer drafts, chat never votes; same
/// rationale as Sealed Deck). Draft.OfferRewards builds each reward after an await, so
/// a push/pop around the method would only catch the first: instead the Func&lt;Task&gt;
/// that GenerateNeowOption returns is wrapped, and the flag stays up until that task
/// completes. Every branch fails open (untagged = whatever the mode says).</summary>
internal static class DraftOriginTags {
    private static readonly ConditionalWeakTable<CardReward, object> Tags = new();
    private static readonly object Marker = new();
    private static int _active;

    internal static bool IsTagged(CardReward reward) => Tags.TryGetValue(reward, out _);

    private static void TagIfDrafting(CardReward instance) {
        try {
            if (Volatile.Read(ref _active) == 0) return;
            Tags.AddOrUpdate(instance, Marker);
            TiLog.Info("[SlayTheStreamer2][card-scope] tagged Draft card reward");
        } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][card-scope] Draft tag failed", ex); }
    }

    [HarmonyPatch(typeof(Draft), nameof(Draft.GenerateNeowOption))]
    internal static class GenerateNeowOptionPatch {
        static void Postfix(ref Func<Task> __result) {
            try {
                var inner = __result;
                if (inner is null) return;
                __result = async () => {
                    Interlocked.Increment(ref _active);
                    try { await inner(); }
                    finally { Interlocked.Decrement(ref _active); }
                };
                TiLog.Info("[SlayTheStreamer2][card-scope] Draft option wrapped; its picks are streamer-only");
            } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][card-scope] Draft wrap failed; picks follow the mode", ex); }
        }
    }

    [HarmonyPatch(typeof(CardReward), MethodType.Constructor,
        new[] { typeof(CardCreationOptions), typeof(int), typeof(Player), typeof(PlayerChoiceSynchronizer) })]
    internal static class CtorAPatch {
        static void Postfix(CardReward __instance) => TagIfDrafting(__instance);
    }

    [HarmonyPatch(typeof(CardReward), MethodType.Constructor,
        new[] { typeof(IEnumerable<CardModel>), typeof(CardCreationSource), typeof(Player), typeof(CardCreationOptions), typeof(PlayerChoiceSynchronizer) })]
    internal static class CtorBPatch {
        static void Postfix(CardReward __instance) => TagIfDrafting(__instance);
    }
}
