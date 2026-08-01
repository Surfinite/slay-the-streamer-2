using SlayTheStreamer2.Game.DecisionVotes;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

public class VoteOverrideBudgetTests {

    [Theory]
    [InlineData("Surfinite", "Ricochet", 2, 1, "Surfinite overrode the vote and took Ricochet. 1 override remaining this act")]
    [InlineData("Surfinite", "Ricochet", 3, 2, "Surfinite overrode the vote and took Ricochet. 2 overrides remaining this act")]
    [InlineData("Surfinite", "Skip",     1, 0, "Surfinite overrode the vote and took Skip. 0 overrides remaining this act")]
    [InlineData("Surfinite", "Ricochet", -1, 2147483647, "Surfinite overrode the vote and took Ricochet.")]  // unlimited: no count
    public void FormatOverrideReceipt_covers_plural_zero_and_unlimited(
        string name, string taken, int limit, int remaining, string expected) =>
        Assert.Equal(expected, VoteOverrideBudget.FormatOverrideReceipt(name, taken, limit, remaining));

    [Theory]
    [InlineData("Surfinite", "Ricochet", "Injury", 2, 1,
        "Surfinite overrode the vote and took Ricochet. Cursed Overrides: gained Injury! 1 override remaining this act")]
    [InlineData("Surfinite", "Skip", "Doubt", 1, 0,
        "Surfinite overrode the vote and took Skip. Cursed Overrides: gained Doubt! 0 overrides remaining this act")]
    [InlineData("Surfinite", "Ricochet", "Writhe", -1, 2147483647,
        "Surfinite overrode the vote and took Ricochet. Cursed Overrides: gained Writhe!")]  // unlimited: no count
    public void FormatOverrideReceipt_appends_curse_clause_when_present(
        string name, string taken, string curse, int limit, int remaining, string expected) =>
        Assert.Equal(expected, VoteOverrideBudget.FormatOverrideReceipt(name, taken, limit, remaining, curse));

    [Fact]
    public void FormatOverrideReceipt_null_curse_is_unchanged() =>
        Assert.Equal("Surfinite overrode the vote and took Ricochet. 1 override remaining this act",
            VoteOverrideBudget.FormatOverrideReceipt("Surfinite", "Ricochet", 2, 1, null));

    [Fact]
    public void FormatResetReceipt_names_limit_and_act() =>
        Assert.Equal("Vote overrides reset to 1 for Act 2", VoteOverrideBudget.FormatResetReceipt(1, 2));

    [Fact]
    public void Observe_and_RecordUse_drive_Snapshot_through_tracker() {
        VoteOverrideBudget.ResetForTests();
        VoteOverrideBudget.Observe("SEED-A", 0);
        VoteOverrideBudget.RecordUse();
        // Limit falls back to default 1 when ModSettings.Current is null (test env).
        var snap = VoteOverrideBudget.Snapshot();
        Assert.Equal(1, snap.UsedThisAct);
        Assert.Equal(0, snap.RemainingThisAct);

        // Act change resets the counter.
        var reason = VoteOverrideBudget.Observe("SEED-A", 1);
        Assert.Equal(BudgetResetReason.ActChanged, reason);
        Assert.Equal(0, VoteOverrideBudget.Snapshot().UsedThisAct);
        VoteOverrideBudget.ResetForTests();
    }
}
