using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// voter-names: session-lifetime fairness pool. Uniform-random among the
/// LEAST-used voters (strict rule per spec §4: nobody gets a second enemy
/// until every distinct voter has had one), decorated by use-count:
/// 1 → bare name, 2 → "Name Jr.", n≥3 → "Name III/IV/…" (StS1 homage).
/// Pure BCL — rides the test glob (CurseRoll precedent). NOT thread-safe by
/// itself; all callers are main-thread or marshal first (see VoterNamePoolHook).
/// </summary>
internal sealed class VoterNamePool {
    private const int MaxNameLength = 25;

    private sealed class Entry {
        public required string DisplayName;
        public int UsedCount;
    }

    private readonly Dictionary<string, Entry> _voters = new();
    private readonly Random _random;

    public VoterNamePool(Random random) {
        _random = random ?? throw new ArgumentNullException(nameof(random));
    }

    public int DistinctVoterCount => _voters.Count;

    public void AddVoters(IReadOnlyDictionary<string, string> voterDisplayNames) {
        foreach (var (key, rawName) in voterDisplayNames) {
            var name = CleanName(rawName);
            if (name is null) continue;
            if (_voters.TryGetValue(key, out var existing)) {
                existing.DisplayName = name;   // people rename; used-count preserved
            } else {
                _voters[key] = new Entry { DisplayName = name };
            }
        }
    }

    public bool TryTakeName(out string decoratedName, out string voterKey) {
        decoratedName = string.Empty;
        voterKey = string.Empty;
        if (_voters.Count == 0) return false;

        int minUsed = _voters.Values.Min(e => e.UsedCount);
        var candidates = _voters.Where(kv => kv.Value.UsedCount == minUsed).ToList();
        var picked = candidates[_random.Next(candidates.Count)];

        picked.Value.UsedCount++;
        voterKey = picked.Key;
        decoratedName = Decorate(picked.Value.DisplayName, picked.Value.UsedCount);
        return true;
    }

    private static string Decorate(string name, int useCount) => useCount switch {
        1 => name,
        2 => name + " Jr.",
        _ => name + " " + RomanNumerals.Convert(useCount),
    };

    private static string? CleanName(string raw) {
        var trimmed = raw?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (trimmed.Length > MaxNameLength) {
            trimmed = trimmed.Substring(0, MaxNameLength - 1) + "…";
        }
        return trimmed;
    }
}

/// <summary>Classic integer→Roman conversion (StS1 mod homage; capped by caller reality, guard at 3999).</summary>
internal static class RomanNumerals {
    private static readonly (int Value, string Symbol)[] Table = {
        (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"),
        (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
        (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I"),
    };

    public static string Convert(int value) {
        if (value < 1 || value > 3999) return value.ToString();
        var sb = new StringBuilder();
        foreach (var (v, s) in Table) {
            while (value >= v) { sb.Append(s); value -= v; }
        }
        return sb.ToString();
    }
}
