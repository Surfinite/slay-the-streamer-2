using System.Collections.Generic;
using SlayTheStreamer2.Game.DecisionVotes;

namespace SlayTheStreamer2.Game.CharSelect;

/// <summary>The pure part of the Custom Mode Random rules (no game types, unit-tested).
/// Game modes arrive as boxed enum values and are compared by name; see
/// <see cref="GameModeNames"/> for why.</summary>
internal static class RandomCharacterRules {
    /// <summary>True when Embark has to be refused: the lobby's mode is Dread and some
    /// lobby player is still on the Random character. The lobby resolves Random picks
    /// for EVERY player at launch and throws for any of them in Dread, after the screen
    /// has disabled its own buttons. The Dread rule moves the LOCAL selection off
    /// Random; a remote player's follows a moment later (their own client moves it when
    /// the mode change reaches them), so a host who switches to Dread and embarks at
    /// once could still catch a remote pick on Random.</summary>
    internal static bool MustRefuseEmbark(object? lobbyGameMode, IEnumerable<bool> playerIsOnRandom) {
        if (!GameModeNames.IsDread(lobbyGameMode)) return false;
        foreach (bool onRandom in playerIsOnRandom) {
            if (onRandom) return true;
        }
        return false;
    }
}
