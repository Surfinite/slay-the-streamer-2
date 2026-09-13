using SlayTheStreamer2.Game.DecisionVotes;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

public class BudgetCounterTextTests {
    [Fact]
    public void Overrides_Singular() =>
        Assert.Equal("[center]Surfinite has [b][color=#EFC851]1 vote override[/color][/b] remaining this act[/center]",
            BudgetCounterText.Overrides("Surfinite", 1));

    [Fact]
    public void Overrides_PluralAndZero() {
        Assert.Contains("2 vote overrides", BudgetCounterText.Overrides("Surfinite", 2));
        Assert.Contains("0 vote overrides", BudgetCounterText.Overrides("Surfinite", 0));
    }

    [Fact]
    public void Skips_Singular() =>
        Assert.Equal("[center]Surfinite has [b][color=#87CEEB]1 card skip[/color][/b] remaining this act[/center]",
            BudgetCounterText.Skips("Surfinite", 1));
}
