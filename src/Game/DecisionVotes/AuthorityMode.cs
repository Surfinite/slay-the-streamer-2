using SlayTheStreamer2.Game.Bootstrap;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>Per-origin behaviour of one card reward (spec section 2).
/// Free = fully vanilla, no mod involvement (the checkbox-On streamer-free path).
/// NormalVote = today's pick vote. RemoveOne = chat votes which option to remove,
/// the streamer picks from the rest. Unskippable = no vote; Skip hidden and denied.</summary>
public enum AuthorityMode { Free, NormalVote, RemoveOne, Unskippable }

/// <summary>The tag facts one CardReward carries. Game code fills this from the
/// origin-tag tables; the rules never see a game type.</summary>
public readonly record struct RewardOrigin(bool CombatTagged, bool RelicTagged, bool RelicAncient, bool RestSiteTagged);

public static class AuthorityRules {
    /// <summary>Spec section 2 evaluation order, parameterised by the three-way mode
    /// (handoff 2026-09-13 section 5). An unregistered combat tag (the game's default
    /// branch) means the classifier cannot tell events from combat, so everything stays
    /// a normal vote. Free = the old "combat only" checkbox On. RemoveOne turns every
    /// non-combat reward into a removal vote. Mixed is the spec table.</summary>
    public static AuthorityMode Resolve(RewardOrigin o, bool combatTagRegistered, NonCombatRewardMode mode) {
        if (!combatTagRegistered) return AuthorityMode.NormalVote;
        if (o.CombatTagged) return AuthorityMode.NormalVote;
        if (mode == NonCombatRewardMode.Free) return AuthorityMode.Free;
        if (o.RelicTagged) return o.RelicAncient || mode == NonCombatRewardMode.RemoveOne ? AuthorityMode.RemoveOne : AuthorityMode.Unskippable;
        if (o.RestSiteTagged) return AuthorityMode.RemoveOne;
        return mode == NonCombatRewardMode.RemoveOne ? AuthorityMode.RemoveOne : AuthorityMode.Unskippable;
    }

    /// <summary>True when the per-origin rules (and their explanation text) are live.</summary>
    public static bool RulesActive(bool combatTagRegistered, NonCombatRewardMode mode) =>
        combatTagRegistered && mode != NonCombatRewardMode.Free;
}
