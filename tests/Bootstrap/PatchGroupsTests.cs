using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SlayTheStreamer2.Game.Bootstrap;
using Xunit;

namespace SlayTheStreamer2.Tests.Bootstrap;

public class PatchGroupsTests {
    [Theory]
    [InlineData("CardRewardVotePatch", PatchGroups.CardVote)]
    [InlineData("CardRewardSkipGatePatch", PatchGroups.CardVote)]
    [InlineData("UnskippableRewards", PatchGroups.RewardModes)]
    [InlineData("RelicOriginTags", PatchGroups.RewardModes)]
    [InlineData("ChooseACardRemovePatch", PatchGroups.RemoveOne)]
    [InlineData("LocTextPatch", PatchGroups.ExplainText)]
    [InlineData("BossVotePatch", PatchGroups.BossVote)]
    [InlineData("TopBarMapButtonGuardPatch", PatchGroups.MapGuard)]
    [InlineData("RandomCharacterDreadEmbarkGuardPatch", PatchGroups.CustomRandom)]
    [InlineData("ChestRefundDemotionGuardPatch", PatchGroups.ChestRelics)]
    [InlineData("LinkedSetSignalRewirePatch", PatchGroups.EliteRelics)]
    [InlineData("SealedNeowLocPatch", PatchGroups.SealedNeow)]
    public void GroupOf_ReturnsTheFeatureGroup_OfAPatchClass(string patchClass, string expected) {
        Assert.Equal(expected, PatchGroups.GroupOf(patchClass));
    }

    [Fact]
    public void GroupOf_ReturnsNull_ForAClassNobodyAssigned() {
        Assert.Null(PatchGroups.GroupOf("SomeBrandNewPatch"));
    }

    [Fact]
    public void Closure_OfNoFailures_IsEmpty() {
        Assert.Empty(PatchGroups.Closure(Array.Empty<string>()));
    }

    [Fact]
    public void Closure_OfAnIndependentGroup_IsOnlyThatGroup() {
        Assert.Equal(new[] { PatchGroups.BossVote }, PatchGroups.Closure(new[] { PatchGroups.BossVote }));
    }

