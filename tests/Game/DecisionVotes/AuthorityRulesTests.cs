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
    [InlineData(true)]
    [InlineData(false)]
    public void CombatTagUnregistered_EverythingIsNormalVote(bool combatOnly) {
        foreach (var o in new[] { Combat, AncientRelic, ShopRelic, RestSite, Untagged })
            Assert.Equal(AuthorityMode.NormalVote, AuthorityRules.Resolve(o, combatTagRegistered: false, combatCardVotesOnly: combatOnly));
    }

    [Fact]
    public void CombatOnlyOn_CombatVotes_EverythingElseFree() {
        Assert.Equal(AuthorityMode.NormalVote, AuthorityRules.Resolve(Combat, true, true));
        foreach (var o in new[] { AncientRelic, ShopRelic, RestSite, Untagged })
            Assert.Equal(AuthorityMode.Free, AuthorityRules.Resolve(o, true, true));
    }

    [Fact]
    public void CombatOnlyOff_TableApplies() {
        Assert.Equal(AuthorityMode.NormalVote, AuthorityRules.Resolve(Combat, true, false));
        Assert.Equal(AuthorityMode.RemoveOne, AuthorityRules.Resolve(AncientRelic, true, false));
        Assert.Equal(AuthorityMode.Unskippable, AuthorityRules.Resolve(ShopRelic, true, false));
        Assert.Equal(AuthorityMode.RemoveOne, AuthorityRules.Resolve(RestSite, true, false));
        Assert.Equal(AuthorityMode.Unskippable, AuthorityRules.Resolve(Untagged, true, false));
    }

    [Fact]
    public void CombatTagWinsOverRelicTag() {
        var both = new RewardOrigin(true, true, true, false);
        Assert.Equal(AuthorityMode.NormalVote, AuthorityRules.Resolve(both, true, false));
    }

    [Fact]
    public void RelicTagWinsOverRestSiteTag() {
        var both = new RewardOrigin(false, true, false, true);
        Assert.Equal(AuthorityMode.Unskippable, AuthorityRules.Resolve(both, true, false));
    }

    [Fact]
    public void RulesActive_OnlyWhenRegisteredAndCombatOnlyOff() {
        Assert.True(AuthorityRules.RulesActive(true, false));
        Assert.False(AuthorityRules.RulesActive(true, true));
        Assert.False(AuthorityRules.RulesActive(false, false));
    }
}
