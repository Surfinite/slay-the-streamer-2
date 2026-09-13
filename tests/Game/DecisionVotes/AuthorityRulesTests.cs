using SlayTheStreamer2.Game.Bootstrap;
using SlayTheStreamer2.Game.DecisionVotes;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

public class AuthorityRulesTests {
    private static RewardOrigin Combat => new(CombatTagged: true, RelicTagged: false, RelicAncient: false, RestSiteTagged: false);
    private static RewardOrigin AncientRelic => new(false, true, true, false);
    private static RewardOrigin ShopRelic => new(false, true, false, false);
    private static RewardOrigin RestSite => new(false, false, false, true);
    private static RewardOrigin Untagged => new(false, false, false, false);

    [Theory]
    [InlineData(NonCombatRewardMode.Free)]
    [InlineData(NonCombatRewardMode.RemoveOne)]
    [InlineData(NonCombatRewardMode.Mixed)]
    public void CombatTagUnregistered_EverythingIsNormalVote(NonCombatRewardMode mode) {
        foreach (var o in new[] { Combat, AncientRelic, ShopRelic, RestSite, Untagged })
            Assert.Equal(AuthorityMode.NormalVote, AuthorityRules.Resolve(o, combatTagRegistered: false, mode));
    }

    [Fact]
    public void Free_CombatVotes_EverythingElseFree() {
        Assert.Equal(AuthorityMode.NormalVote, AuthorityRules.Resolve(Combat, true, NonCombatRewardMode.Free));
        foreach (var o in new[] { AncientRelic, ShopRelic, RestSite, Untagged })
            Assert.Equal(AuthorityMode.Free, AuthorityRules.Resolve(o, true, NonCombatRewardMode.Free));
    }

    [Fact]
    public void Mixed_TableApplies() {
        Assert.Equal(AuthorityMode.NormalVote, AuthorityRules.Resolve(Combat, true, NonCombatRewardMode.Mixed));
        Assert.Equal(AuthorityMode.RemoveOne, AuthorityRules.Resolve(AncientRelic, true, NonCombatRewardMode.Mixed));
        Assert.Equal(AuthorityMode.Unskippable, AuthorityRules.Resolve(ShopRelic, true, NonCombatRewardMode.Mixed));
        Assert.Equal(AuthorityMode.RemoveOne, AuthorityRules.Resolve(RestSite, true, NonCombatRewardMode.Mixed));
        Assert.Equal(AuthorityMode.Unskippable, AuthorityRules.Resolve(Untagged, true, NonCombatRewardMode.Mixed));
    }

    [Fact]
    public void RemoveOne_EveryNonCombatRewardIsRemoveOne() {
        Assert.Equal(AuthorityMode.NormalVote, AuthorityRules.Resolve(Combat, true, NonCombatRewardMode.RemoveOne));
        foreach (var o in new[] { AncientRelic, ShopRelic, RestSite, Untagged })
            Assert.Equal(AuthorityMode.RemoveOne, AuthorityRules.Resolve(o, true, NonCombatRewardMode.RemoveOne));
    }

    [Fact]
    public void CombatTagWinsOverRelicTag() {
        var both = new RewardOrigin(true, true, true, false);
        Assert.Equal(AuthorityMode.NormalVote, AuthorityRules.Resolve(both, true, NonCombatRewardMode.Mixed));
    }

    [Fact]
    public void RelicTagWinsOverRestSiteTag() {
        var both = new RewardOrigin(false, true, false, true);
        Assert.Equal(AuthorityMode.Unskippable, AuthorityRules.Resolve(both, true, NonCombatRewardMode.Mixed));
    }

    [Fact]
    public void RulesActive_OnlyWhenRegisteredAndNotFree() {
        Assert.True(AuthorityRules.RulesActive(true, NonCombatRewardMode.Mixed));
        Assert.True(AuthorityRules.RulesActive(true, NonCombatRewardMode.RemoveOne));
        Assert.False(AuthorityRules.RulesActive(true, NonCombatRewardMode.Free));
        Assert.False(AuthorityRules.RulesActive(false, NonCombatRewardMode.Mixed));
    }
}