    [Fact]
    public void Closure_OfCardVote_TakesEveryGroupBuiltOnIt() {
        var closed = PatchGroups.Closure(new[] { PatchGroups.CardVote });
        Assert.Equal(
            new[] { PatchGroups.CardVote, PatchGroups.ExplainText, PatchGroups.RemoveOne, PatchGroups.RewardModes },
            closed.OrderBy(g => g, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Closure_OfRewardModes_TakesItsDependents_ButLeavesCardVoteActive() {
        var closed = PatchGroups.Closure(new[] { PatchGroups.RewardModes });
        Assert.Equal(
            new[] { PatchGroups.ExplainText, PatchGroups.RemoveOne, PatchGroups.RewardModes },
            closed.OrderBy(g => g, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Closure_OfALeafGroup_IsOnlyThatGroup() {
        Assert.Equal(new[] { PatchGroups.RemoveOne }, PatchGroups.Closure(new[] { PatchGroups.RemoveOne }));
    }

    [Fact]
    public void Closure_PassesAnUnknownGroupThrough() {
        // An unassigned patch class gets a group of its own at registration time.
        Assert.Equal(new[] { "ungrouped:SomeBrandNewPatch" }, PatchGroups.Closure(new[] { "ungrouped:SomeBrandNewPatch" }));
    }

    [Fact]
    public void EveryDependency_NamesAKnownGroup() {
        foreach (var group in PatchGroups.All) {
            Assert.All(PatchGroups.DependenciesOf(group), dep => Assert.Contains(dep, PatchGroups.All));
        }
    }

    [Fact]
    public void FormatSummary_ReportsAllGroupsActive() {
        var line = PatchGroups.FormatSummary("v0.113.0", "0.4.4+abc123", totalGroups: 16, methodCount: 61, failures: new Dictionary<string, string>());
        Assert.Equal("[SlayTheStreamer2] patch summary: game v0.113.0, mod 0.4.4+abc123: 16/16 groups active, 61 methods", line);
    }

    [Fact]
    public void FormatSummary_NamesEachFailedGroupWithItsReason_InNameOrder() {
        var failures = new Dictionary<string, string> {
            [PatchGroups.RemoveOne] = "needs reward-modes",
            [PatchGroups.BossVote] = "BossVotePatch: target not found",
        };
        var line = PatchGroups.FormatSummary("v0.113.0", "0.4.4+abc123", totalGroups: 16, methodCount: 55, failures);
        Assert.Equal(
            "[SlayTheStreamer2] patch summary: game v0.113.0, mod 0.4.4+abc123: 14/16 groups active, 55 methods; "
            + "FAILED boss-vote (BossVotePatch: target not found); FAILED remove-one (needs reward-modes)",
            line);
    }

    [Fact]
    public void ChatNotice_IsNull_WhenNothingFailed() {
        Assert.Null(PatchGroups.ChatNotice(new Dictionary<string, string>()));
    }

    [Fact]
    public void ChatNotice_NamesTheFailedFeaturesInPlainWords_InNameOrder() {
        var failures = new Dictionary<string, string> {
            [PatchGroups.ChestRelics] = "x",
            [PatchGroups.BossVote] = "x",
        };
        Assert.Equal("Not working on this game version: boss vote, chest relic choices.", PatchGroups.ChatNotice(failures));
    }

    [Fact]
    public void ChatNotice_NamesOnlyTheRootCause_WhenDependentGroupsWentDownWithIt() {
        var failures = PatchGroups.Closure(new[] { PatchGroups.CardVote }).ToDictionary(g => g, _ => "x");
        Assert.Equal("Not working on this game version: card reward votes.", PatchGroups.ChatNotice(failures));
    }

    [Fact]
    public void ChatNotice_IsNull_WhenOnlyGroupsWithNoStreamerFacingNameFailed() {
        var failures = new Dictionary<string, string> {
            [PatchGroups.ExplainText] = "x",
            ["ungrouped:SomeBrandNewPatch"] = "x",
        };
        Assert.Null(PatchGroups.ChatNotice(failures));
    }

    /// <summary>Guards the catalogue against drift: a patch class added without a group
    /// still registers (as a group of its own, with a Warn), but it then has no
    /// all-or-nothing partners, which is the property the groups exist for.</summary>
    [Fact]
    public void EveryPatchClassInTheModSource_IsAssignedToAGroup() {
        var srcDir = Path.Combine(RepoRoot(), "src");
        var topLevelClass = new Regex(@"^(?:public|internal)\s+(?:static\s+|sealed\s+|partial\s+)*class\s+(\w+)", RegexOptions.Multiline);
        var unassigned = new List<string>();
        foreach (var file in Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)) {
            if (file.Contains(Path.DirectorySeparatorChar + ".godot" + Path.DirectorySeparatorChar)) continue;
            var text = File.ReadAllText(file);
            if (!text.Contains("[HarmonyPatch")) continue;
            var classes = topLevelClass.Matches(text);
            // A top-level class owns the text from the attribute/comment block directly
            // above its declaration up to the block above the next top-level class. That
            // covers an attribute on the class itself and on any class nested in it,
            // which is also how the registrar assigns groups (by outermost type).
            var starts = classes.Select(c => AttributeBlockStart(text, c.Index)).ToList();
            for (int i = 0; i < classes.Count; i++) {
                int end = i + 1 < classes.Count ? starts[i + 1] : text.Length;
                string owned = text.Substring(starts[i], end - starts[i]);
                if (!patchAttributeLine.IsMatch(owned)) continue;
                var name = classes[i].Groups[1].Value;
                if (PatchGroups.GroupOf(name) is null) unassigned.Add($"{name} ({Path.GetFileName(file)})");
            }
        }
        Assert.True(unassigned.Count == 0, "Patch classes with no PatchGroups entry: " + string.Join(", ", unassigned));
    }

    [Fact]
    public void IsFailed_IsFalse_ForAGroupNeverMarked() {
        Assert.False(PatchGroups.IsFailed("test-only:never-marked"));
    }

    [Fact]
    public void MarkFailed_MakesTheGroupFailed_AndKeepsTheFirstReason() {
        PatchGroups.MarkFailed("test-only:marked", "first reason");
        PatchGroups.MarkFailed("test-only:marked", "second reason");
        Assert.True(PatchGroups.IsFailed("test-only:marked"));
        Assert.Equal("first reason", PatchGroups.Failures["test-only:marked"]);
    }

    /// <summary>An attribute line as code, not one quoted inside a comment.</summary>
    private static readonly Regex patchAttributeLine = new(@"^\s*\[HarmonyPatch", RegexOptions.Multiline);

    /// <summary>The index where the unbroken run of attribute and comment lines directly
    /// above the line starting at <paramref name="classIndex"/> begins.</summary>
    private static int AttributeBlockStart(string text, int classIndex) {
        int lineStart = classIndex;
        while (lineStart > 0) {
            int prevEnd = lineStart - 1;                                   // the '\n' ending the previous line
            int prevStart = prevEnd == 0 ? 0 : text.LastIndexOf('\n', prevEnd - 1) + 1;
            var line = text.Substring(prevStart, prevEnd - prevStart).Trim();
            bool attributeOrComment = line.StartsWith("[") || line.StartsWith("//") || line.EndsWith(")]");
            if (!attributeOrComment) break;
            lineStart = prevStart;
        }
        return lineStart;
    }

    private static string RepoRoot() {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "slay_the_streamer_2.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found above " + AppContext.BaseDirectory);
    }
}
