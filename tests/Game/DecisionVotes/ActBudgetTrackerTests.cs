using SlayTheStreamer2.Game.DecisionVotes;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

public class ActBudgetTrackerTests {
    [Fact]
    public void IsUseAllowed_LimitZero_AlwaysFalse() {
        var t = new ActBudgetTracker();
        Assert.False(t.IsUseAllowed(0));
    }

    [Fact]
    public void IsUseAllowed_LimitMinusOne_AlwaysTrue() {
        var t = new ActBudgetTracker();
        Assert.True(t.IsUseAllowed(-1));
        t.RecordUse();
        t.RecordUse();
        t.RecordUse();
        Assert.True(t.IsUseAllowed(-1));
    }

    [Fact]
    public void IsUseAllowed_PositiveLimit_TrueUntilExhausted() {
        var t = new ActBudgetTracker();
        Assert.True(t.IsUseAllowed(2));
        t.RecordUse();
        Assert.True(t.IsUseAllowed(2));
        t.RecordUse();
        Assert.False(t.IsUseAllowed(2));
    }

    [Fact]
    public void RecordUse_Increments() {
        var t = new ActBudgetTracker();
        Assert.Equal(0, t.ActUsed);
        t.RecordUse();
        Assert.Equal(1, t.ActUsed);
        t.RecordUse();
        Assert.Equal(2, t.ActUsed);
    }

    [Fact]
    public void ObserveRunAndAct_RunChange_ResetsCounter() {
        var t = new ActBudgetTracker();
        t.ObserveRunAndAct("run-1", 0);
        t.RecordUse();
        Assert.Equal(1, t.ActUsed);
        t.ObserveRunAndAct("run-2", 0);
        Assert.Equal(0, t.ActUsed);
    }

    [Fact]
    public void ObserveRunAndAct_ActChangeSameRun_ResetsCounter() {
        var t = new ActBudgetTracker();
        t.ObserveRunAndAct("run-1", 0);
        t.RecordUse();
        t.RecordUse();
        Assert.Equal(2, t.ActUsed);
        t.ObserveRunAndAct("run-1", 1);
        Assert.Equal(0, t.ActUsed);
    }

    [Fact]
    public void ObserveRunAndAct_IdenticalRunAndAct_DoesNotReset() {
        var t = new ActBudgetTracker();
        t.ObserveRunAndAct("run-1", 0);
        t.RecordUse();
        t.ObserveRunAndAct("run-1", 0);
        Assert.Equal(1, t.ActUsed);
    }

    [Fact]
    public void ObserveRunAndAct_NullRunId_DoesNotResetByRun() {
        var t = new ActBudgetTracker();
        t.ObserveRunAndAct("run-1", 0);
        t.RecordUse();
        t.ObserveRunAndAct(null, 0);   // null run-id (degraded run detection)
        Assert.Equal(1, t.ActUsed);
    }

    [Fact]
    public void ObserveRunAndAct_ReturnsRunChanged_OnNewRun() {
        var t = new ActBudgetTracker();
        t.ObserveRunAndAct("run-1", 0);
        Assert.Equal(BudgetResetReason.RunChanged, t.ObserveRunAndAct("run-2", 0));
    }

    [Fact]
    public void ObserveRunAndAct_ReturnsActChanged_OnActJumpSameRun() {
        var t = new ActBudgetTracker();
        t.ObserveRunAndAct("run-1", 0);
        Assert.Equal(BudgetResetReason.ActChanged, t.ObserveRunAndAct("run-1", 1));
    }

    [Fact]
    public void ObserveRunAndAct_ReturnsNone_OnIdenticalRunAndAct() {
        var t = new ActBudgetTracker();
        t.ObserveRunAndAct("run-1", 0);
        Assert.Equal(BudgetResetReason.None, t.ObserveRunAndAct("run-1", 0));
    }

    [Fact]
    public void ObserveRunAndAct_ReturnsRunChanged_OnFirstObservation() {
        var t = new ActBudgetTracker();
        Assert.Equal(BudgetResetReason.RunChanged, t.ObserveRunAndAct("run-1", 0));
    }

    [Fact]
    public void Snapshot_PositiveLimit_ReturnsCorrectRemaining() {
        var t = new ActBudgetTracker();
        t.RecordUse();
        var snap = t.Snapshot(3);
        Assert.Equal(1, snap.UsedThisAct);
        Assert.Equal(3, snap.LimitThisAct);
        Assert.Equal(2, snap.RemainingThisAct);
    }

    [Fact]
    public void Snapshot_UnlimitedLimit_ReturnsIntMaxRemaining() {
        var t = new ActBudgetTracker();
        t.RecordUse();
        var snap = t.Snapshot(-1);
        Assert.Equal(1, snap.UsedThisAct);
        Assert.Equal(-1, snap.LimitThisAct);
        Assert.Equal(int.MaxValue, snap.RemainingThisAct);
    }
}
