using System;
using System.Collections.Generic;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// Outcome of planning the A10 second-boss vote round.
/// </summary>
/// <param name="RunVote">True if round 2 should open a real (>=2-option) vote; false to auto-assign without a vote.</param>
/// <param name="OptionSlateIndices">When <see cref="RunVote"/>, the round-1 slate indices to offer (in order). Empty otherwise.</param>
/// <param name="PriorWinnerOptionIndex">When <see cref="RunVote"/>, the position WITHIN <see cref="OptionSlateIndices"/> of the round-1 winner so the popup can mark it ("won R1"); -1 if the winner is not re-offered.</param>
/// <param name="AutoAssignSlateIndex">When NOT <see cref="RunVote"/>, the slate index to set as the second boss directly; -1 means leave vanilla's second boss untouched.</param>
internal sealed record SecondBossRoundPlan(
    bool RunVote,
    IReadOnlyList<int> OptionSlateIndices,
    int PriorWinnerOptionIndex,
    int AutoAssignSlateIndex);

/// <summary>
/// Pure decision logic for the A10 second-boss vote round (task 2a/2b). Works on
/// round-1 slate INDICES only — no Godot/MegaCrit — so it is unit-testable.
/// BossVotePatch maps the result back onto its sampled EncounterModel slate.
///
/// 2a (allowSameBossTwice = false): round 2 offers only the leftovers of the slate,
///     guaranteeing a second boss DIFFERENT from the round-1 winner.
/// 2b (allowSameBossTwice = true):  round 2 re-offers the FULL slate including the
///     round-1 winner (marked via <see cref="SecondBossRoundPlan.PriorWinnerOptionIndex"/>),
///     so chat may pick the same boss twice.
///
/// A round-2 vote never collapses to a single option (no pick-1-of-1): when only one
/// candidate remains it is auto-assigned instead.
/// </summary>
internal static class SecondBossRoundPlanner {
    public static SecondBossRoundPlan Plan(int slateCount, int round1WinnerSlateIndex, bool allowSameBossTwice) {
        if (slateCount < 1) {
            throw new ArgumentOutOfRangeException(nameof(slateCount), slateCount, "slateCount must be >= 1");
        }
        if ((uint)round1WinnerSlateIndex >= (uint)slateCount) {
            throw new ArgumentOutOfRangeException(nameof(round1WinnerSlateIndex), round1WinnerSlateIndex,
                $"round1WinnerSlateIndex must be in [0, {slateCount})");
        }

        if (allowSameBossTwice) {
            // Re-offer the full slate (winner included, marked). Only a real vote if >=2 options.
            if (slateCount < 2) {
                // Single boss exists at all — re-offering it would be a 1-of-1; auto-assign it.
                return new SecondBossRoundPlan(RunVote: false, Array.Empty<int>(), PriorWinnerOptionIndex: -1, AutoAssignSlateIndex: 0);
            }
            var all = new int[slateCount];
            for (int i = 0; i < slateCount; i++) all[i] = i;
            return new SecondBossRoundPlan(RunVote: true, all, PriorWinnerOptionIndex: round1WinnerSlateIndex, AutoAssignSlateIndex: -1);
        }

        // Distinct mode: offer only the leftovers (slate minus the round-1 winner).
        var leftovers = new List<int>(slateCount - 1);
        for (int i = 0; i < slateCount; i++) {
            if (i != round1WinnerSlateIndex) leftovers.Add(i);
        }

        if (leftovers.Count >= 2) {
            return new SecondBossRoundPlan(RunVote: true, leftovers, PriorWinnerOptionIndex: -1, AutoAssignSlateIndex: -1);
        }
        if (leftovers.Count == 1) {
            // Degenerate slate-of-two: one leftover. Auto-assign it; no pick-1-of-1 vote.
            return new SecondBossRoundPlan(RunVote: false, Array.Empty<int>(), PriorWinnerOptionIndex: -1, AutoAssignSlateIndex: leftovers[0]);
        }
        // slateCount == 1: no distinct second boss available — leave vanilla's pick.
        return new SecondBossRoundPlan(RunVote: false, Array.Empty<int>(), PriorWinnerOptionIndex: -1, AutoAssignSlateIndex: -1);
    }
}
