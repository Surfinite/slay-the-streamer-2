using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SlayTheStreamer2.Game.Content;

/// <summary>Spec section 7.1 step 3: which deck cards the reworked Talisman upgrades.
/// Partial Fisher-Yates over the candidate indices, one draw per pick, so the result is
/// a pure function of (count, take, draw sequence). A draw outside [0, pool) is clamped
/// rather than thrown. Fnv1a64 turns the run seed string plus a salt into a stable ulong
/// for Rng(ulong), so a save-quit-Continue that re-runs the pickup picks the same cards.</summary>
public static class TalismanPickRules {
    public static IReadOnlyList<int> PickIndices(int count, int take, Func<int, int> nextInt) {
        if (count <= 0 || take <= 0) return Array.Empty<int>();
        var pool = Enumerable.Range(0, count).ToList();
        int n = Math.Min(take, count);
        var picks = new List<int>(n);
        for (int i = 0; i < n; i++) {
            int j = Math.Clamp(nextInt(pool.Count), 0, pool.Count - 1);
            picks.Add(pool[j]);
            pool.RemoveAt(j);
        }
        return picks;
    }

    public static ulong Fnv1a64(string text) {
        const ulong offset = 14695981039346656037UL, prime = 1099511628211UL;
        ulong hash = offset;
        foreach (byte b in Encoding.UTF8.GetBytes(text)) { hash ^= b; hash *= prime; }
        return hash;
    }
}
