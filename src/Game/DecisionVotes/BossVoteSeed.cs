namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// FNV-1a 32-bit stable hash for boss-vote candidate-sampling RNG seed.
/// Does NOT use HashCode.Combine because string.GetHashCode() is per-process
/// randomized in .NET 5+ — that would break save-load determinism (Smoke H
/// expects identical candidate sets after reload).
/// </summary>
internal static class BossVoteSeed {
    public static int Stable(string? runSeed, int actIndex) {
        unchecked {
            const int fnvOffset = unchecked((int)2166136261);
            const int fnvPrime = 16777619;
            int hash = fnvOffset;
            foreach (char c in runSeed ?? string.Empty) {
                hash ^= c;
                hash *= fnvPrime;
            }
            hash ^= actIndex;
            hash *= fnvPrime;
            return hash;
        }
    }
}
