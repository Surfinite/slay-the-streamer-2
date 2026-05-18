using SlayTheStreamer2.Game.DecisionVotes;
using SlayTheStreamer2.Game.Ui;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

public sealed class ActVariantVoteResolverTests {

    [Fact]
    public void BuildCandidates_returns_exactly_two_options() {
        var candidates = ActVariantVoteResolver.BuildCandidates();
        Assert.Equal(2, candidates.Count);
    }

    [Fact]
    public void BuildCandidates_keys_are_lowercase_overgrowth_and_underdocks() {
        var candidates = ActVariantVoteResolver.BuildCandidates();
        Assert.Equal("overgrowth", candidates[0].Key);
        Assert.Equal("underdocks", candidates[1].Key);
    }

    [Fact]
    public void BuildCandidates_indices_are_stable_zero_and_one() {
        var candidates = ActVariantVoteResolver.BuildCandidates();
        Assert.Equal(0, candidates[0].Index);
        Assert.Equal(1, candidates[1].Index);
    }

    [Fact]
    public void BuildCandidates_titles_are_non_empty() {
        var candidates = ActVariantVoteResolver.BuildCandidates();
        Assert.False(string.IsNullOrWhiteSpace(candidates[0].Title));
        Assert.False(string.IsNullOrWhiteSpace(candidates[1].Title));
    }

    [Fact]
    public void BuildCandidates_fallback_color_matches_six_digit_hex() {
        var candidates = ActVariantVoteResolver.BuildCandidates();
        var hexPattern = new Regex("^[0-9A-Fa-f]{6}$");
        Assert.Matches(hexPattern, candidates[0].FallbackColorHex);
        Assert.Matches(hexPattern, candidates[1].FallbackColorHex);
    }

    [Fact]
    public void BuildCandidates_returns_independent_list_instances_each_call() {
        var a = ActVariantVoteResolver.BuildCandidates();
        var b = ActVariantVoteResolver.BuildCandidates();
        Assert.NotSame(a, b);
    }

    [Theory]
    [InlineData(0, "overgrowth")]
    [InlineData(1, "underdocks")]
    public void ResolveWinnerKey_valid_index_returns_matching_key(int idx, string expected) {
        var candidates = ActVariantVoteResolver.BuildCandidates();
        Assert.Equal(expected, ActVariantVoteResolver.ResolveWinnerKey(candidates, idx));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(99)]
    public void ResolveWinnerKey_null_or_out_of_range_returns_random(int? idx) {
        var candidates = ActVariantVoteResolver.BuildCandidates();
        Assert.Equal("random", ActVariantVoteResolver.ResolveWinnerKey(candidates, idx));
    }
}
