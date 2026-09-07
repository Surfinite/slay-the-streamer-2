// src/Game/DecisionVotes/RewardAuthority.cs
using System.Threading;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Rewards;
using SlayTheStreamer2.Game.Bootstrap;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>The single decision point for every card reward (spec section 2).</summary>
internal static class RewardAuthority {
    private static int _degradedWarnFired;

    private static bool CombatTagRegistered => CombatOriginTags.TagPatchRegistered && CombatOriginTags.CapturePatchRegistered;
    private static bool CombatOnly => ModSettings.Current?.CombatCardVotesOnly ?? false;

    /// <summary>True when the per-origin rules and their explanation text apply.</summary>
    internal static bool RulesActive => AuthorityRules.RulesActive(CombatTagRegistered, CombatOnly);

    internal static AuthorityMode Classify(CardReward? reward) {
        if (!CombatTagRegistered) {
            if (Interlocked.CompareExchange(ref _degradedWarnFired, 1, 0) == 0) {
                TiLog.Warn("[SlayTheStreamer2][card-scope] combat-origin tagging did not register (default branch?); every card reward is a normal vote");
            }
            return AuthorityMode.NormalVote;
        }
        if (reward is null) return AuthorityMode.Free;   // unknown reward: fail-safe, streamer picks freely
        var relic = RelicOriginTags.RelicFor(reward);
        var origin = new RewardOrigin(
            CombatTagged: CombatOriginTags.IsTagged(reward),
            RelicTagged: relic is not null,
            RelicAncient: relic is not null && relic.Rarity == RelicRarity.Ancient,
            RestSiteTagged: RestSiteOriginTags.IsTagged(reward));
        return AuthorityRules.Resolve(origin, CombatTagRegistered, CombatOnly);
    }

    /// <summary>Mode of the reward whose selection sub-screen is on screen.</summary>
    internal static AuthorityMode ModeOfActiveReward() => Classify(CombatOriginTags.TryGetActiveReward());
}
