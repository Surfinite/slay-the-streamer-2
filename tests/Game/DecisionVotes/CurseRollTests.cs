using System;
using System.Collections.Generic;
using System.Linq;
using SlayTheStreamer2.Game.DecisionVotes;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

public class CurseRollTests {

    [Fact]
    public void PickCurse_returns_the_only_element_of_a_singleton_pool() =>
        Assert.Equal("Injury", CurseRoll.PickCurse(new[] { "Injury" }, new Random(42)));

    [Fact]
    public void PickCurse_is_deterministic_for_a_seeded_rng() {
        var pool = new[] { "Clumsy", "Debt", "Decay", "Doubt", "Guilty" };
        var a = CurseRoll.PickCurse(pool, new Random(1234));
        var b = CurseRoll.PickCurse(pool, new Random(1234));
        Assert.Equal(a, b);
    }

    [Fact]
    public void PickCurse_reaches_every_element_over_many_draws() {
        var pool = new[] { "Clumsy", "Debt", "Decay", "Doubt", "Guilty" };
        var rng = new Random(42);
        var seen = new HashSet<string>();
        for (int i = 0; i < 500; i++) seen.Add(CurseRoll.PickCurse(pool, rng));
        Assert.Equal(pool.Length, seen.Count);
    }

    [Fact]
    public void PickCurse_throws_on_empty_pool() =>
        Assert.Throws<ArgumentException>(() => CurseRoll.PickCurse(Array.Empty<string>(), new Random(42)));
}
