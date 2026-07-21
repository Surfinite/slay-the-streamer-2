using System;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// Pure budget arithmetic. Owns the per-act use counter and run/act change
/// detection. Main-thread-only — plain ++/= state, no Interlocked.
/// Run-id is typed as string? to decouple from the actual sts2.dll RunState.Id type.
/// Generic per-act budget tracker — serves both the card-skip budget and the
/// vote-override budget.
/// </summary>
internal sealed class ActBudgetTracker {
    private int _actUsed;
    private int? _lastSeenActIndex;
    private string? _lastSeenRunId;

    public int ActUsed => _actUsed;

    /// <summary>
    /// Returns the reason the budget was reset, if any. Callers (e.g.,
    /// CardRewardSkipGatePatch.SetRewards postfix) use this to fire a
    /// "card skips reset" chat receipt at the moment of reset.
    /// </summary>
    public BudgetResetReason ObserveRunAndAct(string? runId, int? actIndex) {
        if (runId != null && runId != _lastSeenRunId) {
            _actUsed = 0;
            _lastSeenRunId = runId;
            _lastSeenActIndex = actIndex;
            return BudgetResetReason.RunChanged;
        }
        if (actIndex.HasValue && actIndex != _lastSeenActIndex) {
            _actUsed = 0;
            _lastSeenActIndex = actIndex;
            return BudgetResetReason.ActChanged;
        }
        return BudgetResetReason.None;
    }

    public bool IsUseAllowed(int actLimit) {
        if (actLimit < 0) return true;
        return _actUsed < actLimit;
    }

    public void RecordUse() => _actUsed++;

    public ActBudgetSnapshot Snapshot(int actLimit) => new(
        UsedThisAct: _actUsed,
        LimitThisAct: actLimit,
        RemainingThisAct: actLimit < 0 ? int.MaxValue : Math.Max(0, actLimit - _actUsed));

    internal void ResetForTests() {
        _actUsed = 0;
        _lastSeenActIndex = null;
        _lastSeenRunId = null;
    }

    /// <summary>
    /// Dev-console-only: zero the counter without disturbing run/act memory.
    /// Differs from ResetForTests in that the NEXT ObserveRunAndAct call won't fire
    /// a spurious RunChanged receipt (since we still remember the current run/act).
    /// </summary>
    internal void ResetCounterOnly() {
        _actUsed = 0;
    }
}

internal readonly record struct ActBudgetSnapshot(int UsedThisAct, int LimitThisAct, int RemainingThisAct);

internal enum BudgetResetReason {
    None,
    RunChanged,
    ActChanged,
}
