using System;
using System.IO;
using System.Linq;
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
            new ChatSettings("foo", new ChatCredentials("bar", "abc123"), 1, null),
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

    [Fact]
    public void Load_EmptyFile_ReturnsMalformed() {
        var path = WriteTempJson("");
        try {
            Assert.IsType<SettingsResult.Malformed>(ModSettings.Load(path));
        } finally { File.Delete(path); }
    }

    [Fact]
    public void Load_MalformedJson_ReturnsMalformed() {
        var path = WriteTempJson("{ this is not json");
        try {
            Assert.IsType<SettingsResult.Malformed>(ModSettings.Load(path));
        } finally { File.Delete(path); }
    }

    [Fact]
    public void Load_WhitespaceOnlyChannel_ReturnsMalformed() {
        var path = WriteTempJson("""
        { "schemaVersion": 1, "channel": "   ", "username": "y", "oauthToken": "abc123def456ghi789jkl012mno345" }
        """);
        try {
            Assert.IsType<SettingsResult.Malformed>(ModSettings.Load(path));
        } finally { File.Delete(path); }
    }

    [Fact]
    public void Load_CardSkipsPerActMissing_UsesDefault() {
        var path = WriteTempJson("""
        {
            "schemaVersion": 1,
            "channel": "#foo",
            "username": "bot",
            "oauthToken": "abcdefghijklmnopqrstuvwxyz1234"
        }
        """);
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.Equal(1, success.Settings.CardSkipsPerAct);   // default
        } finally { File.Delete(path); }
    }

    [Fact]
    public void Load_CardSkipsPerActInvalid_WarnsAndUsesDefault() {
        var path = WriteTempJson("""
        {
            "schemaVersion": 1, "channel": "#foo", "username": "bot",
            "oauthToken": "abcdefghijklmnopqrstuvwxyz1234",
            "cardSkipsPerAct": "not a number"
        }
        """);
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.Equal(1, success.Settings.CardSkipsPerAct);
            Assert.Contains(success.Warnings, w => w.Contains("cardSkipsPerAct"));
        } finally { File.Delete(path); }
    }

    [Fact]
    public void Load_CardSkipsPerActNegativeFive_ClampsToMinusOne() {
        var path = WriteTempJson("""
        {
            "schemaVersion": 1, "channel": "#foo", "username": "bot",
            "oauthToken": "abcdefghijklmnopqrstuvwxyz1234",
            "cardSkipsPerAct": -5
        }
        """);
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.Equal(-1, success.Settings.CardSkipsPerAct);
            Assert.Contains(success.Warnings, w => w.Contains("clamped"));
        } finally { File.Delete(path); }
    }

    [Fact]
    public void Load_CardSkipsPerActZero_IsStrict() {
        var path = WriteTempJson("""
        {
            "schemaVersion": 1, "channel": "#foo", "username": "bot",
            "oauthToken": "abcdefghijklmnopqrstuvwxyz1234",
            "cardSkipsPerAct": 0
        }
        """);
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.Equal(0, success.Settings.CardSkipsPerAct);
            Assert.DoesNotContain(success.Warnings, w => w.Contains("cardSkipsPerAct"));
        } finally { File.Delete(path); }
    }

    [Fact]
    public void Load_CardSkipsPerActPositive_Parses() {
        var path = WriteTempJson("""
        {
            "schemaVersion": 1, "channel": "#foo", "username": "bot",
            "oauthToken": "abcdefghijklmnopqrstuvwxyz1234",
            "cardSkipsPerAct": 3
        }
        """);
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.Equal(3, success.Settings.CardSkipsPerAct);
        } finally { File.Delete(path); }
    }

    [Fact]
    public void Load_CardSkipsPerActMinusOne_IsUnlimited() {
        var path = WriteTempJson("""
        {
            "schemaVersion": 1, "channel": "#foo", "username": "bot",
            "oauthToken": "abcdefghijklmnopqrstuvwxyz1234",
            "cardSkipsPerAct": -1
        }
        """);
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.Equal(-1, success.Settings.CardSkipsPerAct);
            Assert.DoesNotContain(success.Warnings, w => w.Contains("cardSkipsPerAct"));
        } finally { File.Delete(path); }
    }

    // --- youtubeChannelId (D6 v4 trim-first; YT-only disable on malformed) ---

    /// <summary>Builds the required-fields JSON, optionally splicing in a youtubeChannelId fragment.</summary>
    private static string BaseJsonWithOptionalYouTube(string? youtubeFragment) =>
        "{\"schemaVersion\":1,\"channel\":\"foo\",\"username\":\"bar\",\"oauthToken\":\"abcdefghijklmnopqrstuvwxyz1234\",\"cardSkipsPerAct\":1"
        + (youtubeFragment is null ? "" : "," + youtubeFragment)
        + "}";

    [Fact]
    public void YoutubeChannelId_Absent_Returns_Success_With_Null() {
        var path = WriteTempJson(BaseJsonWithOptionalYouTube(null));
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.Null(success.Settings.YoutubeChannelId);
        } finally { File.Delete(path); }
    }

    [Fact]
    public void YoutubeChannelId_JsonNull_Returns_Success_With_Null() {
        var path = WriteTempJson(BaseJsonWithOptionalYouTube("\"youtubeChannelId\":null"));
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.Null(success.Settings.YoutubeChannelId);
        } finally { File.Delete(path); }
    }

    [Fact]
    public void YoutubeChannelId_EmptyString_Returns_Success_With_Null() {
        var path = WriteTempJson(BaseJsonWithOptionalYouTube("\"youtubeChannelId\":\"\""));
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.Null(success.Settings.YoutubeChannelId);
        } finally { File.Delete(path); }
    }

    [Fact]
    public void YoutubeChannelId_WhitespaceOnly_Returns_Success_With_Null_No_Warning() {
        var path = WriteTempJson(BaseJsonWithOptionalYouTube("\"youtubeChannelId\":\"   \""));
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.Null(success.Settings.YoutubeChannelId);
            Assert.DoesNotContain(success.Warnings, w => w.Contains("youtubeChannelId", StringComparison.OrdinalIgnoreCase));
        } finally { File.Delete(path); }
    }

    [Fact]
    public void YoutubeChannelId_Valid_NonEmpty_Preserved() {
        var path = WriteTempJson(BaseJsonWithOptionalYouTube("\"youtubeChannelId\":\"UCabc123def456\""));
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.Equal("UCabc123def456", success.Settings.YoutubeChannelId);
        } finally { File.Delete(path); }
    }

    [Fact]
    public void YoutubeChannelId_WhitespaceSurroundingValid_Trimmed() {
        var path = WriteTempJson(BaseJsonWithOptionalYouTube("\"youtubeChannelId\":\"  UCabc123def456  \""));
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.Equal("UCabc123def456", success.Settings.YoutubeChannelId);
        } finally { File.Delete(path); }
    }

    [Fact]
    public void YoutubeChannelId_ContainsControlChar_Returns_Success_With_Warning_And_Null() {
        //  is SOH — a non-whitespace control character. Per D6 v4, this disables YT only,
        // not the whole mod (Success with warning + null YT, Twitch still loads).
        var path = WriteTempJson(BaseJsonWithOptionalYouTube("\"youtubeChannelId\":\"UC\\u0001abc\""));
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.Null(success.Settings.YoutubeChannelId);
            Assert.Contains(success.Warnings, w => w.Contains("youtubeChannelId", StringComparison.OrdinalIgnoreCase));
        } finally { File.Delete(path); }
    }

    // --- voteOnActVariant + forceL3PopupFallback (B.3.2 act-variant vote toggles) ---

    [Fact]
    public void Load_VoteOnActVariantAndForceL3Missing_UseDefaults() {
        var path = WriteTempJson("""
        {
            "schemaVersion": 1, "channel": "x", "username": "y",
            "oauthToken": "abc123def456ghi789jkl012mno345"
        }
        """);
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.True(success.Settings.VoteOnActVariant);
            Assert.False(success.Settings.ForceL3PopupFallback);
            Assert.DoesNotContain(success.Warnings, w => w.Contains("voteOnActVariant"));
            Assert.DoesNotContain(success.Warnings, w => w.Contains("forceL3PopupFallback"));
        } finally { File.Delete(path); }
    }

    [Fact]
    public void Load_VoteOnActVariantFalse_Parses() {
        var path = WriteTempJson("""
        {
            "schemaVersion": 1, "channel": "x", "username": "y",
            "oauthToken": "abc123def456ghi789jkl012mno345",
            "voteOnActVariant": false
        }
        """);
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.False(success.Settings.VoteOnActVariant);
        } finally { File.Delete(path); }
    }

    [Fact]
    public void Load_ForceL3PopupFallbackTrue_Parses() {
        var path = WriteTempJson("""
        {
            "schemaVersion": 1, "channel": "x", "username": "y",
            "oauthToken": "abc123def456ghi789jkl012mno345",
            "forceL3PopupFallback": true
        }
        """);
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.True(success.Settings.ForceL3PopupFallback);
        } finally { File.Delete(path); }
    }

    [Fact]
    public void Load_VoteOnActVariantNotBool_WarnsAndUsesDefault() {
        var path = WriteTempJson("""
        {
            "schemaVersion": 1, "channel": "x", "username": "y",
            "oauthToken": "abc123def456ghi789jkl012mno345",
            "voteOnActVariant": "yes"
        }
        """);
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.True(success.Settings.VoteOnActVariant);   // default
            Assert.Contains(success.Warnings, w => w.Contains("voteOnActVariant"));
        } finally { File.Delete(path); }
    }

    [Fact]
    public void Load_ForceL3PopupFallbackNotBool_WarnsAndUsesDefault() {
        var path = WriteTempJson("""
        {
            "schemaVersion": 1, "channel": "x", "username": "y",
            "oauthToken": "abc123def456ghi789jkl012mno345",
            "forceL3PopupFallback": 1
        }
        """);
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.False(success.Settings.ForceL3PopupFallback);   // default
            Assert.Contains(success.Warnings, w => w.Contains("forceL3PopupFallback"));
        } finally { File.Delete(path); }
    }

    [Fact]
    public void Load_MissingVoteDurationSeconds_DefaultsTo30() {
        var path = WriteTempJson("""
        {
            "schemaVersion": 1,
            "channel": "x",
            "username": "x",
            "oauthToken": "xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        }
        """);
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.Equal(30, success.Settings.VoteDurationSeconds);
        } finally { File.Delete(path); }
    }

    [Fact]
    public void Load_VoteDurationSeconds_ReadsExplicitValue() {
        var path = WriteTempJson("""
        {
            "schemaVersion": 1,
            "channel": "x",
            "username": "x",
            "oauthToken": "xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "voteDurationSeconds": 60
        }
        """);
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.Equal(60, success.Settings.VoteDurationSeconds);
        } finally { File.Delete(path); }
    }

    [Fact]
    public void Load_VoteDurationSeconds_BelowMin_ClampsAndWarns() {
        var path = WriteTempJson("""
        {
            "schemaVersion": 1,
            "channel": "x",
            "username": "x",
            "oauthToken": "xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "voteDurationSeconds": 5
        }
        """);
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.Equal(10, success.Settings.VoteDurationSeconds);
            Assert.Contains(success.Warnings, w => w.Contains("voteDurationSeconds"));
        } finally { File.Delete(path); }
    }

    [Fact]
    public void Load_VoteDurationSeconds_AboveMax_ClampsAndWarns() {
        var path = WriteTempJson("""
        {
            "schemaVersion": 1,
            "channel": "x",
            "username": "x",
            "oauthToken": "xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "voteDurationSeconds": 500
        }
        """);
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.Equal(120, success.Settings.VoteDurationSeconds);
            Assert.Contains(success.Warnings, w => w.Contains("voteDurationSeconds"));
        } finally { File.Delete(path); }
    }

    [Fact]
    public void Load_MissingCardSkipAsVoteOption_DefaultsToTrue() {
        var path = WriteTempJson("""
        {
            "schemaVersion": 1,
            "channel": "x",
            "username": "x",
            "oauthToken": "xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        }
        """);
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.True(success.Settings.CardSkipAsVoteOption);
        } finally { File.Delete(path); }
    }

    [Fact]
    public void Load_CardSkipAsVoteOption_ReadsExplicitFalse() {
        var path = WriteTempJson("""
        {
            "schemaVersion": 1,
            "channel": "x",
            "username": "x",
            "oauthToken": "xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "cardSkipAsVoteOption": false
        }
        """);
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.False(success.Settings.CardSkipAsVoteOption);
        } finally { File.Delete(path); }
    }

    [Fact]
    public void Load_CardSkipAsVoteOption_NonBoolean_WarnsAndDefaults() {
        var path = WriteTempJson("""
        {
            "schemaVersion": 1,
            "channel": "x",
            "username": "x",
            "oauthToken": "xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "cardSkipAsVoteOption": "yes"
        }
        """);
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.True(success.Settings.CardSkipAsVoteOption);
            Assert.Contains(success.Warnings, w => w.Contains("cardSkipAsVoteOption"));
        } finally { File.Delete(path); }
    }

    // --- showVoteTag (A.3 conditional default based on youtubeChannelId) ---

    [Fact]
    public void Load_ShowVoteTag_MissingWithoutYouTube_DefaultsToFalse() {
        var path = WriteTempJson(new {
            schemaVersion = 1,
            channel = "x",
            username = "x",
            oauthToken = "x" + new string('a', 29)
        });
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.False(success.Settings.ShowVoteTag);
        } finally { File.Delete(path); }
    }

    [Fact]
    public void Load_ShowVoteTag_MissingWithYouTube_DefaultsToTrue() {
        var path = WriteTempJson(new {
            schemaVersion = 1,
            channel = "x",
            username = "x",
            oauthToken = "x" + new string('a', 29),
            youtubeChannelId = "UCabcdefghijklmnopqrstu"
        });
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.True(success.Settings.ShowVoteTag);
        } finally { File.Delete(path); }
    }

    [Fact]
    public void Load_ShowVoteTag_ExplicitFalse_OverridesYouTubeDefault() {
        var path = WriteTempJson(new {
            schemaVersion = 1,
            channel = "x",
            username = "x",
            oauthToken = "x" + new string('a', 29),
            youtubeChannelId = "UCabcdefghijklmnopqrstu",
            showVoteTag = false
        });
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.False(success.Settings.ShowVoteTag);
        } finally { File.Delete(path); }
    }

    [Fact]
    public void Load_ShowVoteTag_ExplicitTrue_OverridesTwitchOnlyDefault() {
        var path = WriteTempJson(new {
            schemaVersion = 1,
            channel = "x",
            username = "x",
            oauthToken = "x" + new string('a', 29),
            showVoteTag = true
        });
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.True(success.Settings.ShowVoteTag);
        } finally { File.Delete(path); }
    }

    [Fact]
    public void Load_ShowVoteTag_NonBoolean_WarnsAndAppliesConditionalDefault() {
        var path = WriteTempJson(new {
            schemaVersion = 1,
            channel = "x",
            username = "x",
            oauthToken = "x" + new string('a', 29),
            youtubeChannelId = "UCabcdefghijklmnopqrstu",
            showVoteTag = "maybe"
        });
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.True(success.Settings.ShowVoteTag);  // conditional default fires
            Assert.Contains(success.Warnings, w => w.Contains("showVoteTag"));
        } finally { File.Delete(path); }
    }

    // --- allowSameBossTwice (task 2b: A10 second-boss vote may repeat the first boss) ---

    [Fact]
    public void Load_MissingAllowSameBossTwice_DefaultsToFalse() {
        var path = WriteTempJson("""
        {
            "schemaVersion": 1, "channel": "x", "username": "y",
            "oauthToken": "abc123def456ghi789jkl012mno345"
        }
        """);
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.False(success.Settings.AllowSameBossTwice);
            Assert.DoesNotContain(success.Warnings, w => w.Contains("allowSameBossTwice"));
        } finally { File.Delete(path); }
    }

    [Fact]
    public void Load_AllowSameBossTwice_ReadsExplicitTrue() {
        var path = WriteTempJson("""
        {
            "schemaVersion": 1, "channel": "x", "username": "y",
            "oauthToken": "abc123def456ghi789jkl012mno345",
            "allowSameBossTwice": true
        }
        """);
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.True(success.Settings.AllowSameBossTwice);
        } finally { File.Delete(path); }
    }

    [Fact]
    public void Load_AllowSameBossTwice_NonBoolean_WarnsAndDefaults() {
        var path = WriteTempJson("""
        {
            "schemaVersion": 1, "channel": "x", "username": "y",
            "oauthToken": "abc123def456ghi789jkl012mno345",
            "allowSameBossTwice": "yes"
        }
        """);
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.False(success.Settings.AllowSameBossTwice);   // default
            Assert.Contains(success.Warnings, w => w.Contains("allowSameBossTwice"));
        } finally { File.Delete(path); }
    }

    // --- relicChoices (bossy-relics task 2: relic-reward option count, 1-4, default 1) ---

    [Theory]
    [InlineData("\"relicChoices\": 3,", 3, false)]
    [InlineData("\"relicChoices\": 1,", 1, false)]
    [InlineData("\"relicChoices\": 0,", 1, true)]    // below min -> clamp + warning
    [InlineData("\"relicChoices\": 9,", 4, true)]    // above max -> clamp + warning
    [InlineData("\"relicChoices\": \"two\",", 1, true)] // non-int -> default + warning
    [InlineData("", 1, false)]                        // missing -> default, no warning
    public void RelicChoices_parses_clamps_and_defaults(string fragment, int expected, bool expectWarning) {
        var path = WriteTempJson($$"""
        {
            "schemaVersion": 1, "channel": "x", "username": "y",
            "oauthToken": "abc123def456ghi789jkl012mno345",
            {{fragment}}
            "cardSkipsPerAct": 1
        }
        """);
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.Equal(expected, success.Settings.RelicChoices);
            Assert.Equal(expectWarning, success.Warnings.Any(w => w.Contains("relicChoices")));
        } finally { File.Delete(path); }
    }

    private static string WriteTempJson(string contents) {
        var path = Path.Combine(Path.GetTempPath(), "modsettings_test_" + Guid.NewGuid() + ".json");
        File.WriteAllText(path, contents);
        return path;
    }

    private static string WriteTempJson(object obj) {
        return WriteTempJson(System.Text.Json.JsonSerializer.Serialize(obj));
    }
}
