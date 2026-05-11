using System;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// Pure budget arithmetic. Owns the per-act skip counter and run/act change
/// detection. Main-thread-only — plain ++/= state, no Interlocked.
/// Run-id is typed as string? to decouple from the actual sts2.dll RunState.Id type.
/// </summary>
internal sealed class SkipBudgetTracker {
    private int _actSkipsUsed;
    private int? _lastSeenActIndex;
    private string? _lastSeenRunId;

    public int ActSkipsUsed => _actSkipsUsed;

    /// <summary>
    /// Returns the reason the budget was reset, if any. Callers (e.g.,
    /// CardRewardSkipGatePatch.SetRewards postfix) use this to fire a
    /// "card skips reset" chat receipt at the moment of reset.
    /// </summary>
    public BudgetResetReason ObserveRunAndAct(string? runId, int? actIndex) {
        if (runId != null && runId != _lastSeenRunId) {
            _actSkipsUsed = 0;
            _lastSeenRunId = runId;
            _lastSeenActIndex = actIndex;
            return BudgetResetReason.RunChanged;
        }
        if (actIndex.HasValue && actIndex != _lastSeenActIndex) {
            _actSkipsUsed = 0;
            _lastSeenActIndex = actIndex;
            return BudgetResetReason.ActChanged;
        }
        return BudgetResetReason.None;
    }

    public bool IsSkipAllowed(int actLimit) {
        if (actLimit < 0) return true;
        return _actSkipsUsed < actLimit;
    }

    public void RecordSkip() => _actSkipsUsed++;

    public SkipBudgetSnapshot Snapshot(int actLimit) => new(
        UsedThisAct: _actSkipsUsed,
        LimitThisAct: actLimit,
        RemainingThisAct: actLimit < 0 ? int.MaxValue : Math.Max(0, actLimit - _actSkipsUsed));

    internal void ResetForTests() {
        _actSkipsUsed = 0;
        _lastSeenActIndex = null;
        _lastSeenRunId = null;
    }
}

internal readonly record struct SkipBudgetSnapshot(int UsedThisAct, int LimitThisAct, int RemainingThisAct);

internal enum BudgetResetReason {
    None,
    RunChanged,
    ActChanged,
}
