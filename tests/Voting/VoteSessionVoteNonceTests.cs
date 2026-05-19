using System;
using System.Collections.Generic;
using SlayTheStreamer2.Ti.Voting;
using Xunit;

namespace SlayTheStreamer2.Tests.Voting;

[Collection("TiLog.Sink")]
public class VoteSessionVoteNonceTests : VoteSessionTestBase {
    [Fact]
    public void VoteId_Is_Surfaced_From_Constructor() {
        var session = CreateSession(voteId: 42);
        Assert.Equal(42, session.VoteId);
    }

    [Fact]
    public void VoteCoordinator_Assigns_Sequential_VoteIds() {
        var coordinator = CreateCoordinator();
        var s1 = coordinator.Start("L1", new[] { "A", "B" }, TimeSpan.FromSeconds(5));
        s1.CloseNow();
        var s2 = coordinator.Start("L2", new[] { "A", "B" }, TimeSpan.FromSeconds(5));
        Assert.Equal(0, s1.VoteId);
        Assert.Equal(1, s2.VoteId);
    }

    [Fact]
    public void VoteCoordinator_VoteIds_Cycle_At_100() {
        var coordinator = CreateCoordinator();
        VoteSession? lastSession = null;
        for (int i = 0; i < 101; i++) {
            lastSession?.CloseNow();
            lastSession = coordinator.Start($"L{i}", new[] { "A", "B" }, TimeSpan.FromSeconds(5));
        }
        Assert.Equal(0, lastSession!.VoteId);
    }

    [Fact]
    public void BareNumber_Without_Nonce_Counts() {
        var session = CreateSession(voteId: 42);
        InjectTwitchVoteText(session, userId: "u1", text: "#1");
        Assert.Equal(1, session.Tallies[1]);
    }

    [Fact]
    public void Nonce_Matching_VoteId_Counts() {
        var session = CreateSession(voteId: 42);
        InjectTwitchVoteText(session, userId: "u1", text: "#1!42");
        Assert.Equal(1, session.Tallies[1]);
    }

    [Fact]
    public void Nonce_NonPadded_Matches_When_Numeric_Equal() {
        var session = CreateSession(voteId: 4);
        InjectTwitchVoteText(session, userId: "u1", text: "#1!4");
        Assert.Equal(1, session.Tallies[1]);
    }

    [Fact]
    public void Nonce_ZeroPadded_Also_Matches() {
        var session = CreateSession(voteId: 4);
        InjectTwitchVoteText(session, userId: "u1", text: "#1!04");
        Assert.Equal(1, session.Tallies[1]);
    }

    [Fact]
    public void Stale_Nonce_Is_Dropped() {
        var session = CreateSession(voteId: 42);
        InjectTwitchVoteText(session, userId: "u1", text: "#1!41");
        Assert.Equal(0, session.Tallies[1]);
    }

    [Fact]
    public void OutOfRange_Nonce_Is_Dropped() {
        var session = CreateSession(voteId: 42);
        InjectTwitchVoteText(session, userId: "u1", text: "#1!100");
        Assert.Equal(0, session.Tallies[1]);
    }

    [Fact]
    public void FormatOpen_ShowTagFalse_OmitsBracketTag() {
        var snapshot = new VoteSnapshot(
            Id: "id",
            Label: "Card Reward",
            Options: new[] { new VoteOption(0, "Card A"), new VoteOption(1, "Card B") },
            Duration: TimeSpan.FromSeconds(30),
            TimeRemaining: TimeSpan.FromSeconds(30),
            Tallies: new Dictionary<int, int>(),
            State: VoteSessionState.Open,
            WinnerIndex: null,
            RandomTieAmong: null,
            NoVotesReceived: false,
            DisconnectGap: TimeSpan.Zero,
            VoteId: 4,
            ShowTag: false);

        var text = EnglishReceipts.FormatOpen(snapshot);

        Assert.DoesNotContain("[04]", text);
        Assert.Contains("Card Reward", text);
    }

    [Fact]
    public void FormatOpen_ShowTagTrue_IncludesBracketTag() {
        var snapshot = new VoteSnapshot(
            Id: "id",
            Label: "Card Reward",
            Options: new[] { new VoteOption(0, "Card A"), new VoteOption(1, "Card B") },
            Duration: TimeSpan.FromSeconds(30),
            TimeRemaining: TimeSpan.FromSeconds(30),
            Tallies: new Dictionary<int, int>(),
            State: VoteSessionState.Open,
            WinnerIndex: null,
            RandomTieAmong: null,
            NoVotesReceived: false,
            DisconnectGap: TimeSpan.Zero,
            VoteId: 4,
            ShowTag: true);

        var text = EnglishReceipts.FormatOpen(snapshot);

        Assert.Contains("[04]", text);
    }

    [Fact]
    public void StaleNonceVote_IsDroppedEvenWhenShowTagFalse() {
        // Build a session with ShowTag=false. Send a vote with stale nonce.
        // Confirm tally stays empty: parser is defensive regardless of display.

        var session = CreateSession(voteId: 42, showTag: false);
        var staleNonce = (session.VoteId + 50) % 100;   // guaranteed not to match
        InjectTwitchVoteText(session, userId: "u1", text: $"#0!{staleNonce:D2}");

        Assert.Equal(0, session.Tallies[0]);
    }
}
