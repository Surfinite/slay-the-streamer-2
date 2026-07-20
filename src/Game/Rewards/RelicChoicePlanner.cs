using System;

namespace SlayTheStreamer2.Game.Rewards;

/// <summary>
/// Pure decision logic for the Bossy Relics feature (pick 1 of N relic rewards).
/// System-only on purpose: compiled into the test project, so no Godot or
/// MegaCrit types may appear here.
/// </summary>
public static class RelicChoicePlanner {
    public const int MaxChoices = 4;   // chest UI ships exactly 4 relic holders

    public static int Clamp(int requested) => Math.Clamp(requested, 1, MaxChoices);

    /// <summary>
    /// How many extra relics to add so the offer reaches Clamp(relicChoices),
    /// never exceeding maxTotal and never turning a 0-relic context (empty
    /// chest, no relic reward) into a non-empty one.
    /// </summary>
    public static int ExtraCount(int relicChoices, int existingCount, int maxTotal) {
        if (existingCount <= 0) return 0;
        int target = Math.Min(Clamp(relicChoices), maxTotal);
        return Math.Max(0, target - existingCount);
    }

    /// <summary>
    /// Deterministic per-offer seed (FNV-1a 32-bit) from run seed + surface +
    /// act/floor, so a save-quit-regenerated offer re-rolls identical rarities
    /// without any stream-position tracking.
    /// </summary>
    public static uint OfferSeed(string? runSeed, string surfaceSalt, int actIndex, int floor) {
        unchecked {
            const uint prime = 16777619u;
            uint hash = 2166136261u;
            foreach (char c in runSeed ?? "no-seed") { hash ^= c; hash *= prime; }
            foreach (char c in surfaceSalt)          { hash ^= c; hash *= prime; }
            hash ^= (uint)actIndex; hash *= prime;
            hash ^= (uint)floor;    hash *= prime;
            return hash;
        }
    }
}
