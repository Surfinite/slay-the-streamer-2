using SlayTheStreamer2.Game.Rewards;
using Xunit;

namespace SlayTheStreamer2.Tests.Rewards;

public class RelicChoicePlannerTests {
    [Theory]
    [InlineData(int.MinValue, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(4, 4)]
    [InlineData(9, 4)]
    public void Clamp_bounds_to_1_through_4(int requested, int expected) =>
        Assert.Equal(expected, RelicChoicePlanner.Clamp(requested));

    [Theory]
    [InlineData(1, 1, 4, 0)]   // N=1: never add extras
    [InlineData(4, 1, 4, 3)]   // chest with 1 vanilla relic, N=4 -> 3 extras
    [InlineData(4, 0, 4, 0)]   // empty chest: leave alone
    [InlineData(4, 2, 4, 2)]   // already 2 present (defensive): cap total at 4
    [InlineData(2, 1, 4, 1)]
    [InlineData(4, 4, 4, 0)]   // already at cap
    public void ExtraCount_respects_cap_and_existing(int choices, int existing, int cap, int expected) =>
        Assert.Equal(expected, RelicChoicePlanner.ExtraCount(choices, existing, cap));

    [Fact]
    public void OfferSeed_is_deterministic_and_context_sensitive() {
        var a = RelicChoicePlanner.OfferSeed("SEED123", "bossy-elite", 1, 12);
        var b = RelicChoicePlanner.OfferSeed("SEED123", "bossy-elite", 1, 12);
        var c = RelicChoicePlanner.OfferSeed("SEED123", "bossy-chest", 1, 12);
        var d = RelicChoicePlanner.OfferSeed("SEED123", "bossy-elite", 1, 13);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(a, d);
    }

    [Fact]
    public void OfferSeed_tolerates_null_run_seed() {
        // Pre-run / weird states: must not throw; still deterministic.
        Assert.Equal(
            RelicChoicePlanner.OfferSeed(null, "bossy-chest", 0, 0),
            RelicChoicePlanner.OfferSeed(null, "bossy-chest", 0, 0));
    }

    [Theory]
    [InlineData(true, 0, true)]     // profile's first run: vanilla's forced tutorial Gorget
    [InlineData(true, 1, false)]    // natural Gorget draw on a later run: expand it (FrostPrime, 2026-09-28)
    [InlineData(true, 250, false)]
    [InlineData(false, 0, false)]   // first run, but not Gorget: not the tutorial relic
    public void IsTutorialChestRelic_only_on_first_run_gorget(bool isGorget, int numberOfRuns, bool expected) =>
        Assert.Equal(expected, RelicChoicePlanner.IsTutorialChestRelic(isGorget, numberOfRuns));
}
