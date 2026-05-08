using System;
using System.Collections.Generic;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Voting;
using Xunit;

namespace SlayTheStreamer2.Tests.Voting;

public class VoteSessionTests : VoteSessionTestBase {
    [Fact]
    public void NewSessionStartsInOpenState() {
        var s = StartVote();
        Assert.Equal(VoteSessionState.Open, s.State);
        Assert.Null(s.WinnerIndex);
    }

    [Fact]
    public void OptionsExposed_ZeroIndexed_PositionMatchesIndex() {
        var s = StartVote(options: new[] { "A", "B", "C" });
        Assert.Equal(3, s.Options.Count);
        Assert.Equal(0, s.Options[0].Index);
        Assert.Equal("A", s.Options[0].Label);
        Assert.Equal(2, s.Options[2].Index);
    }

    [Fact]
    public void TalliesStartAtZeroForEveryOption() {
        var s = StartVote(options: new[] { "A", "B", "C" });
        Assert.Equal(0, s.Tallies[0]);
        Assert.Equal(0, s.Tallies[1]);
        Assert.Equal(0, s.Tallies[2]);
    }

    [Fact]
    public void TimeRemainingStartsAtDuration() {
        var s = StartVote(duration: TimeSpan.FromSeconds(45));
        Assert.Equal(TimeSpan.FromSeconds(45), s.TimeRemaining);
    }

    [Fact]
    public void TimeRemainingDecreasesAsClockAdvances() {
        var s = StartVote(duration: TimeSpan.FromSeconds(30));
        Scheduler.Advance(TimeSpan.FromSeconds(7));
        Assert.Equal(TimeSpan.FromSeconds(23), s.TimeRemaining);
    }

    [Fact]
    public void SnapshotMirrorsCurrentState() {
        var s = StartVote();
        var snap = s.Snapshot();
        Assert.Equal(s.Id, snap.Id);
        Assert.Equal(s.Label, snap.Label);
        Assert.Equal(s.State, snap.State);
        Assert.Equal(s.Options.Count, snap.Options.Count);
    }

    [Fact]
    public void EmptyOptionsThrow() {
        Assert.Throws<ArgumentException>(() => StartVote(options: System.Array.Empty<string>()));
    }

