using System;
using System.Collections.Generic;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// Partial Fisher-Yates sampling without replacement. Deterministic under
/// seeded RNG. Used by BossVotePatch to draw up to N boss candidates from
/// the act's AllBossEncounters pool.
/// </summary>
internal static class BossCandidateSampler {
    public static IReadOnlyList<T> SampleDistinct<T>(
        IReadOnlyList<T> source, int count, Random rng) {

        if (source is null) throw new ArgumentNullException(nameof(source));
        if (rng is null) throw new ArgumentNullException(nameof(rng));
        if (count <= 0 || source.Count == 0) return Array.Empty<T>();

        // Copy to mutable list; partial Fisher-Yates from the front.
        // After n = min(count, source.Count) iterations, items[0..n) is a
        // uniform random sample without replacement.
        var items = new List<T>(source);
        int n = Math.Min(count, items.Count);
        for (int i = 0; i < n; i++) {
            int j = rng.Next(i, items.Count);
            (items[i], items[j]) = (items[j], items[i]);
        }
        return items.GetRange(0, n);
    }
}
