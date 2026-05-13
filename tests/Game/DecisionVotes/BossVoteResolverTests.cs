using System;
using SlayTheStreamer2.Game.DecisionVotes;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

[Collection("TiLog.Sink")]
public class BossVoteResolverTests {
    [Fact]
    public void ResolveWinner_ValidIndex_ReturnsOption() {
        var options = new[] { "A", "B", "C" };
        Assert.Equal("B", BossVoteResolver.ResolveWinner(options, 1));
    }

    [Fact]
    public void ResolveWinner_FirstIndex_ReturnsFirst() {
        var options = new[] { "A", "B", "C" };
        Assert.Equal("A", BossVoteResolver.ResolveWinner(options, 0));
    }

    [Fact]
    public void ResolveWinner_LastIndex_ReturnsLast() {
        var options = new[] { "A", "B", "C" };
        Assert.Equal("C", BossVoteResolver.ResolveWinner(options, 2));
    }

    [Fact]
    public void ResolveWinner_OutOfRange_Throws() {
        var options = new[] { "A", "B", "C" };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BossVoteResolver.ResolveWinner(options, 3));
    }

    [Fact]
    public void ResolveWinner_NegativeIndex_Throws() {
        var options = new[] { "A", "B", "C" };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BossVoteResolver.ResolveWinner(options, -1));
    }

    [Fact]
    public void ResolveWinner_EmptyOptions_AnyIndexThrows() {
        var options = Array.Empty<string>();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BossVoteResolver.ResolveWinner(options, 0));
    }

    [Fact]
    public void ResolveWinner_NullOptions_Throws() {
        Assert.Throws<ArgumentNullException>(() =>
            BossVoteResolver.ResolveWinner<string>(null!, 0));
    }
}