    [Fact]
    public void DurationLessThanOneSecondThrows() {
        Assert.Throws<ArgumentException>(() => StartVote(duration: TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void MoreThanTenOptionsThrows() {
        var eleven = new string[11];
        for (int i = 0; i < 11; i++) eleven[i] = $"opt{i}";
        Assert.Throws<ArgumentException>(() => StartVote(options: eleven));
    }

    [Fact]
    public void EmptyOrWhitespaceLabelThrows() {
        var bad = new[] { "  ", "" };
        Assert.Throws<ArgumentException>(() => StartVote(options: bad));
    }

    [Fact]
    public void HashVoteIsCounted() {
        var s = StartVote();
        Inject("alice", "#1");
        Assert.Equal(0, s.Tallies[0]);
        Assert.Equal(1, s.Tallies[1]);
        Assert.Equal(0, s.Tallies[2]);
    }

    [Fact]
    public void BareNumberVoteIsCounted() {
        var s = StartVote();
        Inject("alice", "1");
        Assert.Equal(1, s.Tallies[1]);
    }

    [Fact]
    public void BangVoteIsCountedByDefault() {
        var s = StartVote();
        Inject("alice", "!1");
        Assert.Equal(1, s.Tallies[1]);
    }

    [Fact]
    public void BangVoteIsRejectedWhenPolicyDisablesIt() {
        var s = StartVote(parsing: VoteParsingPolicy.HashOnly);
        Inject("alice", "!1");
        Assert.Equal(0, s.Tallies[1]);
    }

    [Fact]
    public void LatestVoteFromSameUserReplacesEarlier() {
        var s = StartVote();
        Inject("alice", "#1");
        Inject("alice", "#2");
        Assert.Equal(0, s.Tallies[1]);
        Assert.Equal(1, s.Tallies[2]);
    }

    [Fact]
    public void OutOfRangeIndexIsIgnored() {
        var s = StartVote();
        Inject("alice", "#7");
        Assert.Equal(0, s.Tallies[0]);
        Assert.Equal(0, s.Tallies[1]);
        Assert.Equal(0, s.Tallies[2]);
    }

    [Fact]
    public void NonAnchoredMatchIsIgnored() {
        var s = StartVote();
        Inject("alice", "lol #1");
        Assert.Equal(0, s.Tallies[1]);
    }

    [Fact]
    public void OrdinalsLikeOneStAreIgnored() {
        var s = StartVote();
        Inject("alice", "1st time voter");
        Inject("bob", "1.5 sec brb");
        Assert.Equal(0, s.Tallies[1]);
    }

    [Fact]
    public void TallyChangedFiresOnVoteChange() {
        var s = StartVote();
        var fired = 0;
        s.TallyChanged += (_, _) => fired++;
        Inject("alice", "#1");
        Assert.Equal(1, fired);
        Inject("alice", "#1");
        Assert.Equal(1, fired);
        Inject("alice", "#2");
        Assert.Equal(2, fired);
    }

    [Fact]
    public void DifferentUsersAccumulate() {
        var s = StartVote();
        Inject("alice", "#0");
        Inject("bob", "#0");
        Inject("carol", "#1");
        Assert.Equal(2, s.Tallies[0]);
        Assert.Equal(1, s.Tallies[1]);
    }

    [Fact]
    public void VoterKeyFallbackForUntaggedClient() {
        var s = StartVote();
        Inject("alice", "#1", userId: null);
        Inject("alice", "#1", userId: null);
        Assert.Equal(1, s.Tallies[1]);
    }

    [Fact]
    public void ComputeWinner_SingleMaxReturnsThatIndex() {
        var s = StartVote();
        Inject("alice", "#1"); Inject("bob", "#1"); Inject("carol", "#0");
        var (winner, tieAmong, noVotes) = s.ComputeWinnerForTest();
        Assert.Equal(1, winner);
        Assert.Null(tieAmong);
        Assert.False(noVotes);
    }

    [Fact]
    public void ComputeWinner_TwoWayTie_PicksOneOfTwo_ReportsTie() {
        var s = StartVote();
        Inject("alice", "#0"); Inject("bob", "#1");
        var (winner, tieAmong, noVotes) = s.ComputeWinnerForTest();
        Assert.True(winner is 0 or 1);
        Assert.Equal(2, tieAmong);
        Assert.False(noVotes);
    }

    [Fact]
    public void ComputeWinner_ThreeWayTie_PicksOneOfThree_ReportsTie() {
        var s = StartVote();
        Inject("alice", "#0"); Inject("bob", "#1"); Inject("carol", "#2");
        var (winner, tieAmong, noVotes) = s.ComputeWinnerForTest();
        Assert.True(winner is 0 or 1 or 2);
        Assert.Equal(3, tieAmong);
        Assert.False(noVotes);
    }

    [Fact]
    public void ComputeWinner_NoVotes_PicksOneOfAllOptions_ReportsNoVotes() {
        var s = StartVote();
        var (winner, tieAmong, noVotes) = s.ComputeWinnerForTest();
        Assert.True(winner is 0 or 1 or 2);
        Assert.Null(tieAmong);
        Assert.True(noVotes);
    }

    [Fact]
    public void CloseNow_SetsStateClosed_WinnerIndex_FiresClosedEvent() {
        var s = StartVote();
        Inject("alice", "#2");
        VoteSession? closedSeen = null;
        s.Closed += (_, sess) => closedSeen = sess;

        var winner = s.CloseNow();
        Assert.Equal(2, winner);
        Assert.Equal(VoteSessionState.Closed, s.State);
        Assert.Equal(2, s.WinnerIndex);
        Assert.Same(s, closedSeen);
    }

    [Fact]
    public void Cancel_SetsStateCancelled_NoWinner_FiresCancelledEvent() {
        var s = StartVote();
        Inject("alice", "#1");
        VoteSession? cancelSeen = null;
        s.Cancelled += (_, sess) => cancelSeen = sess;

        s.Cancel();
        Assert.Equal(VoteSessionState.Cancelled, s.State);
        Assert.Null(s.WinnerIndex);
        Assert.Same(s, cancelSeen);
    }

    [Fact]
    public void DisposeOfOpenSessionCancels() {
        var s = StartVote();
        var cancelFired = false;
        s.Cancelled += (_, _) => cancelFired = true;
        s.Dispose();
        Assert.Equal(VoteSessionState.Disposed, s.State);
        Assert.True(cancelFired);
    }

    [Fact]
    public void DisposeOfClosedSessionIsNoop() {
        var s = StartVote();
        s.CloseNow();
        var cancelFired = false;
        s.Cancelled += (_, _) => cancelFired = true;
        s.Dispose();
        Assert.Equal(VoteSessionState.Disposed, s.State);
        Assert.False(cancelFired);
    }

    [Fact]
    public void DoubleDisposeIsNoop() {
        var s = StartVote();
        s.Dispose();
        s.Dispose();   // doesn't throw
        Assert.Equal(VoteSessionState.Disposed, s.State);
    }

    [Fact]
    public void VotesAfterCloseAreIgnored() {
        var s = StartVote();
        Inject("alice", "#1");
        s.CloseNow();
        Inject("bob", "#1");          // post-close
        Assert.Equal(1, s.Tallies[1]);   // unchanged from pre-close
    }

    [Fact]
    public void DurationElapsesTriggersClose() {
        var s = StartVote(duration: TimeSpan.FromSeconds(10));
        Inject("alice", "#0");
        var closed = false;
        s.Closed += (_, _) => closed = true;

        Scheduler.Advance(TimeSpan.FromSeconds(10));
        Assert.True(closed);
        Assert.Equal(VoteSessionState.Closed, s.State);
        Assert.Equal(0, s.WinnerIndex);
    }

    [Fact]
    public void CloseNowTwiceReturnsSameWinnerWithoutRefiring() {
        var s = StartVote();
        Inject("alice", "#1");
        var closedFires = 0;
        s.Closed += (_, _) => closedFires++;
        var w1 = s.CloseNow();
        var w2 = s.CloseNow();
        Assert.Equal(w1, w2);
        Assert.Equal(1, closedFires);
    }

    [Fact]
    public void CancelOfClosedSessionIsNoop() {
        var s = StartVote();
        s.CloseNow();
        var cancelFired = false;
        s.Cancelled += (_, _) => cancelFired = true;
        s.Cancel();
        Assert.Equal(VoteSessionState.Closed, s.State);
        Assert.False(cancelFired);
    }
}
