using System;
using System.Collections.Generic;
using SlayTheStreamer2.Game.DecisionVotes;
using SlayTheStreamer2.Ti.Voting;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

public class RemoveVoteReceiptsTests {
    private static VoteSnapshot Snap(int? winner = null, int? tieAmong = null, bool noVotes = false,
            IReadOnlyDictionary<int, int>? tallies = null, bool showTag = false, int voteId = 7) {
        var opts = new List<VoteOption> { new(0, "Skip"), new(1, "Bash"), new(2, "Defend") };
        return new VoteSnapshot("remove-x", "Remove an option", opts, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(12),
            tallies ?? new Dictionary<int, int> { [0] = 0, [1] = 0, [2] = 0 },
            winner is null ? VoteSessionState.Open : VoteSessionState.Closed,
            winner, tieAmong, noVotes, TimeSpan.Zero, voteId, showTag);
    }

    [Fact]
    public void Open_NamesStreamerAndIndices() {
        var text = RemoveVoteReceipts.Format(Snap(), ReceiptKind.Open, "Surfinite");
        Assert.Equal("Vote: remove one option from Surfinite's card reward! Type 0, 1, 2. 30s left.", text);
    }

    [Fact]
    public void Open_WithTag_IncludesVoteId() {
        var text = RemoveVoteReceipts.Format(Snap(showTag: true), ReceiptKind.Open, "Surfinite");
        Assert.StartsWith("Vote [07]: remove one option", text);
    }

    [Fact]
    public void Close_RemovedCard() {
        var text = RemoveVoteReceipts.Format(Snap(winner: 1), ReceiptKind.Close, "Surfinite");
        Assert.Equal("Chat removed 1: Bash. Surfinite picks from the rest.", text);
    }

    [Fact]
    public void Close_RemovedSkip_SaysMustTakeACard() {
        var text = RemoveVoteReceipts.Format(Snap(winner: 0), ReceiptKind.Close, "Surfinite");
        Assert.Equal("Chat removed 0: Skip. Surfinite must take a card.", text);
    }

    [Fact]
    public void Close_NoVotes_SaysRandomly() {
        var text = RemoveVoteReceipts.Format(Snap(winner: 2, noVotes: true), ReceiptKind.Close, "Surfinite");
        Assert.Equal("No votes received. Chat removed 2: Defend randomly. Surfinite picks from the rest.", text);
    }

    [Fact]
    public void Close_ThreeWayTie() {
        var text = RemoveVoteReceipts.Format(Snap(winner: 2, tieAmong: 3), ReceiptKind.Close, "Surfinite");
        Assert.Equal("3-way tie! Chat removed 2: Defend randomly. Surfinite picks from the rest.", text);
    }

    [Fact]
    public void PeriodicTally_UsesDefaultFormat() {
        var text = RemoveVoteReceipts.Format(Snap(), ReceiptKind.PeriodicTally, "Surfinite");
        Assert.Equal(EnglishReceipts.FormatPeriodicTally(Snap()), text);
    }

    [Fact]
    public void Override_Limited() =>
        Assert.Equal("Surfinite overrode chat's removal and took Bash. 0 overrides remaining this act",
            RemoveVoteReceipts.FormatOverride("Surfinite", "Bash", limit: 1, remaining: 0, curseTitle: null));

    [Fact]
    public void Override_WithCurse_Unlimited() =>
        Assert.Equal("Surfinite overrode chat's removal and took Skip. Cursed Overrides: gained Injury!",
            RemoveVoteReceipts.FormatOverride("Surfinite", "Skip", limit: -1, remaining: int.MaxValue, curseTitle: "Injury"));
}
