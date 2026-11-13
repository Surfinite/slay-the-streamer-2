using SlayTheStreamer2.Game.CharSelect;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.CharSelect;

public class RandomCharacterRulesTests {
    // The game's GameMode enum on the default branch and on the Beta; value 3 is
    // Custom on one and Dread on the other.
    private enum StableGameMode { None, Standard, Daily, Custom }
    private enum BetaGameMode { None, Standard, Daily, Dread, Blitz }

    [Fact]
    public void MustRefuseEmbark_InDread_WhenAnyLobbyPlayerIsStillOnRandom() {
        Assert.True(RandomCharacterRules.MustRefuseEmbark(BetaGameMode.Dread, new[] { false, true }));
    }

    [Fact]
    public void MustNotRefuseEmbark_InDread_WhenEveryPlayerHasAConcreteCharacter() {
        Assert.False(RandomCharacterRules.MustRefuseEmbark(BetaGameMode.Dread, new[] { false, false }));
    }

    [Theory]
    [InlineData(BetaGameMode.Standard)]
    [InlineData(BetaGameMode.Blitz)]
    public void MustNotRefuseEmbark_OutsideDread_EvenWithARandomPick(object mode) {
        Assert.False(RandomCharacterRules.MustRefuseEmbark(mode, new[] { true }));
    }

    [Fact]
    public void MustNotRefuseEmbark_ForStableCustom_ThoughItSharesDreadsNumericValue() {
        Assert.False(RandomCharacterRules.MustRefuseEmbark(StableGameMode.Custom, new[] { true }));
    }

    [Fact]
    public void MustNotRefuseEmbark_WhenTheModeIsUnknown() {
        Assert.False(RandomCharacterRules.MustRefuseEmbark(null, new[] { true }));
    }
}
