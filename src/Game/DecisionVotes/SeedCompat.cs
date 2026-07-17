using System;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// Game v0.109.0 widened RNG seeds from uint to ulong: Rng's ctor changed from
/// (uint seed, int counter) to (ulong seed), RunRngSet.Seed from uint to ulong,
/// and NMapScreen.SetMap's seed parameter followed. A direct C# call compiles
/// against exactly one of those signatures and throws MissingMethodException at
/// JIT time on the other game branch (surfaced live on the Workshop when the
/// beta branch moved to v0.109.0 while non-beta stayed on v0.107.1). The mod's
/// two seed-touching call sites go through this class instead, binding the
/// member reflectively per game version. Both are cold paths (once per
/// act-variant vote / once per A10 second-boss swap), so reflection cost is
/// irrelevant.
/// </summary>
internal static class SeedCompat {
    /// <summary>Rng(ulong) on game >= v0.109.0, Rng(uint, int) before.</summary>
    internal static Rng CreateRng(uint seed) {
        var t = typeof(Rng);
        var c64 = t.GetConstructor(new[] { typeof(ulong) });
        if (c64 != null) return (Rng)c64.Invoke(new object[] { (ulong)seed });
        var c32 = t.GetConstructor(new[] { typeof(uint), typeof(int) });
        if (c32 != null) return (Rng)c32.Invoke(new object[] { seed, 0 });
        throw new MissingMethodException("Rng ctor: neither (ulong) nor (uint, int) found");
    }

    /// <summary>
    /// NMapScreen.SetMap(map, seed, clearDrawings) with the run's current seed,
    /// reading RunRngSet.Seed and converting to whatever width this game
    /// version's SetMap expects. StringSeed (string) is unchanged across
    /// versions — only the numeric Seed widened.
    /// </summary>
    internal static void SetMapPreservingSeed(NMapScreen screen, ActMap map, RunRngSet rngSet, bool clearDrawings) {
        var seedProp = typeof(RunRngSet).GetProperty("Seed")
            ?? throw new MissingMemberException("RunRngSet.Seed not found");
        object seed = seedProp.GetValue(rngSet)!;

        var setMap = typeof(NMapScreen).GetMethod("SetMap")
            ?? throw new MissingMethodException("NMapScreen.SetMap not found");
        var seedParamType = setMap.GetParameters()[1].ParameterType;

        setMap.Invoke(screen, new[] { (object)map, Convert.ChangeType(seed, seedParamType), clearDrawings });
    }
}
