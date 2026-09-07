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
    /// <summary>Spec section 2 evaluation order. An unregistered combat tag (the
    /// game's default branch) means the classifier cannot tell events from combat,
    /// so everything stays a normal vote, exactly today's behaviour.</summary>
    public static AuthorityMode Resolve(RewardOrigin o, bool combatTagRegistered, bool combatCardVotesOnly) {
        if (!combatTagRegistered) return AuthorityMode.NormalVote;
        if (combatCardVotesOnly) return o.CombatTagged ? AuthorityMode.NormalVote : AuthorityMode.Free;
        if (o.CombatTagged) return AuthorityMode.NormalVote;
        if (o.RelicTagged) return o.RelicAncient ? AuthorityMode.RemoveOne : AuthorityMode.Unskippable;
        if (o.RestSiteTagged) return AuthorityMode.RemoveOne;
        return AuthorityMode.Unskippable;
    }

    /// <summary>True when the per-origin rules (and their explanation text) are live.</summary>
    public static bool RulesActive(bool combatTagRegistered, bool combatCardVotesOnly) =>
        combatTagRegistered && !combatCardVotesOnly;
}
