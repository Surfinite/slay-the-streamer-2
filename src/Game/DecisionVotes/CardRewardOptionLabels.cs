using System.Collections.Generic;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// Pure helper for card-reward vote option construction. Extracted from
/// CardRewardVotePatch so it can be unit-tested (Patch references Godot/Harmony
/// types not visible to the test project).
///
/// When the streamer enables "Allow chat to skip", Skip becomes #0 and cards
/// shift to #1..#N. Vote-close winners need the inverse mapping to find the
/// underlying card index.
/// </summary>
internal static class CardRewardOptionLabels {
    public const string SkipLabel = "Skip";

    /// <summary>
    /// Returns the labels list to pass into VoteCoordinator.Start.
    /// </summary>
    public static IReadOnlyList<string> Build(IReadOnlyList<string> cardTitles, bool includeSkip) {
        if (!includeSkip) return cardTitles;
        var labels = new List<string>(cardTitles.Count + 1) { SkipLabel };
        labels.AddRange(cardTitles);
        return labels;
    }

    /// <summary>
    /// Maps a chat-voted index back to the underlying card index.
    /// Returns null when the vote was for Skip (only possible when includeSkip is true and votedIndex == 0).
    /// </summary>
    public static int? ResolveCardIndex(int votedIndex, bool includeSkip) {
        if (!includeSkip) return votedIndex;
        if (votedIndex == 0) return null;
        return votedIndex - 1;
    }
}
