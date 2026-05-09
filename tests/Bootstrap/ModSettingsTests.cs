using System;
using System.IO;
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

    [Fact]
    public void Load_NonexistentPath_ReturnsMissing() {
        var nonexistent = Path.Combine(Path.GetTempPath(), "nonexistent_" + Guid.NewGuid() + ".json");
        var result = ModSettings.Load(nonexistent);
        var missing = Assert.IsType<SettingsResult.Missing>(result);
        Assert.Equal(nonexistent, missing.Path);
    }

    [Fact]
    public void Load_ValidJson_ReturnsSuccess() {
        var path = WriteTempJson("""
        {
            "schemaVersion": 1,
            "channel": "surfinite",
            "username": "surfinitebot",
            "oauthToken": "abc123def456ghi789jkl012mno345"
        }
        """);
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.Equal("surfinite", success.Settings.Channel);
            Assert.Equal("surfinitebot", success.Settings.Credentials.Username);
            Assert.Equal("abc123def456ghi789jkl012mno345", success.Settings.Credentials.OauthToken);
        } finally {
            File.Delete(path);
        }
    }

    private static string WriteTempJson(string contents) {
        var path = Path.Combine(Path.GetTempPath(), "modsettings_test_" + Guid.NewGuid() + ".json");
        File.WriteAllText(path, contents);
        return path;
    }
}
