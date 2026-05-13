using System;
using System.Collections.Generic;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// Bounds-checked index→option lookup. Game-free testable seam for
/// BossVotePatch's winner→encounter resolution. The actual
/// MapCmd.SetBossEncounter invocation is operator-validated via Smoke A,
/// not unit-tested.
/// </summary>
internal static class BossVoteResolver {
    public static T ResolveWinner<T>(IReadOnlyList<T> options, int winnerIndex) {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if ((uint)winnerIndex >= (uint)options.Count) {
            throw new ArgumentOutOfRangeException(nameof(winnerIndex),
                $"winnerIndex {winnerIndex} out of range [0, {options.Count})");
        }
        return options[winnerIndex];
    }
}
