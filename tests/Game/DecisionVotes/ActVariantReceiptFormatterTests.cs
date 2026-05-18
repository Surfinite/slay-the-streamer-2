using System;
using System.Collections.Generic;
using SlayTheStreamer2.Game.DecisionVotes;
using SlayTheStreamer2.Ti.Voting;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

[Collection("TiLog.Sink")]
public sealed class ActVariantReceiptFormatterTests {

    private static VoteSnapshot MakeSnapshot(
            bool noVotes = false,
            int? winnerIndex = null,
            int? randomTieAmong = null,
            VoteSessionState state = VoteSessionState.Closed,
            int voteId = 7) {
        var options = new List<VoteOption> {
            new(0, "Overgrowth"),
            new(1, "Underdocks"),
        };
        IReadOnlyDictionary<int, int> tallies = new Dictionary<int, int> {
            [0] = noVotes ? 0 : 5,
            [1] = noVotes ? 0 : 3,
        };
        return new VoteSnapshot(
            Id: "act-variant-1",
            Label: "Act 1 variant",
            Options: options,
            Duration: TimeSpan.FromSeconds(30),
            TimeRemaining: TimeSpan.Zero,
            Tallies: tallies,
            State: state,
            WinnerIndex: winnerIndex,
            RandomTieAmong: randomTieAmong,
            NoVotesReceived: noVotes,
            DisconnectGap: TimeSpan.Zero,
            VoteId: voteId);
    }

    [Fact]
    public void Close_with_no_votes_returns_custom_text_and_invokes_onNoVotes() {
        var snap = MakeSnapshot(noVotes: true, winnerIndex: 0, state: VoteSessionState.Closed);
        bool called = false;
        var result = ActVariantReceiptFormatter.Format(snap, ReceiptKind.Close, () => called = true);
        Assert.Contains("no votes received", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("vanilla random pick stands", result, StringComparison.OrdinalIgnoreCase);
        Assert.True(called);
    }

    [Fact]
    public void Close_with_winner_delegates_to_EnglishReceipts_FormatClose() {
        var snap = MakeSnapshot(noVotes: false, winnerIndex: 0, state: VoteSessionState.Closed);
        bool called = false;
        var result = ActVariantReceiptFormatter.Format(snap, ReceiptKind.Close, () => called = true);
        Assert.Equal(EnglishReceipts.FormatClose(snap), result);
        Assert.False(called);
    }

    [Fact]
    public void Open_always_delegates_to_EnglishReceipts_FormatOpen() {
        var snap = MakeSnapshot(noVotes: false, state: VoteSessionState.Open);
        bool called = false;
        var result = ActVariantReceiptFormatter.Format(snap, ReceiptKind.Open, () => called = true);
        Assert.Equal(EnglishReceipts.FormatOpen(snap), result);
        Assert.False(called);
    }

    [Fact]
    public void PeriodicTally_always_delegates_to_EnglishReceipts_FormatPeriodicTally() {
        var snap = MakeSnapshot(noVotes: false, state: VoteSessionState.Open);
        bool called = false;
        var result = ActVariantReceiptFormatter.Format(snap, ReceiptKind.PeriodicTally, () => called = true);
        Assert.Equal(EnglishReceipts.FormatPeriodicTally(snap), result);
        Assert.False(called);
    }

    [Fact]
    public void Open_with_no_votes_does_NOT_invoke_onNoVotes() {
        var snap = MakeSnapshot(noVotes: true, state: VoteSessionState.Open);
        bool called = false;
        ActVariantReceiptFormatter.Format(snap, ReceiptKind.Open, () => called = true);
        Assert.False(called);
    }
}
