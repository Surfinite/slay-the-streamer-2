using SlayTheStreamer2.Game.DecisionVotes;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

public class GameModeNamesTests {
    // The game's GameMode enum on the default branch (v0.107.1) and on the Beta
    // (v0.113.0). Value 3 is Custom on one and Dread on the other.
    private enum StableGameMode { None, Standard, Daily, Custom }
    private enum BetaGameMode { None, Standard, Daily, Dread, Blitz }

    [Fact]
    public void IsDread_IsTrue_ForTheBetaDreadMember() {
        Assert.True(GameModeNames.IsDread(BetaGameMode.Dread));
    }

    [Fact]
    public void IsDread_IsFalse_ForStableCustom_ThoughItSharesDreadsNumericValue() {
        Assert.Equal((int)BetaGameMode.Dread, (int)StableGameMode.Custom);
        Assert.False(GameModeNames.IsDread(StableGameMode.Custom));
    }

    [Fact]
    public void IsBlitz_IsTrue_ForTheBetaBlitzMember() {
        Assert.True(GameModeNames.IsBlitz(BetaGameMode.Blitz));
    }

    [Theory]
    [InlineData(BetaGameMode.Standard)]
    [InlineData(BetaGameMode.Daily)]
    [InlineData(BetaGameMode.Dread)]
    public void IsBlitz_IsFalse_ForEveryOtherBetaMode(object mode) {
        Assert.False(GameModeNames.IsBlitz(mode));
    }

    [Fact]
    public void IsDread_IsFalse_ForStandard() {
        Assert.False(GameModeNames.IsDread(BetaGameMode.Standard));
    }

    [Fact]
    public void BothAreFalse_ForNull() {
        Assert.False(GameModeNames.IsDread(null));
        Assert.False(GameModeNames.IsBlitz(null));
    }
}
