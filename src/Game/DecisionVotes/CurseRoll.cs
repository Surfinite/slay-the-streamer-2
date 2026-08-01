using System;
using System.Collections.Generic;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// Pure uniform picker for Cursed Overrides (spec 2026-08-01). Godot-free on
/// purpose: it rides the test csproj's DecisionVotes glob, so no Godot or
/// MegaCrit types may appear here. The game-side pool build + deck add live
/// in CursedOverrides.cs (Compile Remove'd from the test project).
/// </summary>
internal static class CurseRoll {
    internal static T PickCurse<T>(IReadOnlyList<T> pool, Random rng) {
        if (pool.Count == 0) throw new ArgumentException("curse pool is empty", nameof(pool));
        return pool[rng.Next(pool.Count)];
    }
}
