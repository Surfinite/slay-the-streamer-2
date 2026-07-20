using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using SlayTheStreamer2.Game.Bootstrap;
using SlayTheStreamer2.Game.DecisionVotes;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.Rewards;

/// <summary>
/// Bossy Relics, elite surface: when a combat RewardsSet contains the standard
/// single elite RelicReward, replace it with vanilla's (dormant) LinkedRewardSet
/// containing the original + N-1 fixed-rarity extras. Vanilla renders the
/// chain-link UI and enforces claim-one; we refund unclaimed members to the
/// back of the relic deques.
///
/// Patch point is WithRewardsFromRoom (not GenerateRewardsFor): it runs before
/// Populate, and the tutorial path (TryGenerateTutorialRewards) produces
/// PREDETERMINED relic rewards that are already populated - the unpopulated
/// filter below therefore skips tutorials for free. (Edge case: the tutorial
/// path can fall back to a plain unpopulated RelicReward when Vajra/OrnamentalFan
/// is unavailable - the offer then expands there too, which is harmless.)
/// </summary>
[HarmonyPatch(typeof(RewardsSet), nameof(RewardsSet.WithRewardsFromRoom))]
internal static class EliteRelicChoicePatch {
    /// <summary>Members of linked sets THIS mod created, pending refund.
    /// Instance-identity registry; entries removed as refunds fire.</summary>
    internal static readonly HashSet<Reward> Registry = new();
    /// <summary>Wrappers this mod created that still need completion bookkeeping.</summary>
    internal static readonly HashSet<LinkedRewardSet> PendingWrappers = new();

    static void Postfix(RewardsSet __instance, AbstractRoom room) {
        try {
            int choices = RelicChoicePlanner.Clamp(ModSettings.Current?.RelicChoices ?? 1);
            if (choices <= 1) return;
            var player = __instance.Player;
            if (player.RunState.Players.Count != 1) return;

            // Exactly the vanilla elite shape: one rarity-less, unpopulated
            // RelicReward directly in the set (never inside ExtraRewards - those
            // were appended from CombatRoom.ExtraRewards and are serialized).
            var candidates = __instance.Rewards
                .Where(r => r is RelicReward rr && !rr.IsPopulated && rr.Rarity == MegaCrit.Sts2.Core.Entities.Relics.RelicRarity.None)
                .Cast<RelicReward>()
                .ToList();
            if (candidates.Count != 1) return;
            var original = candidates[0];
            if (room is CombatRoom combat && combat.ExtraRewards.TryGetValue(player, out var extras) && extras.Contains(original)) return;

            int extraCount = RelicChoicePlanner.ExtraCount(choices, 1, RelicChoicePlanner.MaxChoices);
            var rng = SeedCompat.CreateRng(RelicChoicePlanner.OfferSeed(
                player.RunState.Rng?.StringSeed, "bossy-elite",
                player.RunState.CurrentActIndex, player.RunState.TotalFloor));

            var members = new List<Reward> { original };
            for (int i = 0; i < extraCount; i++) {
                // Fixed-rarity ctor consumes no vanilla RNG; rarity from OUR rng.
                members.Add(new RelicReward(RelicFactory.RollRarity(rng), player));
            }

            var wrapper = new LinkedRewardSet(members, player);
            int idx = __instance.Rewards.IndexOf(original);
            __instance.Rewards[idx] = wrapper;

            foreach (var m in members) Registry.Add(m);
            PendingWrappers.Add(wrapper);
            TiLog.Info($"[bossy-relics] elite offer expanded to {members.Count} linked relics (act {player.RunState.CurrentActIndex}, floor {player.RunState.TotalFloor})");
        } catch (System.Exception ex) {
            TiLog.Error("[bossy-relics] EliteRelicChoicePatch failed; vanilla rewards unchanged", ex);
        }
    }
}

/// <summary>
/// When a child of OUR linked set is claimed, vanilla calls
/// LinkedRewardSet.RemoveReward(child). Complete the wrapper's bookkeeping
/// (vanilla never sets its SuccessfullySelected via the UI path - dormant-code
/// wart) so RewardsSet completion fires cleanly instead of Log.Error/hang.
/// </summary>
[HarmonyPatch(typeof(LinkedRewardSet), nameof(LinkedRewardSet.RemoveReward))]
internal static class LinkedSetClaimBookkeepingPatch {
    static void Postfix(LinkedRewardSet __instance, Reward reward) {
        try {
            if (!EliteRelicChoicePatch.PendingWrappers.Remove(__instance)) return;
            EliteRelicChoicePatch.Registry.Remove(reward);   // claimed member: no refund
            // Wrapper's own OnSelect trivially returns true; selecting it marks
            // SuccessfullySelected so the RewardsSet can complete. The chain
            // resolves synchronously TODAY (v0.109: Hook.AfterRewardTaken has no
            // awaiting overrides - verified against the decompile); if a future
            // game version makes it truly async, the completion check may run
            // before the wrapper is marked and the set won't complete - re-check
            // this on game-update compat passes. The continuation makes any
            // async fault observable instead of silently discarded.
            __instance.SelectUnsynchronized().ContinueWith(
                t => TiLog.Error("[bossy-relics] wrapper SelectUnsynchronized faulted", t.Exception),
                System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
        } catch (System.Exception ex) {
            TiLog.Error("[bossy-relics] linked-set bookkeeping failed", ex);
        }
    }
}

/// <summary>
/// Refund path: NLinkedRewardSet.GetReward calls OnSkipped() on the remaining
/// members after a claim, and skipping the whole screen routes
/// LinkedRewardSet.OnSkipped -> each member's OnSkipped. Either way, a member
/// of OUR set that gets skipped goes back to the pool. Registry.Remove makes
/// the (known) double-OnSkipped harmless.
/// </summary>
[HarmonyPatch(typeof(RelicReward), nameof(RelicReward.OnSkipped))]
internal static class LinkedSetRefundPatch {
    static void Postfix(RelicReward __instance) {
        try {
            if (!EliteRelicChoicePatch.Registry.Remove(__instance)) return;
            if (__instance.Relic is null) return;   // never populated - nothing was pulled
            RelicReturnHelper.ReturnToPools(__instance.Player, __instance.Relic, sharedBagOnly: false);
        } catch (System.Exception ex) {
            TiLog.Error("[bossy-relics] elite refund failed", ex);
        }
    }
}
