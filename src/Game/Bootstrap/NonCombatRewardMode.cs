using System;

namespace SlayTheStreamer2.Game.Bootstrap;

/// <summary>How card rewards that did NOT come from combat are handled (Surfinite's
/// ruling 2026-09-13, replacing the combatCardVotesOnly checkbox).
/// Free: the streamer picks, Skip allowed, no chat vote (the old checkbox On).
/// RemoveOne: every non-combat card reward gets the remove-one vote.
/// Mixed (default): Ancient relics and Dream Catcher get the remove-one vote; event and
/// shop-relic rewards are unskippable with no vote (the old checkbox Off).</summary>
public enum NonCombatRewardMode { Free, RemoveOne, Mixed }

public static class NonCombatRewardModes {
    public const string Key = "nonCombatCardRewards";
    public const NonCombatRewardMode Default = NonCombatRewardMode.Mixed;

    public static string ToJson(NonCombatRewardMode mode) => mode switch {
        NonCombatRewardMode.Free => "free",
        NonCombatRewardMode.RemoveOne => "removeOne",
        _ => "mixed",
    };

    /// <summary>Case-insensitive; tolerates surrounding spaces and an underscore or
    /// hyphen inside removeOne.</summary>
    public static bool TryParse(string? s, out NonCombatRewardMode mode) {
        mode = Default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        switch (s.Trim().Replace("_", "").Replace("-", "").ToLowerInvariant()) {
            case "free": mode = NonCombatRewardMode.Free; return true;
            case "removeone": mode = NonCombatRewardMode.RemoveOne; return true;
            case "mixed": mode = NonCombatRewardMode.Mixed; return true;
            default: return false;
        }
    }

    /// <summary>The old boolean's meaning: On (true) was "combat only" = Free.</summary>
    public static NonCombatRewardMode FromLegacyCombatOnly(bool combatCardVotesOnly) =>
        combatCardVotesOnly ? NonCombatRewardMode.Free : NonCombatRewardMode.Mixed;
}
