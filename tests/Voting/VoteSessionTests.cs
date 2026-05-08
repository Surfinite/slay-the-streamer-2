using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Internal;
using SlayTheStreamer2.Ti.Voting;
using Xunit;

namespace SlayTheStreamer2.Tests.Voting;

[Collection("TiLog.Sink")]
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

    [Fact]
    public void OpenReceiptIsSentAtStartWithNormalPriority() {
        var s = StartVote(label: "card reward");
        Assert.Single(Chat.SentMessages);
        Assert.Equal(OutgoingMessagePriority.Normal, Chat.SentMessages[0].Priority);
        Assert.Contains("card reward", Chat.SentMessages[0].Text);
    }

    [Fact]
    public void PeriodicTally_AdaptiveCadence_30sVote_Fires_ApproxEvery7s() {
        var s = StartVote(duration: TimeSpan.FromSeconds(30));   // adaptive: max(7, 30/5) = 7
        Inject("alice", "#0");
        // Initial open receipt is at index 0.
        Assert.Single(Chat.SentMessages);

        Scheduler.Advance(TimeSpan.FromSeconds(7));
        Assert.Equal(2, Chat.SentMessages.Count);
        Assert.Equal(OutgoingMessagePriority.Low, Chat.SentMessages[1].Priority);
        Assert.Contains("0=1", Chat.SentMessages[1].Text);

        // Change the tally so the next periodic isn't suppressed by the
        // identical-state dedup (covered separately in
        // PeriodicTally_IsSkippedWhenIdenticalToPrevious).
        Inject("bob", "#1");
        Scheduler.Advance(TimeSpan.FromSeconds(7));
        Assert.Equal(3, Chat.SentMessages.Count);
    }

    [Fact]
    public void PeriodicTally_FixedCadence_HonoursPolicy() {
        var s = StartVote(
            duration: TimeSpan.FromSeconds(60),
            receipts: VoteReceiptPolicy.WithFixedCadence(TimeSpan.FromSeconds(10)));
        Inject("alice", "#0");

        Scheduler.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(2, Chat.SentMessages.Count);   // open + 1 periodic
    }

    [Fact]
    public void PeriodicTally_IsSkippedWhenAllZero() {
        var s = StartVote(duration: TimeSpan.FromSeconds(30));   // adaptive 7s
        // No votes injected.
        Scheduler.Advance(TimeSpan.FromSeconds(7));
        Assert.Single(Chat.SentMessages);  // still just the open receipt — no periodic
    }

    [Fact]
    public void PeriodicTally_IsSkippedWhenIdenticalToPrevious() {
        var s = StartVote(duration: TimeSpan.FromSeconds(60));   // adaptive 12s
        Inject("alice", "#0");

        Scheduler.Advance(TimeSpan.FromSeconds(12));   // sends periodic #1 (0=1)
        var afterFirst = Chat.SentMessages.Count;

        Scheduler.Advance(TimeSpan.FromSeconds(12));   // identical tally → skip
        Assert.Equal(afterFirst, Chat.SentMessages.Count);

        Inject("bob", "#1");
        Scheduler.Advance(TimeSpan.FromSeconds(12));   // tally now different → send
        Assert.Equal(afterFirst + 1, Chat.SentMessages.Count);
    }

    [Fact]
    public void PeriodicTally_Disabled_WhenZeroCadence() {
        var s = StartVote(receipts: VoteReceiptPolicy.Silent);
        Inject("alice", "#0");
        Scheduler.Advance(TimeSpan.FromSeconds(60));
        // Silent policy: no open, no periodic, no close.
        Assert.Empty(Chat.SentMessages);
    }

    [Fact]
    public void CloseReceiptIsSentAtCloseWithHighPriority() {
        var s = StartVote();
        Inject("alice", "#1");
        s.CloseNow();
        var lastSend = Chat.SentMessages[^1];
        Assert.Equal(OutgoingMessagePriority.High, lastSend.Priority);
        Assert.Contains("Defend", lastSend.Text);
    }

    [Fact]
    public void Cancel_DoesNotSendCloseReceipt() {
        var s = StartVote();
        var openCount = Chat.SentMessages.Count;
        Assert.Equal(1, openCount);   // sanity: open receipt was sent so this isn't a vacuous pass
        s.Cancel();
        Assert.Equal(openCount, Chat.SentMessages.Count);
    }

    [Fact]
    public void CloseNow_OnCancelledSession_Throws() {
        var s = StartVote();
        s.Cancel();
        var ex = Assert.Throws<InvalidOperationException>(() => s.CloseNow());
        Assert.Contains("Cancelled", ex.Message);
    }

    [Fact]
    public void CloseNow_OnDisposedSession_Throws() {
        var s = StartVote();
        s.Dispose();
        Assert.Throws<InvalidOperationException>(() => s.CloseNow());
    }

    [Fact]
    public void DisconnectGap_IncludesInProgressOutage_WhenCloseFiresDuringOutage() {
        var s = StartVote(duration: TimeSpan.FromSeconds(20));
        Scheduler.Advance(TimeSpan.FromSeconds(5));
        Chat.SimulateState(ChatConnectionState.Reconnecting);  // never comes back online
        Scheduler.Advance(TimeSpan.FromSeconds(6));
        s.CloseNow();   // close while still offline — CloseNowInternal finalises the gap
        Assert.Equal(TimeSpan.FromSeconds(6), s.Snapshot().DisconnectGap);
    }

    [Fact]
    public void ReceiptSend_LogsErrorOnFault() {
        var captured = new List<(LogLevel Level, string Msg, Exception? Ex)>();
        var prior = TiLog.Sink;
        TiLog.Sink = (l, m, e) => captured.Add((l, m, e));
        try {
            // Force the fake chat to fault on the open receipt.
            Chat.SimulateState(ChatConnectionState.Disconnected);
            // Construct a session anyway — open receipt will be attempted on a disconnected chat.
            var s = StartVote();
            // FakeChatService.SendMessageAsync returns Task.FromException synchronously when
            // CanSend == false, and SendReceipt's ContinueWith uses ExecuteSynchronously, so the
            // fault-log continuation runs inline before this assert. Removing ExecuteSynchronously
            // would queue the continuation to the threadpool and race this assert.
            Assert.Contains(captured, e => e.Level == LogLevel.Error && e.Msg.Contains("receipt send failed"));
        } finally {
            TiLog.Sink = prior;
        }
    }

    [Fact]
    public async Task AwaitWinnerAsync_CompletesWithWinner_WhenClosed() {
        var s = StartVote();
        Inject("alice", "#2");
        var task = s.AwaitWinnerAsync();
        s.CloseNow();
        var winner = await task;
        Assert.Equal(2, winner);
    }

    [Fact]
    public async Task AwaitWinnerAsync_CancelsWhenSessionCancelled() {
        var s = StartVote();
        var task = s.AwaitWinnerAsync();
        s.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task AwaitWinnerAsync_CallerCancellation_OnlyCancelsThatAwaiter_NotSession() {
        var s = StartVote();
        using var cts = new System.Threading.CancellationTokenSource();
        var task = s.AwaitWinnerAsync(cts.Token);
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => task);
        Assert.Equal(VoteSessionState.Open, s.State);   // session still open

        // Another awaiter still works:
        var task2 = s.AwaitWinnerAsync();
        Inject("alice", "#1");
        s.CloseNow();
        Assert.Equal(1, await task2);
    }

    [Fact]
    public void Closed_WithoutAwait_LogsWarn() {
        var captured = new List<(LogLevel Level, string Msg)>();
        var prior = TiLog.Sink;
        TiLog.Sink = (lvl, msg, _) => captured.Add((lvl, msg));
        try {
            var s = StartVote();
            Inject("alice", "#0");
            s.CloseNow();
            Assert.Contains(captured, e => e.Level == LogLevel.Warn && e.Msg.Contains("AwaitWinnerAsync was never called"));
        } finally {
            TiLog.Sink = prior;
        }
    }

    [Fact]
    public void Closed_WithAwait_DoesNotLogNoAwaitWarn() {
        var captured = new List<(LogLevel Level, string Msg)>();
        var prior = TiLog.Sink;
        TiLog.Sink = (lvl, msg, _) => captured.Add((lvl, msg));
        try {
            var s = StartVote();
            _ = s.AwaitWinnerAsync();   // call once — that's enough
            Inject("alice", "#0");
            s.CloseNow();
            Assert.DoesNotContain(captured, e => e.Level == LogLevel.Warn && e.Msg.Contains("AwaitWinnerAsync"));
        } finally {
            TiLog.Sink = prior;
        }
    }

    [Fact]
    public void DisconnectGap_Zero_WhenChatStaysConnected() {
        var s = StartVote();
        Inject("alice", "#0");
        s.CloseNow();
        Assert.Equal(TimeSpan.Zero, s.Snapshot().DisconnectGap);
    }

    [Fact]
    public void DisconnectGap_AccumulatesOfflineTime_DuringMidVoteOutage() {
        var s = StartVote(duration: TimeSpan.FromSeconds(30));
        Inject("alice", "#0");

        Scheduler.Advance(TimeSpan.FromSeconds(5));
        Chat.SimulateState(ChatConnectionState.Reconnecting);   // offline starts here
        Scheduler.Advance(TimeSpan.FromSeconds(8));
        Chat.SimulateState(ChatConnectionState.ConnectedReadWrite);   // back online
        Scheduler.Advance(TimeSpan.FromSeconds(5));
        s.CloseNow();
        Assert.Equal(TimeSpan.FromSeconds(8), s.Snapshot().DisconnectGap);
    }

    [Fact]
    public void DisconnectGap_AccumulatesAcrossMultipleOutages() {
        var s = StartVote(duration: TimeSpan.FromSeconds(60));
        Inject("alice", "#0");

        Scheduler.Advance(TimeSpan.FromSeconds(5));
        Chat.SimulateState(ChatConnectionState.Reconnecting);
        Scheduler.Advance(TimeSpan.FromSeconds(3));
        Chat.SimulateState(ChatConnectionState.ConnectedReadWrite);

        Scheduler.Advance(TimeSpan.FromSeconds(10));
        Chat.SimulateState(ChatConnectionState.Disconnected);
        Scheduler.Advance(TimeSpan.FromSeconds(7));
        Chat.SimulateState(ChatConnectionState.ConnectedReadWrite);

        s.CloseNow();
        Assert.Equal(TimeSpan.FromSeconds(10), s.Snapshot().DisconnectGap);
    }

    [Fact]
    public void CloseReceipt_MentionsOfflineGap_WhenPresent() {
        var s = StartVote();
        Inject("alice", "#0");
        Chat.SimulateState(ChatConnectionState.Reconnecting);
        Scheduler.Advance(TimeSpan.FromSeconds(8));
        Chat.SimulateState(ChatConnectionState.ConnectedReadWrite);
        s.CloseNow();
        var closeReceipt = Chat.SentMessages[^1].Text;
        Assert.Contains("offline", closeReceipt);
        Assert.Contains("8s", closeReceipt);
    }

    [Fact]
    public void DisconnectGap_AccumulatesFromStart_WhenChatNotConnectedAtStart() {
        // Override the base class's auto-Connect so the vote opens while chat
        // is in a non-online state. Documents the IsChatOnline() contract:
        // Connecting / Reconnecting / Disconnected all count as offline.
        Chat.SimulateState(ChatConnectionState.Connecting);
        var s = StartVote(duration: TimeSpan.FromSeconds(20));
        Scheduler.Advance(TimeSpan.FromSeconds(5));
        Chat.SimulateState(ChatConnectionState.ConnectedReadWrite);
        Scheduler.Advance(TimeSpan.FromSeconds(5));
        s.CloseNow();
        Assert.Equal(TimeSpan.FromSeconds(5), s.Snapshot().DisconnectGap);
    }

    [Fact]
    public void VoterDict_DropsBeyond10k_LogsWarnOnce() {
        var captured = new List<(LogLevel, string)>();
        var prior = TiLog.Sink;
        TiLog.Sink = (l, m, _) => captured.Add((l, m));
        try {
            var s = StartVote();
            for (int i = 0; i < 10_005; i++)
                Inject($"u{i}", "#0", userId: $"id-{i}");

            Assert.Equal(10_000, s.Tallies[0]);
            var warns = captured.Where(c => c.Item1 == LogLevel.Warn && c.Item2.Contains("voter cap")).ToList();
            Assert.Single(warns);   // only one warn no matter how many overflows
        } finally {
            TiLog.Sink = prior;
        }
    }

    [Fact]
    public void VoterDict_ExistingVoterCanStillChangeVote_WhenAtCap() {
        var s = StartVote();
        for (int i = 0; i < 10_000; i++)
            Inject($"u{i}", "#0", userId: $"id-{i}");

        // u0 changes vote — should still be honoured because they're already in the dict
        Inject("u0", "#1", userId: "id-0");
        Assert.Equal(9_999, s.Tallies[0]);
        Assert.Equal(1, s.Tallies[1]);
    }
}
