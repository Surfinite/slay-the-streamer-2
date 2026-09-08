using System.IO;
using SlayTheStreamer2.Game.DecisionVotes;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

[Collection("TiLog.Sink")]
public class RelicTextRegistryTests {
    private static string TempPath() => Path.Combine(Path.GetTempPath(), "sts2-learned-" + Path.GetRandomFileName() + ".json");

    [Fact]
    public void BuiltIns_AreKnown_AndNeverLearned() {
        var path = TempPath();
        var reg = new RelicTextRegistry(path);
        Assert.True(reg.IsKnown("KALEIDOSCOPE"));
        reg.Learn("KALEIDOSCOPE", AuthorityMode.RemoveOne);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Learn_PersistsAndReloads() {
        var path = TempPath();
        new RelicTextRegistry(path).Learn("MOD_RELIC", AuthorityMode.Unskippable);
        var reloaded = new RelicTextRegistry(path);
        Assert.True(reloaded.TryGetLearnedMode("MOD_RELIC", out var mode));
        Assert.Equal(AuthorityMode.Unskippable, mode);
        Assert.True(reloaded.IsKnown("MOD_RELIC"));
    }

    [Fact]
    public void Learn_IsIdempotent() {
        var path = TempPath();
        var reg = new RelicTextRegistry(path);
        reg.Learn("MOD_RELIC", AuthorityMode.RemoveOne);
        var first = File.ReadAllText(path);
        reg.Learn("MOD_RELIC", AuthorityMode.RemoveOne);
        Assert.Equal(first, File.ReadAllText(path));
    }

    [Fact]
    public void Learn_WritesLowercaseKeys() {
        var path = TempPath();
        new RelicTextRegistry(path).Learn("MOD_RELIC", AuthorityMode.Unskippable);
        var text = File.ReadAllText(path);
        Assert.Contains("\"id\": \"MOD_RELIC\"", text);
        Assert.Contains("\"mode\": \"Unskippable\"", text);
        Assert.DoesNotContain("\"Id\"", text);
    }

    [Fact]
    public void CorruptFile_IsIgnored() {
        var path = TempPath();
        File.WriteAllText(path, "{ not json");
        var reg = new RelicTextRegistry(path);
        Assert.False(reg.TryGetLearnedMode("ANY", out _));
    }
}
