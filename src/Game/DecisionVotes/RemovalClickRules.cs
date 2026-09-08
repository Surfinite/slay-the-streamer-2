namespace SlayTheStreamer2.Game.DecisionVotes;

public enum RemovalClickVerdict { Deny, Allow, AllowWithOverride }

/// <summary>Spec section 3.2: after chat's removal lands, everything is legal except the
/// removed option, which costs a vote override. Index space: card holder index, or
/// <see cref="SkipIndex"/> for Skip. Pure function. The spec's reroll index is not a
/// parameter here because Reroll clicks never reach Judge; both screen patches pass
/// non-Skip alternatives straight to vanilla.</summary>
public static class RemovalClickRules {
    public const int SkipIndex = -1;

    public static RemovalClickVerdict Judge(int clicked, int? removed, int overridesRemaining) {
        if (removed is null) return RemovalClickVerdict.Deny;          // no removal yet: the vote owns the screen
        if (clicked != removed.Value) return RemovalClickVerdict.Allow;
        return overridesRemaining > 0 ? RemovalClickVerdict.AllowWithOverride : RemovalClickVerdict.Deny;
    }
}
