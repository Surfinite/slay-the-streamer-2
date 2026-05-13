using System;
using System.Linq;
using SlayTheStreamer2.Game.DecisionVotes;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

[Collection("TiLog.Sink")]
public class BossCandidateSamplerTests {
    [Fact]
    public void SampleDistinct_SameSeed_ReturnsSameOrder() {
        var pool = new[] { "A", "B", "C", "D", "E" };
        var s1 = BossCandidateSampler.SampleDistinct(pool, 3, new Random(42));
        var s2 = BossCandidateSampler.SampleDistinct(pool, 3, new Random(42));
        Assert.Equal(s1, s2);
    }

    [Fact]
    public void SampleDistinct_PoolLargerThanCount_ReturnsExactCount() {
        var pool = new[] { "A", "B", "C", "D", "E" };
        var s = BossCandidateSampler.SampleDistinct(pool, 3, new Random(1));
        Assert.Equal(3, s.Count);
    }

    [Fact]
    public void SampleDistinct_NoDuplicates() {
        var pool = new[] { "A", "B", "C", "D", "E" };
        var s = BossCandidateSampler.SampleDistinct(pool, 3, new Random(1));
        Assert.Equal(s.Count, s.Distinct().Count());
    }

    [Fact]
    public void SampleDistinct_PoolEqualsCount_ReturnsAllItems() {
        var pool = new[] { "A", "B", "C" };
        var s = BossCandidateSampler.SampleDistinct(pool, 3, new Random(1));
        Assert.Equal(3, s.Count);
        Assert.Equal(new[] { "A", "B", "C" }.ToHashSet(), s.ToHashSet());
    }

    [Fact]
    public void SampleDistinct_PoolSmallerThanCount_ReturnsPoolSize() {
        var pool = new[] { "A", "B" };
        var s = BossCandidateSampler.SampleDistinct(pool, 3, new Random(1));
        Assert.Equal(2, s.Count);
        Assert.Equal(new[] { "A", "B" }.ToHashSet(), s.ToHashSet());
    }

    [Fact]
    public void SampleDistinct_SinglePool_ReturnsSingle() {
        var pool = new[] { "A" };
        var s = BossCandidateSampler.SampleDistinct(pool, 3, new Random(1));
        Assert.Single(s);
        Assert.Equal("A", s[0]);
    }

    [Fact]
    public void SampleDistinct_EmptyPool_ReturnsEmpty() {
        var pool = Array.Empty<string>();
        var s = BossCandidateSampler.SampleDistinct(pool, 3, new Random(1));
        Assert.Empty(s);
    }

    [Fact]
    public void SampleDistinct_ZeroCount_ReturnsEmpty() {
        var pool = new[] { "A", "B", "C" };
        var s = BossCandidateSampler.SampleDistinct(pool, 0, new Random(1));
        Assert.Empty(s);
    }

    [Fact]
    public void SampleDistinct_NullSource_Throws() {
        Assert.Throws<ArgumentNullException>(() =>
            BossCandidateSampler.SampleDistinct<string>(null!, 3, new Random(1)));
    }

    [Fact]
    public void SampleDistinct_NullRng_Throws() {
        var pool = new[] { "A", "B", "C" };
        Assert.Throws<ArgumentNullException>(() =>
            BossCandidateSampler.SampleDistinct(pool, 3, null!));
    }
}
