using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using SlayTheStreamer2.Game.Bootstrap;
using Xunit;

namespace SlayTheStreamer2.Tests.Bootstrap;

public class SettingsBootstrapTests {

    // ---------------------------------------------------------------------
    // Template
    // ---------------------------------------------------------------------

    [Fact]
    public void Template_Parses_And_Loads_As_Unconfigured() {
        var path = WriteTemp(SettingsBootstrap.BuildTemplateJson());
        try {
            var result = ModSettings.Load(path);
            var unconfigured = Assert.IsType<SettingsResult.Unconfigured>(result);
            Assert.Equal(path, unconfigured.Path);
        } finally { File.Delete(path); }
    }

    [Fact]
    public void Template_Contains_All_Knob_Keys_With_Load_Defaults() {
        var json = JsonNode.Parse(SettingsBootstrap.BuildTemplateJson())!.AsObject();
        Assert.Equal(ModSettings.CurrentSchemaVersion, (int)json["schemaVersion"]!);
        Assert.Equal(1, (int)json["cardSkipsPerAct"]!);
        Assert.True((bool)json["voteOnActVariant"]!);
        Assert.Equal(30, (int)json["voteDurationSeconds"]!);
        Assert.True((bool)json["cardSkipAsVoteOption"]!);
        Assert.False((bool)json["showVoteTag"]!);
        Assert.False((bool)json["voteTallyOnLeft"]!);
        Assert.False((bool)json["allowSameBossTwice"]!);
        Assert.True(json.ContainsKey("youtubeChannelId"));
        Assert.Null(json["youtubeChannelId"]);
    }

    // ---------------------------------------------------------------------
    // EnsureFile — create path
    // ---------------------------------------------------------------------

    [Fact]
    public void EnsureFile_MissingFile_CreatesTemplate() {
        var path = TempPath();
        try {
            var outcome = SettingsBootstrap.EnsureFile(path);
            var created = Assert.IsType<SettingsBootstrap.Outcome.CreatedTemplate>(outcome);
            Assert.Equal(path, created.Path);
            Assert.True(File.Exists(path));
            Assert.IsType<SettingsResult.Unconfigured>(ModSettings.Load(path));
        } finally { File.Delete(path); }
    }

    // ---------------------------------------------------------------------
    // EnsureFile — additive migration
    // ---------------------------------------------------------------------

    [Fact]
    public void EnsureFile_AddsMissingKeys_WithoutTouchingExistingValues() {
        var path = WriteTemp("""
        {
            "schemaVersion": 1,
            "channel": "surfinite",
            "username": "surfinitebot",
            "oauthToken": "abc123def456ghi789jkl012mno345",
            "voteDurationSeconds": 45
        }
        """);
        try {
            var outcome = SettingsBootstrap.EnsureFile(path);
            var added = Assert.IsType<SettingsBootstrap.Outcome.AddedMissingKeys>(outcome);
            Assert.Contains("cardSkipsPerAct", added.Keys);
            Assert.Contains("allowSameBossTwice", added.Keys);

            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.Equal("surfinite", (string)json["channel"]!);
            Assert.Equal(45, (int)json["voteDurationSeconds"]!);   // user value untouched
            Assert.False((bool)json["allowSameBossTwice"]!);       // default added

            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.Equal(45, success.Settings.VoteDurationSeconds);
        } finally { CleanUp(path); }
    }

    [Fact]
    public void EnsureFile_DoesNotAdd_ShowVoteTag() {
        // showVoteTag's runtime default is conditional on youtubeChannelId; the
        // migration must not bake a static value into files that omit it.
        var path = WriteTemp("""
        {
            "schemaVersion": 1,
            "channel": "surfinite",
            "username": "surfinitebot",
            "oauthToken": "abc123def456ghi789jkl012mno345",
            "youtubeChannelId": "UCnrdFUk_XfPJooztStcHG4g"
        }
        """);
        try {
            var outcome = SettingsBootstrap.EnsureFile(path);
            Assert.IsType<SettingsBootstrap.Outcome.AddedMissingKeys>(outcome);
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.False(json.ContainsKey("showVoteTag"));

            // Conditional default still applies: YT configured → tag on.
            var success = Assert.IsType<SettingsResult.Success>(ModSettings.Load(path));
            Assert.True(success.Settings.ShowVoteTag);
        } finally { CleanUp(path); }
    }

    [Fact]
    public void EnsureFile_PreservesUnknownKeys() {
        var path = WriteTemp("""
        {
            "schemaVersion": 1,
            "channel": "surfinite",
            "username": "surfinitebot",
            "oauthToken": "abc123def456ghi789jkl012mno345",
            "forceL3PopupFallback": true,
            "someFutureKey": "kept"
        }
        """);
        try {
            SettingsBootstrap.EnsureFile(path);
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.True((bool)json["forceL3PopupFallback"]!);
            Assert.Equal("kept", (string)json["someFutureKey"]!);
        } finally { CleanUp(path); }
    }

