using System.IO;
using System.Text.Json;
using SlayTheStreamer2.Game.DecisionVotes;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

public class AuthorityLocTests {
    private static RelicTextRegistry Registry() => new(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));

    [Fact]
    public void Lead_IsExact() =>
        Assert.Equal("\n[color=#668CFF]Slay the Streamer:[/color] ", AuthorityLoc.Lead);

    [Fact]
    public void RelicDescription_GetsSuffix() {
        var s = AuthorityLoc.SuffixFor("relics", "KALEIDOSCOPE.description", Registry());
        Assert.Equal(AuthorityLoc.Lead + "for each card reward, chat votes to remove one option, Skip included, and you pick from the rest.", s);
    }

    [Fact]
    public void RelicEventDescription_GetsSameSuffix() =>
        Assert.Equal(AuthorityLoc.SuffixFor("relics", "GLASS_EYE.description", Registry()),
                     AuthorityLoc.SuffixFor("relics", "GLASS_EYE.eventDescription", Registry()));

    [Fact]
    public void RelicFlavor_GetsNothing() => Assert.Null(AuthorityLoc.SuffixFor("relics", "KALEIDOSCOPE.flavor", Registry()));

    [Fact]
    public void DreamCatcherRestText_GetsItsOwnLine() =>
        Assert.Equal(AuthorityLoc.Lead + "chat removes one of the options first.",
            AuthorityLoc.SuffixFor("relics", "DREAM_CATCHER.additionalRestSiteHealText", Registry()));

    [Fact]
    public void EventKeys_GetSuffix() {
        Assert.Equal(AuthorityLoc.Lead + "the card rewards cannot be skipped.",
            AuthorityLoc.SuffixFor("events", "BRAIN_LEECH.pages.INITIAL.options.RIP.description", Registry()));
        Assert.StartsWith("\n" + AuthorityLoc.Lead, AuthorityLoc.SuffixFor("events", "CRYSTAL_SPHERE.minigame.instructions.description", Registry()));
    }

    [Fact]
    public void UnknownKeyOrTable_GetsNothing() {
        Assert.Null(AuthorityLoc.SuffixFor("events", "TRASH_HEAP.title", Registry()));
        Assert.Null(AuthorityLoc.SuffixFor("cards", "STRIKE.description", Registry()));
    }

    [Fact]
    public void LearnedRelic_GetsGenericLineByMode() {
        var reg = Registry();
        reg.Learn("SOME_MOD_RELIC", AuthorityMode.RemoveOne);
        reg.Learn("SOME_SHOP_RELIC", AuthorityMode.Unskippable);
        Assert.Equal(AuthorityLoc.Lead + "chat votes to remove one option from this relic's card rewards.", AuthorityLoc.SuffixFor("relics", "SOME_MOD_RELIC.description", reg));
        Assert.Equal(AuthorityLoc.Lead + "card rewards from this relic cannot be skipped.", AuthorityLoc.SuffixFor("relics", "SOME_SHOP_RELIC.eventDescription", reg));
    }

    [Fact]
    public void Append_IsIdempotent() {
        string suffix = AuthorityLoc.Lead + "x.";
        string once = AuthorityLoc.Append("Base text.", suffix);
        Assert.Equal("Base text." + suffix, once);
        Assert.Equal(once, AuthorityLoc.Append(once, suffix));
    }

    [Fact]
    public void NoEmDashOrStrikeAnywhere() {
        var reg = Registry();
        reg.Learn("X", AuthorityMode.RemoveOne); reg.Learn("Y", AuthorityMode.Unskippable);
        foreach (var key in AuthorityLoc.AllKeysForTests()) {
            var s = AuthorityLoc.SuffixFor(key.Table, key.Key, reg)!;
            Assert.DoesNotContain("\u2014", s);
            Assert.DoesNotContain("strike", s.ToLowerInvariant());
            Assert.StartsWith(key.Key.EndsWith("minigame.instructions.description") ? "\n" + AuthorityLoc.Lead : AuthorityLoc.Lead, s);
        }
    }

    /// <summary>Every built-in vanilla key exists in the shipped English tables. Reads the
    /// extracted assets when present (decompiled/sts2-assets); skipped quietly otherwise.</summary>
    [Fact]
    public void BuiltInKeys_ExistInVanillaTables() {
        string root = FindRepoRoot();
        string relics = Path.Combine(root, "decompiled", "sts2-assets", "localization", "eng", "relics.json");
        string events = Path.Combine(root, "decompiled", "sts2-assets", "localization", "eng", "events.json");
        if (!File.Exists(relics) || !File.Exists(events)) return;
        using var r = JsonDocument.Parse(File.ReadAllText(relics));
        using var e = JsonDocument.Parse(File.ReadAllText(events));
        foreach (var id in AuthorityLoc.BuiltInRelicIds) {
            if (id == "STRONGBOX") continue;   // third-party (More Relics)
            Assert.True(r.RootElement.TryGetProperty(id + ".description", out _), $"missing relics key {id}.description");
        }
        Assert.True(r.RootElement.TryGetProperty("DREAM_CATCHER.additionalRestSiteHealText", out _));
        foreach (var key in AuthorityLoc.EventKeys)
            Assert.True(e.RootElement.TryGetProperty(key, out _), $"missing events key {key}");
    }

    private static string FindRepoRoot() {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
