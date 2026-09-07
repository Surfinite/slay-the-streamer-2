using SlayTheStreamer2.Game.DecisionVotes;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

public class RemovalClickRulesTests {
    [Fact]
    public void NoRemovalYet_Denies() =>
        Assert.Equal(RemovalClickVerdict.Deny, RemovalClickRules.Judge(clicked: 1, removed: null, overridesRemaining: 3));

    [Fact]
    public void ClickOnOtherCard_Allows() =>
        Assert.Equal(RemovalClickVerdict.Allow, RemovalClickRules.Judge(0, removed: 2, overridesRemaining: 0));

    [Fact]
    public void ClickOnRemovedCard_WithBudget_AllowsWithOverride() =>
        Assert.Equal(RemovalClickVerdict.AllowWithOverride, RemovalClickRules.Judge(2, removed: 2, overridesRemaining: 1));

    [Fact]
    public void ClickOnRemovedCard_NoBudget_Denies() =>
        Assert.Equal(RemovalClickVerdict.Deny, RemovalClickRules.Judge(2, removed: 2, overridesRemaining: 0));

    [Fact]
    public void SkipRemoved_SkipClickCostsOverride() =>
        Assert.Equal(RemovalClickVerdict.AllowWithOverride,
            RemovalClickRules.Judge(RemovalClickRules.SkipIndex, removed: RemovalClickRules.SkipIndex, overridesRemaining: 1));

    [Fact]
    public void SkipRemoved_CardClickIsFree() =>
        Assert.Equal(RemovalClickVerdict.Allow, RemovalClickRules.Judge(0, removed: RemovalClickRules.SkipIndex, overridesRemaining: 0));
}
