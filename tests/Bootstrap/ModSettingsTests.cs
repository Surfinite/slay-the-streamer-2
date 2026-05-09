using SlayTheStreamer2.Game.Bootstrap;
using SlayTheStreamer2.Ti.Chat;
using Xunit;

namespace SlayTheStreamer2.Tests.Bootstrap;

public class ModSettingsTests {
    [Fact]
    public void SettingsResult_TypesExist() {
        SettingsResult missing = new SettingsResult.Missing("x");
        SettingsResult malformed = new SettingsResult.Malformed("x", "y");
        SettingsResult success = new SettingsResult.Success(
            new ChatSettings("foo", new ChatCredentials("bar", "abc123")),
            new[] { "warn" });
        Assert.NotNull(missing);
        Assert.NotNull(malformed);
        Assert.NotNull(success);
    }
}
