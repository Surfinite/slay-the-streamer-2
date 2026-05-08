using System;
using System.Collections.Generic;
using SlayTheStreamer2.Ti.Voting;
using Xunit;

namespace SlayTheStreamer2.Tests.Voting;

public class EnglishReceiptsTests {
    private static VoteSnapshot Snap(
        VoteSessionState state = VoteSessionState.Open,
        int? winner = null,
        int? tieAmong = null,
        bool noVotes = false,
        TimeSpan? remaining = null,
        TimeSpan? disconnectGap = null,
        IReadOnlyDictionary<int, int>? tallies = null,
        IReadOnlyList<VoteOption>? options = null) {
        var opts = options ?? new List<VoteOption> {
            new(0, "Bash"),
            new(1, "Defend"),
            new(2, "Strike"),
        };
        var tlies = tallies ?? new Dictionary<int, int> { [0] = 0, [1] = 0, [2] = 0 };
        return new VoteSnapshot(
            "card-reward-X", "card reward",
            opts, TimeSpan.FromSeconds(30), remaining ?? TimeSpan.FromSeconds(30),
            tlies, state, winner, tieAmong, noVotes, disconnectGap ?? TimeSpan.Zero);
    }

    [Fact]
    public void OpenIncludesLabelOptionsAndDuration() {
        var s = Snap();
        var text = EnglishReceipts.FormatOpen(s);
        Assert.Contains("card reward", text);
        Assert.Contains("0", text);
        Assert.Contains("1", text);
        Assert.Contains("2", text);
        Assert.Contains("30s", text);
    }

    [Fact]
    public void PeriodicShowsTalliesAndRemaining() {
        var s = Snap(
            tallies: new Dictionary<int, int> { [0] = 12, [1] = 8, [2] = 3 },
            remaining: TimeSpan.FromSeconds(15));
        var text = EnglishReceipts.FormatPeriodicTally(s);
        Assert.Contains("0=12", text);
        Assert.Contains("1=8", text);
        Assert.Contains("2=3", text);
        Assert.Contains("15s", text);
    }

    [Fact]
    public void CloseWinnerSaysChatChose() {
        var s = Snap(state: VoteSessionState.Closed, winner: 1);
        var text = EnglishReceipts.FormatClose(s);
        Assert.Contains("Chat chose", text);
        Assert.Contains("1", text);
        Assert.Contains("Defend", text);
    }

    [Fact]
    public void CloseTwoWayTieMentionsBetween() {
        var s = Snap(
            state: VoteSessionState.Closed, winner: 1, tieAmong: 2,
            tallies: new Dictionary<int, int> { [0] = 5, [1] = 5, [2] = 0 });
        var text = EnglishReceipts.FormatClose(s);
        Assert.Contains("Tie", text);
        Assert.Contains("between", text);
        Assert.Contains("Bash", text);     // tied option 0
        Assert.Contains("Defend", text);   // tied option 1 (winner)
        Assert.DoesNotContain("Strike", text);   // option 2 has 0 votes — not in the tie
    }

    [Fact]
    public void CloseThreePlusWayTieUsesDistinctFormat() {
        var s = Snap(state: VoteSessionState.Closed, winner: 1, tieAmong: 3);
        var text = EnglishReceipts.FormatClose(s);
        Assert.Contains("3-way tie", text);
        Assert.Contains("Defend", text);
        Assert.DoesNotContain("between", text);   // distinct from 2-way format
    }

    [Fact]
    public void CloseNoVotesAnnouncesRandomPick() {
        var s = Snap(state: VoteSessionState.Closed, winner: 0, noVotes: true);
        var text = EnglishReceipts.FormatClose(s);
        Assert.Contains("No votes", text);
        Assert.Contains("Bash", text);
        Assert.Contains("randomly", text);
    }

    [Fact]
    public void CloseWithDisconnectGapMentionsOfflineSeconds() {
        var s = Snap(state: VoteSessionState.Closed, winner: 1, disconnectGap: TimeSpan.FromSeconds(8));
        var text = EnglishReceipts.FormatClose(s);
        Assert.Contains("8s", text);
        Assert.Contains("offline", text);
    }
}
