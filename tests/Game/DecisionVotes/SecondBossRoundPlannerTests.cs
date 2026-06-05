using System;
using SlayTheStreamer2.Game.DecisionVotes;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

/// <summary>
/// Tests for the A10 second-boss vote-round planner. Pure index logic — no
/// Godot/MegaCrit. Models the two designs:
///   2a (allowSameBossTwice = false): round 2 offers only the leftovers of the
///       round-1 slate, guaranteeing a DIFFERENT second boss.
///   2b (allowSameBossTwice = true):  round 2 re-offers the FULL slate including
///       the round-1 winner (visually marked), so chat may pick the same boss twice.
/// Degenerate slates (fewer than 2 round-2 options) auto-assign with NO 1-of-1 vote.
/// </summary>
public class SecondBossRoundPlannerTests {
    // --- 2b: allow-same-twice re-offers the full slate, winner marked ---

    [Fact]
    public void AllowSameTwice_SlateOfThree_OffersAllThree_WinnerMarked() {
        var plan = SecondBossRoundPlanner.Plan(slateCount: 3, round1WinnerSlateIndex: 1, allowSameBossTwice: true);
        Assert.True(plan.RunVote);
        Assert.Equal(new[] { 0, 1, 2 }, plan.OptionSlateIndices);
        Assert.Equal(1, plan.PriorWinnerOptionIndex);   // winner is at option position 1
        Assert.Equal(-1, plan.AutoAssignSlateIndex);
    }

    [Fact]
    public void AllowSameTwice_SlateOfTwo_OffersBoth_WinnerMarked() {
        var plan = SecondBossRoundPlanner.Plan(slateCount: 2, round1WinnerSlateIndex: 0, allowSameBossTwice: true);
        Assert.True(plan.RunVote);
        Assert.Equal(new[] { 0, 1 }, plan.OptionSlateIndices);
        Assert.Equal(0, plan.PriorWinnerOptionIndex);
    }

    // --- 2a: distinct mode offers only the leftovers, no marker ---

    [Fact]
    public void Distinct_SlateOfThree_OffersTwoLeftovers_NoMarker() {
        var plan = SecondBossRoundPlanner.Plan(slateCount: 3, round1WinnerSlateIndex: 1, allowSameBossTwice: false);
        Assert.True(plan.RunVote);
        Assert.Equal(new[] { 0, 2 }, plan.OptionSlateIndices);   // index 1 (winner) removed
        Assert.Equal(-1, plan.PriorWinnerOptionIndex);
        Assert.Equal(-1, plan.AutoAssignSlateIndex);
    }

    [Fact]
    public void Distinct_SlateOfThree_WinnerFirst_OffersLastTwo() {
        var plan = SecondBossRoundPlanner.Plan(slateCount: 3, round1WinnerSlateIndex: 0, allowSameBossTwice: false);
        Assert.True(plan.RunVote);
        Assert.Equal(new[] { 1, 2 }, plan.OptionSlateIndices);
    }

    // --- degenerate slate-of-two under distinct: 1 leftover -> auto-assign, no vote ---

    [Fact]
    public void Distinct_SlateOfTwo_WinnerZero_AutoAssignsLeftoverOne() {
        var plan = SecondBossRoundPlanner.Plan(slateCount: 2, round1WinnerSlateIndex: 0, allowSameBossTwice: false);
        Assert.False(plan.RunVote);
        Assert.Empty(plan.OptionSlateIndices);
        Assert.Equal(1, plan.AutoAssignSlateIndex);
    }

    [Fact]
    public void Distinct_SlateOfTwo_WinnerOne_AutoAssignsLeftoverZero() {
        var plan = SecondBossRoundPlanner.Plan(slateCount: 2, round1WinnerSlateIndex: 1, allowSameBossTwice: false);
        Assert.False(plan.RunVote);
        Assert.Equal(0, plan.AutoAssignSlateIndex);
    }

    // --- defensive: slate of one (only one boss exists at all) never runs a 1-of-1 vote ---

    [Fact]
    public void AllowSameTwice_SlateOfOne_AutoAssignsThatBoss() {
        var plan = SecondBossRoundPlanner.Plan(slateCount: 1, round1WinnerSlateIndex: 0, allowSameBossTwice: true);
        Assert.False(plan.RunVote);
        Assert.Equal(0, plan.AutoAssignSlateIndex);
    }

    [Fact]
    public void Distinct_SlateOfOne_NoDistinctSecondAvailable_KeepsVanilla() {
        var plan = SecondBossRoundPlanner.Plan(slateCount: 1, round1WinnerSlateIndex: 0, allowSameBossTwice: false);
        Assert.False(plan.RunVote);
        Assert.Equal(-1, plan.AutoAssignSlateIndex);   // -1 => caller leaves vanilla's second boss
    }

    // --- input guards ---

    [Fact]
    public void WinnerIndexOutOfRange_Throws() {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SecondBossRoundPlanner.Plan(slateCount: 3, round1WinnerSlateIndex: 3, allowSameBossTwice: true));
    }

    [Fact]
    public void NegativeSlateCount_Throws() {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SecondBossRoundPlanner.Plan(slateCount: 0, round1WinnerSlateIndex: 0, allowSameBossTwice: true));
    }
}
