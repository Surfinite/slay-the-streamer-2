using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Rewards;
using SlayTheStreamer2.Game.Bootstrap;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// card-scope: allowlist of combat-origin CardRewards, ported from
/// SabotageTheStreamer's scope/ slice (2026-08-24, rig-proven there).
///
/// A CardReward is combat-origin iff its RewardsSet passed through
/// <c>Hook.BeforeCombatRewardOffered</c> — the one gateway every combat reward
/// set crosses (sole call site: CombatRoom.OfferRoomEndRewards, per-set, AFTER
/// GenerateForRoomEnd fully completes — so PrayerWheel/WhiteStar injections,
/// TheHunt extras, and tutorial ctor-B rewards are already in the list). The
/// save-resume path (StartPreFinishedCombat → OfferRoomEndRewards) re-generates
/// and therefore re-tags. RewardsCmd.OfferForRoomEnd — the only bypass route —
/// has ZERO callers (decompile-verified against v0.111.0, 2026-08-24;
/// watchlisted in notes/06). Relic obtains (Orrery/Kaleidoscope class), pure
/// event rewards, Dream Catcher, and the Draft modifier never cross the hook →
/// untagged → streamer-free under the combatCardVotesOnly toggle.
///
/// Tagging is UNCONDITIONAL of the checkbox — the tag is inert data; the
/// toggle is a pure read-side switch (<see cref="ShouldVoteOn"/>), so flipping
/// it mid-run works retroactively for already-offered sets.
///
/// Fail-safe direction: unknown/untagged ⇒ NO vote ⇒ streamer picks freely —
/// an unreachable vote can never strand the run. But if either patch here
/// failed to REGISTER (future game update renames the hook), the toggle is
/// treated as inoperative and every card reward votes again (one-time Warn) —
/// killing all card votes silently would be the worse failure.
/// </summary>
internal static class CombatOriginTags {
    private static readonly ConditionalWeakTable<CardReward, object> Tags = new();
    private static readonly object Marker = new();

    /// <summary>
    /// The CardReward whose selection sub-screen is currently (or was most
    /// recently) on screen. CardReward.OnSelect is the ONLY caller of
    /// NCardRewardSelectionScreen.ShowScreen in the whole game, so this is a
    /// faithful screen→reward mapping. Weak so a stale capture never roots a
    /// dead run's model graph. Main-thread only (OnSelect and the vote
    /// prefixes all run on the Godot main thread).
    /// </summary>
    private static WeakReference<CardReward>? _activeReward;

    private static int _degradedWarnFired;

    internal static bool TagPatchRegistered { get; private set; }
    internal static bool CapturePatchRegistered { get; private set; }

    internal static bool IsTagged(CardReward reward) => Tags.TryGetValue(reward, out _);

    internal static CardReward? TryGetActiveReward() {
        var weak = _activeReward;
        return weak is not null && weak.TryGetTarget(out var reward) ? reward : null;
    }

    /// <summary>
    /// The single vote-scope predicate — every surface that assumes
    /// "card reward ⇒ vote machinery" must consult this (vote prefix, skip
    /// gate counting, streamer-Skip budget, Skip-alt flip, counter label).
    /// True = the reward is in scope for chat voting; false = streamer-free,
    /// full vanilla behavior.
    /// </summary>
    internal static bool ShouldVoteOn(CardReward? reward) {
        if (ModSettings.Current?.CombatCardVotesOnly != true) return true;   // toggle off = today's behavior
        if (!TagPatchRegistered || !CapturePatchRegistered) {
            if (Interlocked.CompareExchange(ref _degradedWarnFired, 1, 0) == 0) {
                TiLog.Warn("[SlayTheStreamer2][card-scope] combatCardVotesOnly is ON but the tagging/capture patches did not register; treating the toggle as inoperative — all card rewards vote");
            }
            return true;
        }
        if (reward is null) return false;   // unknown reward → fail-safe: no vote, streamer free
        return IsTagged(reward);
    }

    /// <summary>Scope predicate for the reward whose sub-screen is on screen
    /// (call sites that only have the NCardRewardSelectionScreen).</summary>
    internal static bool ShouldVoteOnActiveReward() => ShouldVoteOn(TryGetActiveReward());

    [HarmonyPatch(typeof(Hook), nameof(Hook.BeforeCombatRewardOffered))]
    internal static class TagPatch {
        static bool Prepare(MethodBase? original) {
            if (original is not null) return true;   // per-method pass after registration check
            var target = AccessTools.Method(typeof(Hook), nameof(Hook.BeforeCombatRewardOffered));
            if (target is null) {
                TiLog.Error("[SlayTheStreamer2][card-scope] Hook.BeforeCombatRewardOffered not found; combat-origin tagging will not register (combatCardVotesOnly degrades to voting on all card rewards)");
                TagPatchRegistered = false;
                return false;
            }
            TagPatchRegistered = true;
            return true;
        }

        // Async target — the prefix runs in the state-machine kickoff stub's
        // synchronous head, before any reward is offered.
        static void Prefix(RewardsSet rewards) {
            try {
                int tagged = 0;
                foreach (var reward in rewards.Rewards) {
                    if (reward is CardReward cardReward) {
                        Tags.GetValue(cardReward, _ => Marker);   // idempotent add
                        tagged++;
                    }
                }
                if (tagged > 0) {
                    TiLog.Info($"[SlayTheStreamer2][card-scope] tagged {tagged} combat-origin card reward(s)");
                }
            } catch (Exception ex) {
                TiLog.Error("[SlayTheStreamer2][card-scope] combat-origin tagging failed", ex);
            }
        }
    }

    // OnSelect is protected — string literal target (nameof won't bind across
    // accessibility), same idiom as CardRewardVotePatch's SelectCard patch.
    [HarmonyPatch(typeof(CardReward), "OnSelect")]
    internal static class ActiveRewardCapturePatch {
        static bool Prepare(MethodBase? original) {
            if (original is not null) return true;
            var target = AccessTools.Method(typeof(CardReward), "OnSelect");
            if (target is null) {
                TiLog.Error("[SlayTheStreamer2][card-scope] CardReward.OnSelect not found; active-reward capture will not register (combatCardVotesOnly degrades to voting on all card rewards)");
                CapturePatchRegistered = false;
                return false;
            }
            CapturePatchRegistered = true;
            return true;
        }

        static void Prefix(CardReward __instance) {
            _activeReward = new WeakReference<CardReward>(__instance);
        }
    }
}
