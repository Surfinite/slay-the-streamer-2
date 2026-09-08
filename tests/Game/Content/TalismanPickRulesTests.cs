using System.Collections.Generic;
using System.Linq;
using SlayTheStreamer2.Game.Content;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.Content;

public class TalismanPickRulesTests {
    [Fact]
    public void Picks_AreDistinct_AndWithinRange() {
        int seed = 0;
        var picks = TalismanPickRules.PickIndices(10, 2, max => (seed += 7) % max);
        Assert.Equal(2, picks.Count);
        Assert.Equal(2, picks.Distinct().Count());
        Assert.All(picks, p => Assert.InRange(p, 0, 9));
    }

    [Fact]
    public void SameDraws_SamePicks() {
        IReadOnlyList<int> Run() { int s = 3; return TalismanPickRules.PickIndices(6, 3, max => (s = s * 31 + 7) % max); }
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void TakeAtLeastCount_ReturnsEverything() {
        var picks = TalismanPickRules.PickIndices(3, 5, max => 0);
        Assert.Equal(new[] { 0, 1, 2 }, picks.OrderBy(x => x));
    }

    [Fact]
    public void TakeZero_OrEmptyDeck_ReturnsNone() {
        Assert.Empty(TalismanPickRules.PickIndices(5, 0, max => 0));
        Assert.Empty(TalismanPickRules.PickIndices(0, 2, max => 0));
    }

    [Fact]
    public void OutOfRangeDraw_IsClamped_NotThrown() {
        var picks = TalismanPickRules.PickIndices(4, 2, max => 99);
        Assert.Equal(2, picks.Distinct().Count());
    }

    [Fact]
    public void Fnv1a64_IsStable_AndSensitive() {
        Assert.Equal(TalismanPickRules.Fnv1a64("abc|talisman|0"), TalismanPickRules.Fnv1a64("abc|talisman|0"));
        Assert.NotEqual(TalismanPickRules.Fnv1a64("abc|talisman|0"), TalismanPickRules.Fnv1a64("abc|talisman|1"));
        Assert.Equal(14695981039346656037UL, TalismanPickRules.Fnv1a64(""));
    }
}
