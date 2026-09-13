using SlayTheStreamer2.Game.DecisionVotes;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

public class RemovalStatusTextTests {
    private const string Red = RemovalStatusText.RedHex;

    [Fact]
    public void Prompt_PaintsOnlyTheWordRemoval() {
        Assert.Equal($"Click any option to start the [color={Red}]removal[/color] vote.", RemovalStatusText.Prompt());
    }

    [Fact]
    public void AfterRemoval_Card_WithBudget_OffersTheOverride() {
        var text = RemovalStatusText.AfterRemoval(isSkip: false, removedLabel: "Bash", canOverride: true, hasReroll: false);
        Assert.Equal($"Chat [color={Red}]removed[/color] Bash. Choose from the rest, or spend an override to take it.", text);
    }

    [Fact]
    public void AfterRemoval_Card_NoBudget_SaysChooseFromTheRest() {
        var text = RemovalStatusText.AfterRemoval(isSkip: false, removedLabel: "Bash", canOverride: false, hasReroll: false);
        Assert.Equal($"Chat [color={Red}]removed[/color] Bash. Choose from the rest.", text);
    }

    [Fact]
    public void AfterRemoval_Skip_WithBudget_OffersTheOverride() {
        var text = RemovalStatusText.AfterRemoval(isSkip: true, removedLabel: "Skip", canOverride: true, hasReroll: false);
        Assert.Equal($"Chat [color={Red}]removed[/color] Skip. Take a card, or spend an override to skip.", text);
    }

    [Fact]
    public void AfterRemoval_Skip_NoBudget_SaysMustTakeACard() {
        var text = RemovalStatusText.AfterRemoval(isSkip: true, removedLabel: "Skip", canOverride: false, hasReroll: false);
        Assert.Equal($"Chat [color={Red}]removed[/color] Skip. You must take a card.", text);
    }

    [Fact]
    public void AfterRemoval_WithReroll_AppendsTheRerollLine() {
        var text = RemovalStatusText.AfterRemoval(isSkip: false, removedLabel: "Bash", canOverride: false, hasReroll: true);
        Assert.EndsWith("\nYou can reroll these cards once, for free.", text);
    }

    [Fact]
    public void AfterRemoval_EscapesBbcodeInTheCardTitle() {
        var text = RemovalStatusText.AfterRemoval(isSkip: false, removedLabel: "[b]Odd[/b]", canOverride: false, hasReroll: false);
        Assert.DoesNotContain("[b]", text);
        Assert.Contains("[lb]b[rb]Odd", text);
    }

    [Fact]
    public void PopupTitle_PaintsOnlyTheWordRemove() {
        Assert.Equal($"[lb]07[rb] Chat is choosing which option to [color={Red}]remove[/color]", RemovalStatusText.PopupTitle("[07] "));
        Assert.Equal($"Chat is choosing which option to [color={Red}]remove[/color]", RemovalStatusText.PopupTitle(""));
    }

    [Fact]
    public void RedHex_MatchesTheRemovedPaint() {
        // RemovalVisuals.RemovedRed is (1, 0.28, 0.28): 0x47 = 71 = round(0.28 * 255).
        Assert.Equal("#FF4747", RemovalStatusText.RedHex);
    }
}
