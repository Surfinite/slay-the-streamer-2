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

    [Fact]
    public void Load_MissingSchemaVersion_ReturnsMalformed() {
        var path = WriteTempJson("""
        { "channel": "x", "username": "y", "oauthToken": "abc123def456ghi789jkl012mno345" }
        """);
        try {
            var result = ModSettings.Load(path);
            var malformed = Assert.IsType<SettingsResult.Malformed>(result);
            Assert.Contains("schemaVersion", malformed.Reason);
        } finally {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_UnknownSchemaVersion_ReturnsMalformed() {
        var path = WriteTempJson("""
        { "schemaVersion": 999, "channel": "x", "username": "y", "oauthToken": "abc123def456ghi789jkl012mno345" }
        """);
        try {
            var result = ModSettings.Load(path);
            var malformed = Assert.IsType<SettingsResult.Malformed>(result);
            Assert.Contains("999", malformed.Reason);
        } finally {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_CurrentSchemaVersion_ReturnsSuccess() {
        var path = WriteTempJson($$"""
        { "schemaVersion": {{ModSettings.CurrentSchemaVersion}}, "channel": "x", "username": "y", "oauthToken": "abc123def456ghi789jkl012mno345" }
        """);
        try {
            Assert.IsType<SettingsResult.Success>(ModSettings.Load(path));
        } finally {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("foo")]
    [InlineData("#foo")]
    [InlineData("https://twitch.tv/foo")]
    [InlineData("https://www.twitch.tv/foo")]
    [InlineData("http://twitch.tv/foo/")]
    public void Load_ChannelForms_NormaliseToBareLowercase(string channelInput) {
        var path = WriteTempJson($$"""
        { "schemaVersion": 1, "channel": "{{channelInput}}", "username": "y", "oauthToken": "abc123def456ghi789jkl012mno345" }
        """);
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.Equal("foo", success.Settings.Channel);
        } finally { File.Delete(path); }
    }

    [Fact]
    public void Load_ChannelWithUrlForm_AddsWarning() {
        var path = WriteTempJson("""
        { "schemaVersion": 1, "channel": "https://twitch.tv/Surfinite", "username": "y", "oauthToken": "abc123def456ghi789jkl012mno345" }
        """);
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.Contains(success.Warnings, w => w.Contains("normalised") || w.Contains("normalized"));
        } finally { File.Delete(path); }
    }

    [Fact]
    public void Load_UsernameWithUppercase_LowercasesAndWarns() {
        var path = WriteTempJson("""
        { "schemaVersion": 1, "channel": "x", "username": "SurfiniteBot", "oauthToken": "abc123def456ghi789jkl012mno345" }
        """);
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.Equal("surfinitebot", success.Settings.Credentials.Username);
            Assert.Contains(success.Warnings, w => w.Contains("SurfiniteBot") || w.Contains("lowercased"));
        } finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("oauth:abc123def456ghi789jkl012mno345")]
    [InlineData("abc123def456ghi789jkl012mno345")]
    public void Load_OauthBothForms_NormaliseToBare(string token) {
        var path = WriteTempJson($$"""
        { "schemaVersion": 1, "channel": "x", "username": "y", "oauthToken": "{{token}}" }
        """);
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.Equal("abc123def456ghi789jkl012mno345", success.Settings.Credentials.OauthToken);
        } finally { File.Delete(path); }
    }

    [Fact]
    public void Load_OauthWithUnusualShape_ReturnsSuccessWithWarning() {
        var path = WriteTempJson("""
        { "schemaVersion": 1, "channel": "x", "username": "y", "oauthToken": "ABC123DEF456GHI789JKL012MNO345" }
        """);
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.Contains(success.Warnings, w => w.Contains("oauth") || w.Contains("token"));
        } finally { File.Delete(path); }
    }

    [Fact]
    public void Load_OauthWithWhitespace_ReturnsMalformed() {
        var path = WriteTempJson("""
        { "schemaVersion": 1, "channel": "x", "username": "y", "oauthToken": "abc 123" }
        """);
        try {
            Assert.IsType<SettingsResult.Malformed>(ModSettings.Load(path));
        } finally { File.Delete(path); }
    }

    private static string WriteTempJson(string contents) {
        var path = Path.Combine(Path.GetTempPath(), "modsettings_test_" + Guid.NewGuid() + ".json");
        File.WriteAllText(path, contents);
        return path;
    }
}
