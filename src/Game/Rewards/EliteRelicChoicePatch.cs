using System.Collections.Generic;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
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

/// <summary>
/// Vanilla's NLinkedRewardSet ships two arity bugs that make its claim-one
/// UI dormant/never-exercised code (this mod is the only thing that ever
/// constructs a LinkedRewardSet today):
///
/// 1. NLinkedRewardSet.Reload (decompiled v0.109, line 123) wires each child
///    NRewardButton's RewardClaimed signal to `Callable.From((Action)GetReward)`
///    - a ZERO-arg callable. But NRewardButton.GetReward (NRewardButton.cs
///    line 210) emits RewardClaimed WITH ONE argument (the button itself).
///    The C# trampoline throws `ArgumentException: Invalid argument count
///    for invoking callable. Expected 0 argument(s), received 1` the instant
///    a chained relic is claimed, so NLinkedRewardSet.GetReward() - which
///    would remove the whole group from the screen - never runs. Symptom:
///    claiming one relic of a linked pair grants it but leaves BOTH visible
///    and claimable.
/// 2. Even if bug 1 didn't throw, NLinkedRewardSet.GetReward's own self-emit
///    (line 141) does `EmitSignal(SignalName.RewardClaimed, Array.Empty
///    <Variant>())` - ZERO args - against its own signal declared with ONE
///    parameter (`RewardClaimedEventHandler(NLinkedRewardSet
///    linkedRewardSet)`), which NRewardsScreen subscribes to with a 1-arg
///    handler (NRewardsScreen.cs line 312). That mismatch would throw too,
///    aborting before the trailing QueueFreeSafely().
///
/// Rather than patch around GetReward's second bug, this postfix rewires the
/// button connections Reload just made: disconnect vanilla's broken 0-arg
/// hookup and reconnect a correct-arity replacement that performs GetReward's
/// intended effect directly (screen removal + skip-remaining + free), skipping
/// the doubly-broken self-emit entirely (nothing outside this mod listens for
/// NLinkedRewardSet.RewardClaimed, so not re-emitting it is harmless).
///
/// This rewire is unconditional for any NLinkedRewardSet, not gated to sets
/// this mod created - only this mod constructs them today. If MegaCrit ever
/// starts using LinkedRewardSet natively (and fixes the arity bugs), revisit.
/// </summary>
[HarmonyPatch(typeof(NLinkedRewardSet), "Reload")]
internal static class LinkedSetSignalRewirePatch {
    private static readonly AccessTools.FieldRef<NLinkedRewardSet, Control> RewardContainerRef =
        AccessTools.FieldRefAccess<NLinkedRewardSet, Control>("_rewardContainer");
    private static readonly AccessTools.FieldRef<NLinkedRewardSet, NRewardsScreen> RewardsScreenRef =
        AccessTools.FieldRefAccess<NLinkedRewardSet, NRewardsScreen>("_rewardsScreen");

    /// <summary>Sets already handed off to HandleLinkedClaim, guarding against a
    /// double-fire if some future path re-enters before QueueFree takes effect.</summary>
    private static readonly HashSet<ulong> HandledInstanceIds = new();

    static void Postfix(NLinkedRewardSet __instance) {
        try {
            var container = RewardContainerRef(__instance);
            if (container is null) return;

            foreach (var child in container.GetChildren()) {
                if (child is not NRewardButton button) continue;

                // Reload runs once per Create/SetReward, but guard against a
                // future double-Reload leaving duplicate connections anyway:
                // strip whatever is currently attached (vanilla's broken
                // hookup is the only subscriber inside a linked set) before
                // attaching ours.
                foreach (var conn in button.GetSignalConnectionList(NRewardButton.SignalName.RewardClaimed)) {
                    button.Disconnect(NRewardButton.SignalName.RewardClaimed, conn["callable"].AsCallable());
                }

                var setNode = __instance;
                button.Connect(
                    NRewardButton.SignalName.RewardClaimed,
                    Callable.From((System.Action<NRewardButton>)(_ => HandleLinkedClaim(setNode))),
                    0u);
            }
        } catch (System.Exception ex) {
            TiLog.Error("[bossy-relics] LinkedSetSignalRewirePatch failed; linked-set claim-removal may be broken", ex);
        }
    }

    private static void HandleLinkedClaim(NLinkedRewardSet setNode) {
        try {
            if (!GodotObject.IsInstanceValid(setNode)) return;
            if (!HandledInstanceIds.Add(setNode.GetInstanceId())) return;

            var screen = RewardsScreenRef(setNode);
            // The claimed reward already left LinkedRewardSet.Rewards - Reward.OnSelect
            // calls ParentRewardSet.RemoveReward(this) (see LinkedSetClaimBookkeepingPatch)
            // BEFORE NRewardButton.GetReward emits RewardClaimed, which is what fires
            // this handler. So the remaining count IS the unclaimed count.
            int remaining = setNode.LinkedRewardSet.Rewards.Count;
            screen.RewardCollectedFrom(setNode);
            setNode.LinkedRewardSet.OnSkipped();   // refunds every remaining member via LinkedSetRefundPatch
            HandledInstanceIds.Remove(setNode.GetInstanceId());

            if (GodotObject.IsInstanceValid(setNode)) setNode.QueueFree();
            TiLog.Info($"[bossy-relics] linked claim handled: group removed, {remaining} unclaimed refunded");
        } catch (System.Exception ex) {
            TiLog.Error("[bossy-relics] HandleLinkedClaim failed; linked group may remain stuck on screen", ex);
        }
    }
}