    [Fact]
    public void EnsureFile_ExplicitNull_IsNotOverwritten() {
        var path = WriteTemp("""
        {
            "schemaVersion": 1,
            "channel": "surfinite",
            "username": "surfinitebot",
            "oauthToken": "abc123def456ghi789jkl012mno345",
            "youtubeChannelId": null
        }
        """);
        try {
            var outcome = SettingsBootstrap.EnsureFile(path);
            var added = Assert.IsType<SettingsBootstrap.Outcome.AddedMissingKeys>(outcome);
            Assert.DoesNotContain("youtubeChannelId", added.Keys);
        } finally { CleanUp(path); }
    }

    [Fact]
    public void EnsureFile_CompleteFile_IsNoChange() {
        var path = WriteTemp(SettingsBootstrap.BuildTemplateJson());
        var before = File.ReadAllText(path);
        try {
            var outcome = SettingsBootstrap.EnsureFile(path);
            Assert.IsType<SettingsBootstrap.Outcome.NoChange>(outcome);
            Assert.Equal(before, File.ReadAllText(path));
        } finally { CleanUp(path); }
    }

    [Fact]
    public void EnsureFile_MalformedJson_LeavesFileUntouched() {
        var path = WriteTemp("{ this is not json");
        try {
            var outcome = SettingsBootstrap.EnsureFile(path);
            Assert.IsType<SettingsBootstrap.Outcome.SkippedUnparseable>(outcome);
            Assert.Equal("{ this is not json", File.ReadAllText(path));
        } finally { CleanUp(path); }
    }

    [Fact]
    public void EnsureFile_ForeignSchemaVersion_LeavesFileUntouched() {
        var raw = """{ "schemaVersion": 2, "channel": "surfinite" }""";
        var path = WriteTemp(raw);
        try {
            var outcome = SettingsBootstrap.EnsureFile(path);
            Assert.IsType<SettingsBootstrap.Outcome.SkippedUnparseable>(outcome);
            Assert.Equal(raw, File.ReadAllText(path));
        } finally { CleanUp(path); }
    }

    [Fact]
    public void EnsureFile_MissingSchemaVersion_AddsIt() {
        // A creds-only hand-made file gains schemaVersion and starts loading.
        var path = WriteTemp("""
        {
            "channel": "surfinite",
            "username": "surfinitebot",
            "oauthToken": "abc123def456ghi789jkl012mno345"
        }
        """);
        try {
            var outcome = SettingsBootstrap.EnsureFile(path);
            var added = Assert.IsType<SettingsBootstrap.Outcome.AddedMissingKeys>(outcome);
            Assert.Contains("schemaVersion", added.Keys);
            Assert.IsType<SettingsResult.Success>(ModSettings.Load(path));
        } finally { CleanUp(path); }
    }

    [Fact]
    public void EnsureFile_Migration_WritesBakBackup() {
        var original = """
        {
            "schemaVersion": 1,
            "channel": "surfinite",
            "username": "surfinitebot",
            "oauthToken": "abc123def456ghi789jkl012mno345"
        }
        """;
        var path = WriteTemp(original);
        try {
            Assert.IsType<SettingsBootstrap.Outcome.AddedMissingKeys>(SettingsBootstrap.EnsureFile(path));
            Assert.True(File.Exists(path + ".bak"));
            Assert.Equal(original, File.ReadAllText(path + ".bak"));
        } finally { CleanUp(path); }
    }

    // ---------------------------------------------------------------------
    // ModSettings.Load — placeholder handling
    // ---------------------------------------------------------------------

    [Fact]
    public void Load_PartialPlaceholder_IsMalformed_NamingTheField() {
        var path = WriteTemp($$"""
        {
            "schemaVersion": 1,
            "channel": "surfinite",
            "username": "surfinitebot",
            "oauthToken": "{{SettingsBootstrap.PlaceholderOauthToken}}"
        }
        """);
        try {
            var malformed = Assert.IsType<SettingsResult.Malformed>(ModSettings.Load(path));
            Assert.Contains("oauthToken", malformed.Reason);
            Assert.Contains("placeholder", malformed.Reason);
        } finally { File.Delete(path); }
    }

    [Fact]
    public void Load_AllEmptyCredentials_IsUnconfigured() {
        var path = WriteTemp("""
        {
            "schemaVersion": 1,
            "channel": "",
            "username": "",
            "oauthToken": ""
        }
        """);
        try {
            Assert.IsType<SettingsResult.Unconfigured>(ModSettings.Load(path));
        } finally { File.Delete(path); }
    }

    // ---------------------------------------------------------------------

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "sts2_bootstrap_" + Guid.NewGuid().ToString("N") + ".json");

    private static string WriteTemp(string content) {
        var path = TempPath();
        File.WriteAllText(path, content);
        return path;
    }

    private static void CleanUp(string path) {
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
        if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
    }
}
