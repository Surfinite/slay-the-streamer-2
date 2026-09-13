using SlayTheStreamer2.Game.Bootstrap;
using Xunit;

namespace SlayTheStreamer2.Tests.Bootstrap;

public class NonCombatRewardModeTests {
    [Theory]
    [InlineData(NonCombatRewardMode.Free, "free")]
    [InlineData(NonCombatRewardMode.RemoveOne, "removeOne")]
    [InlineData(NonCombatRewardMode.Mixed, "mixed")]
    public void ToJson_RoundTrips(NonCombatRewardMode mode, string json) {
        Assert.Equal(json, NonCombatRewardModes.ToJson(mode));
        Assert.True(NonCombatRewardModes.TryParse(json, out var back));
        Assert.Equal(mode, back);
    }

    [Theory]
    [InlineData(" Free ")]
    [InlineData("REMOVEONE")]
    [InlineData("remove_one")]
    public void TryParse_IsForgivingAboutCaseAndSpaces(string s) => Assert.True(NonCombatRewardModes.TryParse(s, out _));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("banana")]
    public void TryParse_RejectsUnknown(string? s) => Assert.False(NonCombatRewardModes.TryParse(s, out _));
}
