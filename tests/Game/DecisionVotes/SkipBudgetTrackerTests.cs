using SlayTheStreamer2.Game.DecisionVotes;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

public class SkipBudgetTrackerTests {
    [Fact]
    public void IsSkipAllowed_LimitZero_AlwaysFalse() {
        var t = new SkipBudgetTracker();
        Assert.False(t.IsSkipAllowed(0));
    }

    [Fact]
    public void IsSkipAllowed_LimitMinusOne_AlwaysTrue() {
        var t = new SkipBudgetTracker();
        Assert.True(t.IsSkipAllowed(-1));
        t.RecordSkip();
        t.RecordSkip();
        t.RecordSkip();
        Assert.True(t.IsSkipAllowed(-1));
    }

    [Fact]
    public void IsSkipAllowed_PositiveLimit_TrueUntilExhausted() {
        var t = new SkipBudgetTracker();
        Assert.True(t.IsSkipAllowed(2));
        t.RecordSkip();
        Assert.True(t.IsSkipAllowed(2));
        t.RecordSkip();
        Assert.False(t.IsSkipAllowed(2));
    }

    [Fact]
    public void RecordSkip_Increments() {
        var t = new SkipBudgetTracker();
        Assert.Equal(0, t.ActSkipsUsed);
        t.RecordSkip();
        Assert.Equal(1, t.ActSkipsUsed);
        t.RecordSkip();
        Assert.Equal(2, t.ActSkipsUsed);
    }

    [Fact]
    public void ObserveRunAndAct_RunChange_ResetsCounter() {
        var t = new SkipBudgetTracker();
        t.ObserveRunAndAct("run-1", 0);
        t.RecordSkip();
        Assert.Equal(1, t.ActSkipsUsed);
        t.ObserveRunAndAct("run-2", 0);
        Assert.Equal(0, t.ActSkipsUsed);
    }

    [Fact]
    public void ObserveRunAndAct_ActChangeSameRun_ResetsCounter() {
        var t = new SkipBudgetTracker();
        t.ObserveRunAndAct("run-1", 0);
        t.RecordSkip();
        t.RecordSkip();
        Assert.Equal(2, t.ActSkipsUsed);
        t.ObserveRunAndAct("run-1", 1);
        Assert.Equal(0, t.ActSkipsUsed);
    }

    [Fact]
    public void ObserveRunAndAct_IdenticalRunAndAct_DoesNotReset() {
        var t = new SkipBudgetTracker();
        t.ObserveRunAndAct("run-1", 0);
        t.RecordSkip();
        t.ObserveRunAndAct("run-1", 0);
        Assert.Equal(1, t.ActSkipsUsed);
    }

    [Fact]
    public void ObserveRunAndAct_NullRunId_DoesNotResetByRun() {
        var t = new SkipBudgetTracker();
        t.ObserveRunAndAct("run-1", 0);
        t.RecordSkip();
        t.ObserveRunAndAct(null, 0);   // null run-id (degraded run detection)
        Assert.Equal(1, t.ActSkipsUsed);
    }

    [Fact]
    public void ObserveRunAndAct_ReturnsRunChanged_OnNewRun() {
        var t = new SkipBudgetTracker();
        t.ObserveRunAndAct("run-1", 0);
        Assert.Equal(BudgetResetReason.RunChanged, t.ObserveRunAndAct("run-2", 0));
    }

    [Fact]
    public void ObserveRunAndAct_ReturnsActChanged_OnActJumpSameRun() {
        var t = new SkipBudgetTracker();
        t.ObserveRunAndAct("run-1", 0);
        Assert.Equal(BudgetResetReason.ActChanged, t.ObserveRunAndAct("run-1", 1));
    }

    [Fact]
    public void ObserveRunAndAct_ReturnsNone_OnIdenticalRunAndAct() {
        var t = new SkipBudgetTracker();
        t.ObserveRunAndAct("run-1", 0);
        Assert.Equal(BudgetResetReason.None, t.ObserveRunAndAct("run-1", 0));
    }

    [Fact]
    public void ObserveRunAndAct_ReturnsRunChanged_OnFirstObservation() {
        var t = new SkipBudgetTracker();
        Assert.Equal(BudgetResetReason.RunChanged, t.ObserveRunAndAct("run-1", 0));
    }

    [Fact]
    public void Snapshot_PositiveLimit_ReturnsCorrectRemaining() {
        var t = new SkipBudgetTracker();
        t.RecordSkip();
        var snap = t.Snapshot(3);
        Assert.Equal(1, snap.UsedThisAct);
        Assert.Equal(3, snap.LimitThisAct);
        Assert.Equal(2, snap.RemainingThisAct);
    }

    [Fact]
    public void Snapshot_UnlimitedLimit_ReturnsIntMaxRemaining() {
        var t = new SkipBudgetTracker();
        t.RecordSkip();
        var snap = t.Snapshot(-1);
        Assert.Equal(1, snap.UsedThisAct);
        Assert.Equal(-1, snap.LimitThisAct);
        Assert.Equal(int.MaxValue, snap.RemainingThisAct);
    }
}
