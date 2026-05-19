using System.Collections.Generic;
using SlayTheStreamer2.Game.DecisionVotes;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

public class CardRewardOptionLabelsTests {
    [Fact]
    public void Build_SkipDisabled_ReturnsCardTitlesOnly() {
        var titles = new List<string> { "Strike", "Defend", "Bash" };
        var labels = CardRewardOptionLabels.Build(titles, includeSkip: false);
        Assert.Equal(new[] { "Strike", "Defend", "Bash" }, labels);
    }

    [Fact]
    public void Build_SkipEnabled_PrependsSkipAndShiftsCards() {
        var titles = new List<string> { "Strike", "Defend", "Bash" };
        var labels = CardRewardOptionLabels.Build(titles, includeSkip: true);
        Assert.Equal(new[] { "Skip", "Strike", "Defend", "Bash" }, labels);
    }

    [Fact]
    public void ResolveCardIndex_SkipDisabled_PassthroughIndex() {
        Assert.Equal(0, CardRewardOptionLabels.ResolveCardIndex(votedIndex: 0, includeSkip: false));
        Assert.Equal(2, CardRewardOptionLabels.ResolveCardIndex(votedIndex: 2, includeSkip: false));
    }

    [Fact]
    public void ResolveCardIndex_SkipEnabled_ZeroIsSkip() {
        Assert.Null(CardRewardOptionLabels.ResolveCardIndex(votedIndex: 0, includeSkip: true));
    }

    [Fact]
    public void ResolveCardIndex_SkipEnabled_NonZeroShiftsDown() {
        Assert.Equal(0, CardRewardOptionLabels.ResolveCardIndex(votedIndex: 1, includeSkip: true));
        Assert.Equal(2, CardRewardOptionLabels.ResolveCardIndex(votedIndex: 3, includeSkip: true));
    }
}
