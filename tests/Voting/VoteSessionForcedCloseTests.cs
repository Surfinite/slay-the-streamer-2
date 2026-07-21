using System;
using System.Threading.Tasks;
using SlayTheStreamer2.Ti.Voting;
using Xunit;

namespace SlayTheStreamer2.Tests.Voting;

[Collection("TiLog.Sink")]
public class VoteSessionForcedCloseTests : VoteSessionTestBase {

    [Fact]
    public void TryCloseNow_sets_forced_winner_and_fires_Closed_once() {
        var s = StartVote();                    // options: Bash, Defend, Strike
        int closedCount = 0;
        s.Closed += (_, _) => closedCount++;
        InjectTwitchVote(s, "1001", 0);
        InjectTwitchVote(s, "1002", 0);         // chat leader is #0
        _ = s.AwaitWinnerAsync();               // suppress the never-awaited warn

        Assert.True(s.TryCloseNow(2));          // streamer forces #2 anyway

        Assert.Equal(VoteSessionState.Closed, s.State);
        Assert.Equal(2, s.WinnerIndex);
        Assert.Equal(1, closedCount);
    }

    [Fact]
    public async Task TryCloseNow_completes_AwaitWinnerAsync_with_forced_index() {
        var s = StartVote();
        var winnerTask = s.AwaitWinnerAsync();
        Assert.True(s.TryCloseNow(1));
        Assert.Equal(1, await winnerTask);
    }

    [Fact]
    public void TryCloseNow_sends_no_close_receipt() {
        var s = StartVote();                    // open receipt already sent
        _ = s.AwaitWinnerAsync();
        int before = Chat.SentMessages.Count;
        Assert.True(s.TryCloseNow(1));
        Assert.Equal(before, Chat.SentMessages.Count);
    }

    [Fact]
    public void Natural_CloseNow_still_sends_close_receipt() {
        // Regression guard: the no-receipt rule is forced-close-only.
        var s = StartVote();
        _ = s.AwaitWinnerAsync();
        int before = Chat.SentMessages.Count;
        s.CloseNow();
        Assert.Equal(before + 1, Chat.SentMessages.Count);
    }

    [Fact]
    public void TryCloseNow_returns_false_on_closed_and_cancelled_sessions() {
        var closed = StartVote();
        _ = closed.AwaitWinnerAsync();
        int naturalWinner = closed.CloseNow();
        Assert.False(closed.TryCloseNow(1));
        Assert.Equal(naturalWinner, closed.WinnerIndex);   // untouched

        var cancelled = StartVote();
        cancelled.Cancel();
        Assert.False(cancelled.TryCloseNow(1));
        Assert.Equal(VoteSessionState.Cancelled, cancelled.State);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void TryCloseNow_throws_on_out_of_range_index(int idx) {
        var s = StartVote();                    // 3 options -> valid 0..2
        Assert.Throws<ArgumentOutOfRangeException>(() => s.TryCloseNow(idx));
        Assert.Equal(VoteSessionState.Open, s.State);   // still open after throw
        s.Cancel();
    }

    [Fact]
    public void Snapshot_carries_ForcedWinner_flag() {
        var forced = StartVote();
        _ = forced.AwaitWinnerAsync();
        forced.TryCloseNow(0);
        Assert.True(forced.Snapshot().ForcedWinner);

        var natural = StartVote();
        _ = natural.AwaitWinnerAsync();
        natural.CloseNow();
        Assert.False(natural.Snapshot().ForcedWinner);
    }

    [Fact]
    public void Elapsed_tracks_clock_advance() {
        var s = StartVote();
        Assert.Equal(TimeSpan.Zero, s.Elapsed);
        Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(2), s.Elapsed);
        s.Cancel();
    }

    [Fact]
    public void Close_timer_is_dead_after_forced_close() {
        var s = StartVote(duration: TimeSpan.FromSeconds(30));
        int closedCount = 0;
        s.Closed += (_, _) => closedCount++;
        _ = s.AwaitWinnerAsync();
        Assert.True(s.TryCloseNow(0));
        // Advance past natural expiry; the disposed close timer must not re-fire.
        // Scheduler.Advance both moves the clock and fires any due callbacks
        // (there's no separate Clock.Advance + Scheduler.RunDueTimers split in
        // this codebase's fakes — see VoteSessionTests.cs for the established
        // pattern), so a single call covers both concerns here.
        Scheduler.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(1, closedCount);
        Assert.Equal(0, s.WinnerIndex);
    }
}
