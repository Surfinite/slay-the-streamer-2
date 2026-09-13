// src/Game/DecisionVotes/RelicOriginTags.cs
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Rewards;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>Which relic (if any) produced a CardReward (spec section 2). A prefix on
/// RelicCmd.Obtain pushes the relic; a HarmonyFinalizer pops it (a finalizer, not a
/// postfix: relic.AssertMutable() at RelicCmd.cs:23 is a real synchronous throw path).
/// __state carries "this call pushed" so the pop is exactly as conditional as the push.
/// Orrery, Glass Eye, Kaleidoscope, Lost Coffer and Strongbox construct their rewards
/// before their first await inside AfterObtained, so the stub's finalizer fires after
/// construction; nested obtains (Neow's Bones then Kaleidoscope) push inside a completed
/// pop. Both CardReward constructors are postfixed and tag the reward with the relic on
/// top of the stack. Local metadata only: catch + log, never throw.</summary>
internal static class RelicOriginTags {
    private static readonly ConditionalWeakTable<CardReward, RelicModel> Tags = new();
    private static readonly Stack<RelicModel> Obtaining = new();

    internal static RelicModel? RelicFor(CardReward reward) => Tags.TryGetValue(reward, out var relic) ? relic : null;

    /// <summary>The relic whose AfterObtained is currently running (innermost), or null.</summary>
    internal static RelicModel? CurrentObtaining => Obtaining.Count > 0 ? Obtaining.Peek() : null;

    private static void TagIfObtaining(CardReward instance) {
        try {
            if (Obtaining.Count > 0) {
                var relic = Obtaining.Peek();
                Tags.AddOrUpdate(instance, relic);
                // Learn the mode-independent fact (Ancient => RemoveOne, else Unskippable). The
                // learned-relics file is write-once, so a verdict that depended on the live
                // setting would outlive a later mode switch; AuthorityLoc upgrades the wording
                // at read time in removeOne mode instead.
                if (RewardAuthority.RulesActive)
                    LocTextPatch.Registry.Learn(relic.Id.Entry, relic.Rarity == MegaCrit.Sts2.Core.Entities.Relics.RelicRarity.Ancient ? AuthorityMode.RemoveOne : AuthorityMode.Unskippable);
                TiLog.Info($"[SlayTheStreamer2][card-scope] tagged relic-origin card reward (relic={relic.Id.Entry}, rarity={relic.Rarity})");
            }
        } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][card-scope] relic-origin tag failed", ex); }
    }

    [HarmonyPatch(typeof(RelicCmd), nameof(RelicCmd.Obtain), typeof(RelicModel), typeof(Player), typeof(int))]
    internal static class ObtainPatch {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        private static void PushPrefix(RelicModel relic, out bool __state) {
            __state = false;
            try { Obtaining.Push(relic); __state = true; }
            catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][card-scope] relic-origin push failed", ex); }
        }

        [HarmonyFinalizer]
        private static Exception? PopFinalizer(Exception? __exception, bool __state) {
            try { if (__state && Obtaining.Count > 0) Obtaining.Pop(); }
            catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][card-scope] relic-origin pop failed", ex); }
            return __exception;
        }
    }

    [HarmonyPatch(typeof(CardReward), MethodType.Constructor,
        new[] { typeof(CardCreationOptions), typeof(int), typeof(Player), typeof(PlayerChoiceSynchronizer) })]
    internal static class CtorAPatch {
        static void Postfix(CardReward __instance) => TagIfObtaining(__instance);
    }

    [HarmonyPatch(typeof(CardReward), MethodType.Constructor,
        new[] { typeof(IEnumerable<CardModel>), typeof(CardCreationSource), typeof(Player), typeof(CardCreationOptions), typeof(PlayerChoiceSynchronizer) })]
    internal static class CtorBPatch {
        static void Postfix(CardReward __instance) => TagIfObtaining(__instance);
    }
}
